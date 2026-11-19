using Koh.Emulator.App;

string? dapPipe = null;
bool headless = false;
bool engineSession = false;
string? screenshotPath = null;
int frames = 120;
string? inputScriptPath = null;
bool mode3ReportRequested = false;
string? mode3ReportPath = null;
string? bootRomPath = null;
string? bootRomDmgPath = null;
string? bootRomCgbPath = null;
var positional = new List<string>();
foreach (var arg in args)
{
    if (arg == "--headless")
        headless = true;
    else if (arg == "--session")
        engineSession = true;
    else if (arg.StartsWith("--boot-rom=", StringComparison.Ordinal))
        bootRomPath = arg["--boot-rom=".Length..];
    else if (arg.StartsWith("--boot-rom-dmg=", StringComparison.Ordinal))
        bootRomDmgPath = arg["--boot-rom-dmg=".Length..];
    else if (arg.StartsWith("--boot-rom-cgb=", StringComparison.Ordinal))
        bootRomCgbPath = arg["--boot-rom-cgb=".Length..];
    else if (arg.StartsWith("--dap=", StringComparison.Ordinal))
        dapPipe = arg["--dap=".Length..];
    else if (arg.StartsWith("--screenshot=", StringComparison.Ordinal))
        screenshotPath = arg["--screenshot=".Length..];
    else if (arg.StartsWith("--frames=", StringComparison.Ordinal))
        frames = int.Parse(arg["--frames=".Length..]);
    else if (arg.StartsWith("--input=", StringComparison.Ordinal))
        inputScriptPath = arg["--input=".Length..];
    else if (arg == "--mode3-report")
        mode3ReportRequested = true;
    else if (arg.StartsWith("--mode3-report=", StringComparison.Ordinal))
    {
        mode3ReportRequested = true;
        mode3ReportPath = arg["--mode3-report=".Length..];
    }
    else
        positional.Add(arg);
}

var bootRoms = new BootRomResolver(bootRomPath, bootRomDmgPath, bootRomCgbPath);
if (engineSession)
{
    if (
        positional.Count > 1
        || screenshotPath is not null
        || mode3ReportRequested
        || inputScriptPath is not null
    )
    {
        Console.Error.WriteLine(
            "--session accepts one ROM and cannot use screenshot, report or input-script modes"
        );
        return 1;
    }
    using var sessionCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        sessionCancellation.Cancel();
    };
    AudioSink? sessionAudio = null;
    if (!headless)
    {
        try
        {
            sessionAudio = new AudioSink();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koh-engine] audio unavailable: {ex.Message}");
        }
    }
    using var sessionAudioLifetime = sessionAudio;
    using var sessionLoop = new EmulatorLoop(sessionAudio);
    var session = new EngineSession(sessionLoop, bootRoms);
    try
    {
        if (positional.Count == 1)
            sessionLoop.Execute(() => session.Load(positional[0], dapPipe is not null));
        using var sessionDap = dapPipe is not null ? new DapServerHost(dapPipe, sessionLoop) : null;
        if (sessionDap is not null)
            _ = sessionDap.Completion.ContinueWith(
                _ =>
                {
                    try
                    {
                        sessionCancellation.Cancel();
                    }
                    catch (ObjectDisposedException) { }
                },
                TaskScheduler.Default
            );
        using var input = new StreamReader(
            Console.OpenStandardInput(),
            new System.Text.UTF8Encoding(false, true)
        );
        return await session.RunAsync(
            input,
            Console.OpenStandardOutput(),
            sessionCancellation.Token
        );
    }
    catch (OperationCanceledException)
    {
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[koh-engine] {ex.Message}");
        return 1;
    }
}
if (positional.Count != 1)
{
    Console.Error.WriteLine(
        "Use koh-emulator for the desktop, or pass one ROM with --session, --dap, --screenshot or --mode3-report."
    );
    return 1;
}
string romPath = positional[0];
if (screenshotPath is not null || mode3ReportRequested)
{
    return HeadlessRunner.Run(
        romPath,
        screenshotPath,
        frames,
        inputScriptPath,
        mode3ReportRequested,
        mode3ReportPath,
        bootRoms
    );
}
if (dapPipe is null)
{
    Console.Error.WriteLine(
        "Use koh-emulator for the desktop, or select --session, --dap, --screenshot or --mode3-report."
    );
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
AudioSink? audio = null;
if (!headless)
{
    try
    {
        audio = new AudioSink();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[koh-dap] audio unavailable: {ex.Message}");
    }
}
using var audioLifetime = audio;
using var loop = new EmulatorLoop(audio);
var dapSession = new EngineSession(loop, bootRoms);
try
{
    loop.Execute(() => dapSession.Load(romPath, paused: true));
    using var dapServer = new DapServerHost(dapPipe, loop);
    await dapServer.Completion.WaitAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[koh-dap] {ex.Message}");
    return 1;
}
