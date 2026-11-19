using System.Diagnostics;
using System.Runtime.InteropServices;
using Koh.Emulator.App;

namespace Koh.Debugger.Tests;

public class AudioSinkTests
{
    [Test]
    public async Task Reset_AllowsFrameSizedAudioToWarmUpAgain()
    {
        if (Environment.GetEnvironmentVariable("KOH_AUDIO_TEST_CHILD") == "1")
        {
            using var sink = new AudioSink();
            var frame = new short[738];
            for (int session = 0; session < 3; session++)
            {
                if (session > 0)
                    sink.Reset();
                for (int index = 0; index < 9; index++)
                    sink.Push(frame);
                await Assert.That(sink.Buffered()).IsGreaterThan(frame.Length * 2);
            }
            return;
        }
        string directory = Path.Combine(Path.GetTempPath(), "koh-audio-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"
        )
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(AudioSinkTests).Assembly.Location);
        start.ArgumentList.Add("--treenode-filter");
        start.ArgumentList.Add("/*/*/AudioSinkTests/*");
        start.ArgumentList.Add("--results-directory");
        start.ArgumentList.Add(directory);
        start.Environment["ALSOFT_DRIVERS"] = "null";
        start.Environment["KOH_AUDIO_TEST_CHILD"] = "1";
        if (OperatingSystem.IsLinux())
        {
            string native = Path.Combine(
                AppContext.BaseDirectory,
                "runtimes",
                RuntimeInformation.RuntimeIdentifier,
                "native"
            );
            string? previous = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            start.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(previous)
                ? native
                : native + Path.PathSeparator + previous;
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string report = await output + await errors;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(report);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}
