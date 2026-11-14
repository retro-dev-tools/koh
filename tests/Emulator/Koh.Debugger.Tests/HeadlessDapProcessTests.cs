using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Koh.Emulator.App;

namespace Koh.Debugger.Tests;

public class HeadlessDapProcessTests
{
    [Test]
    [Arguments("disconnect", true)]
    [Arguments("terminate", true)]
    [Arguments("eof", true)]
    [Arguments("disconnect", false)]
    public async Task HeadlessSession_DebugsWithoutDisplayAndExits(string ending, bool headless)
    {
        string directory = Path.Combine(Path.GetTempPath(), "koh-dap-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string romPath = Path.Combine(directory, "game.gb");
        var rom = TestRom.Create();
        rom[0x100] = 0xC3;
        rom[0x101] = 0x50;
        rom[0x102] = 0x01;
        rom[0x150] = 0x3E;
        rom[0x151] = 0x42;
        rom[0x152] = 0x18;
        rom[0x153] = 0xFC;
        await File.WriteAllBytesAsync(romPath, rom);
        string pipeName = "koh-test-" + Guid.NewGuid();
        string testAssembly = typeof(HeadlessDapProcessTests).Assembly.Location;
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            string argument in new[]
            {
                "exec",
                "--runtimeconfig",
                Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                "--depsfile",
                Path.ChangeExtension(testAssembly, ".deps.json"),
                typeof(EmulatorLoop).Assembly.Location,
                romPath,
                "--dap=" + pipeName,
            }
        )
            start.ArgumentList.Add(argument);
        if (headless)
            start.ArgumentList.Add("--headless");
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        start.Environment["ALSOFT_DRIVERS"] = "unavailable";
        start.Environment["SDL_VIDEODRIVER"] = "unavailable";
        start.Environment["SDL_RENDER_DRIVER"] = "unavailable";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );
            await pipe.ConnectAsync(timeout.Token);
            int sequence = 0;
            var events = new Queue<JsonElement>();
            async Task<JsonElement> Request(string command, object arguments)
            {
                int requestSequence = ++sequence;
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        seq = requestSequence,
                        type = "request",
                        command,
                        arguments,
                    }
                );
                await pipe.WriteAsync(
                    Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"),
                    timeout.Token
                );
                await pipe.WriteAsync(body, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
                while (true)
                {
                    var message = await ReadMessage(pipe, timeout.Token);
                    if (message.GetProperty("type").GetString() != "response")
                    {
                        events.Enqueue(message);
                        continue;
                    }
                    if (message.GetProperty("request_seq").GetInt32() != requestSequence)
                        continue;
                    if (!message.GetProperty("success").GetBoolean())
                        throw new InvalidOperationException(message.ToString());
                    return message;
                }
            }
            await Request("initialize", new { adapterID = "koh" });
            await Request("launch", new { stopOnEntry = true });
            await Request("configurationDone", new { });
            var stack = await Request("stackTrace", new { threadId = 1 });
            await Assert
                .That(stack.GetProperty("body").GetProperty("stackFrames").GetArrayLength())
                .IsGreaterThan(0);
            await Request(
                "setInstructionBreakpoints",
                new { breakpoints = new[] { new { instructionReference = "0x0150" } } }
            );
            await Request("continue", new { threadId = 1 });
            JsonElement stopped;
            do
            {
                stopped = events.TryDequeue(out var queued)
                    ? queued
                    : await ReadMessage(pipe, timeout.Token);
            } while (
                !stopped.TryGetProperty("event", out var evt)
                || evt.GetString() != "stopped"
                || stopped.GetProperty("body").GetProperty("reason").GetString() != "breakpoint"
            );
            await Request("stepIn", new { threadId = 1 });
            var stepped = await Request("stackTrace", new { threadId = 1 });
            await Assert
                .That(
                    stepped
                        .GetProperty("body")
                        .GetProperty("stackFrames")[0]
                        .GetProperty("instructionPointerReference")
                        .GetString()
                )
                .IsEqualTo("0x0152");
            var memory = await Request("readMemory", new { memoryReference = "0x0150", count = 2 });
            await Assert
                .That(memory.GetProperty("body").GetProperty("data").GetString())
                .IsEqualTo("PkI=");
            if (ending == "eof")
                pipe.Dispose();
            else
                await Request(ending, new { });
            await process.WaitForExitAsync(timeout.Token);
            string errors = await stderr;
            await stdout;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Emulator exit {process.ExitCode}: {errors}");
            if (headless)
            {
                await Assert
                    .That(errors.Contains("graphics unavailable", StringComparison.Ordinal))
                    .IsFalse();
                await Assert
                    .That(errors.Contains("audio unavailable", StringComparison.Ordinal))
                    .IsFalse();
            }
            else
                await Assert
                    .That(errors.Contains("graphics unavailable", StringComparison.Ordinal))
                    .IsTrue();
        }
        catch (Exception ex) when (process.HasExited)
        {
            throw new InvalidOperationException(
                $"Emulator exit {process.ExitCode}: {await stderr}",
                ex
            );
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

    private static async Task<JsonElement> ReadMessage(Stream stream, CancellationToken token)
    {
        var header = new StringBuilder();
        byte[] single = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(single, token);
            header.Append((char)single[0]);
        }
        int length = int.Parse(header.ToString().Split(':', 2)[1].Trim());
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
