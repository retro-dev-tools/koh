using Koh.Emulator.App;
using KohUI;
using KohUI.Backends.Sdl3;

string? dapPipe = null;
bool headless = false;
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

string romPath = positional.Count > 0 ? positional[0] : FindDefaultRom();
EmulatorApp.BootRoms = new BootRomResolver(bootRomPath, bootRomDmgPath, bootRomCgbPath);
if (headless && dapPipe is null && screenshotPath is null && !mode3ReportRequested)
{
    Console.Error.WriteLine("--headless requires --dap, --screenshot or --mode3-report");
    return 1;
}

if (screenshotPath is not null || mode3ReportRequested)
{
    return HeadlessRunner.Run(
        romPath,
        screenshotPath,
        frames,
        inputScriptPath,
        mode3ReportRequested,
        mode3ReportPath,
        EmulatorApp.BootRoms
    );
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
    catch (Exception ex) when (dapPipe is not null)
    {
        Console.Error.WriteLine($"[koh-dap] audio unavailable: {ex.Message}");
    }
}
using var audioLifetime = audio;
using var loop = new EmulatorLoop(audio);
var loaded = EmulatorApp.LoadRomFromDisk(romPath);
if (loaded is LoadRomFailed failure)
{
    Console.Error.WriteLine(failure.Error);
    return 1;
}
if (loaded is LoadRomSucceeded success)
    loop.SetSystem(success.System, success.Path);
using var dapServer = dapPipe is not null ? new DapServerHost(dapPipe, loop) : null;
if (dapServer is not null)
    _ = dapServer.Completion.ContinueWith(
        _ =>
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException) { }
        },
        TaskScheduler.Default
    );
else
    loop.Resume();

if (headless)
{
    await WaitForSessionEnd();
    return 0;
}

var runner = new Runner<EmulatorModel, EmulatorMsg>(
    initialModel: new EmulatorModel(
        Loop: loop,
        RomPath: romPath,
        FrameCount: 0,
        Status: $"Loaded {Path.GetFileName(romPath)}",
        ShowDebug: false
    ),
    update: EmulatorApp.Update,
    view: EmulatorApp.View
);

var backend = new Sdl3Backend<EmulatorModel, EmulatorMsg>(
    runner,
    title: "Koh Emulator",
    onTick: () => new Tick(),
    onKeyDown: name =>
        name == "Tab"
            ? new SetFastForward(true)
            : EmulatorApp.MapShortcut(name)
                ?? (EmulatorApp.MapKey(name) is { } btn ? new JoypadDown(btn) : null),
    onKeyUp: name =>
        name == "Tab"
            ? new SetFastForward(false)
            : (EmulatorApp.MapKey(name) is { } btn ? new JoypadUp(btn) : null)
);

try
{
    backend.Run(cancellation.Token);
}
catch (Exception ex) when (dapServer is not null)
{
    Console.Error.WriteLine($"[koh-dap] graphics unavailable; continuing headlessly: {ex.Message}");
    await WaitForSessionEnd();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[koh-emulator] graphics failed: {ex.Message}");
    return 1;
}
finally
{
    await runner.DisposeAsync();
}

return 0;

async Task WaitForSessionEnd()
{
    try
    {
        await dapServer!.Completion.WaitAsync(cancellation.Token);
    }
    catch (OperationCanceledException) { }
}

static string FindDefaultRom()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        var candidate = Path.Combine(dir, ".playwright-cli", "azure-dreams.gbc");
        if (File.Exists(candidate))
            return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        var candidate = Path.Combine(
            dir,
            "tests",
            "fixtures",
            "test-roms",
            "blargg",
            "cpu_instrs",
            "cpu_instrs.gb"
        );
        if (File.Exists(candidate))
            return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    throw new FileNotFoundException(
        "no default ROM found. Pass a path as arg1 or place azure-dreams.gbc under .playwright-cli/."
    );
}
