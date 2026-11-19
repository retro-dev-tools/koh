using System.Diagnostics;
using Koh.Native;

namespace Koh.Lsp.Tests;

public class NativeClientTests
{
    [Test]
    public async Task UnreadLargeRequestTimesOutAndClientCanRestart()
    {
        var start = new ProcessStartInfo();
        if (OperatingSystem.IsWindows())
        {
            start.FileName = "powershell.exe";
            foreach (
                var argument in new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "Start-Sleep -Seconds 30",
                }
            )
                start.ArgumentList.Add(argument);
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("exec sleep 30");
        }
        using var client = new NativeClient(start, TimeSpan.FromMilliseconds(250));
        var sources = new[] { new NativeSource("large.asm", new string(' ', 4 * 1024 * 1024)) };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            var timedOut = false;
            try
            {
                client.Request("parse", sources, ["large.asm"]);
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }
            await Assert.That(timedOut).IsTrue();
            // Well under the child's 30 s sleep: the client gave up instead of waiting for it.
            await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(15));
        }
    }
}
