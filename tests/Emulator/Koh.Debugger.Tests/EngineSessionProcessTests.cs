using System.Diagnostics;
using System.Text.Json;

namespace Koh.Debugger.Tests;

public class EngineSessionProcessTests
{
    [Test]
    [Arguments(false, "eof")]
    [Arguments(true, "quit")]
    [Arguments(false, "oversized")]
    public async Task WindowlessSession_LoadsSnapshotsAndPersistsWhilePaused(
        bool cgb,
        string ending
    )
    {
        string directory = Path.Combine(Path.GetTempPath(), "koh-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "game.gb");
        var rom = TestRom.Create();
        rom[0x143] = cgb ? (byte)0x80 : (byte)0;
        rom[0x147] = 3;
        rom[0x149] = 2;
        rom[0x100] = 0xc3;
        rom[0x101] = 0x50;
        rom[0x102] = 1;
        new byte[]
        {
            0xaf,
            0xe0,
            0,
            0x3e,
            0x0a,
            0xea,
            0,
            0,
            0x3e,
            0x42,
            0xea,
            0,
            0xa0,
            0x18,
            0xfe,
        }.CopyTo(rom, 0x150);
        TestRom.WithValidHeader(rom);
        await File.WriteAllBytesAsync(path, rom);
        var start = EmulatorProcess.StartInfo("--session", "--headless");
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long sequence = 0;
        async Task<JsonElement> Request(object request)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            await process.StandardInput.FlushAsync();
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
                throw new InvalidOperationException("Engine closed stdout: " + await errors);
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        try
        {
            var empty = await Request(new { id = ++sequence, command = "snapshot" });
            await Assert.That(empty.GetProperty("ok").GetBoolean()).IsTrue();
            await Assert.That(empty.GetProperty("id").GetInt64()).IsEqualTo(sequence);
            await Assert
                .That(empty.GetProperty("snapshot").GetProperty("romPath").ValueKind)
                .IsEqualTo(JsonValueKind.Null);
            var loaded = await Request(
                new
                {
                    id = ++sequence,
                    command = "load",
                    path,
                    paused = true,
                }
            );
            await Assert.That(loaded.GetProperty("ok").GetBoolean()).IsTrue();
            var snapshot = loaded.GetProperty("snapshot");
            await Assert.That(snapshot.GetProperty("paused").GetBoolean()).IsTrue();
            await Assert
                .That(snapshot.GetProperty("framebuffer").GetBytesFromBase64().Length)
                .IsEqualTo(160 * 144 * 4);
            var saved = await Request(new { id = ++sequence, command = "save" });
            await Assert.That(saved.GetProperty("ok").GetBoolean()).IsTrue();
            await Assert.That(File.Exists(path + ".state")).IsTrue();
            var restored = await Request(new { id = ++sequence, command = "restore" });
            await Assert.That(restored.GetProperty("ok").GetBoolean()).IsTrue();
            var missing = await Request(
                new
                {
                    id = ++sequence,
                    command = "load",
                    path = path + ".missing",
                }
            );
            await Assert.That(missing.GetProperty("ok").GetBoolean()).IsFalse();
            var badShape = await Request(
                new
                {
                    id = ++sequence,
                    command = "reset",
                    debug = "invalid",
                }
            );
            await Assert.That(badShape.GetProperty("ok").GetBoolean()).IsFalse();
            var invalidAddress = await Request(
                new
                {
                    id = ++sequence,
                    command = "snapshot",
                    address = 65536,
                }
            );
            await Assert.That(invalidAddress.GetProperty("ok").GetBoolean()).IsFalse();
            var inspected = await Request(
                new
                {
                    id = ++sequence,
                    command = "snapshot",
                    debug = true,
                    address = 0xfff0,
                }
            );
            snapshot = inspected.GetProperty("snapshot");
            await Assert.That(snapshot.GetProperty("romPath").GetString()).IsEqualTo(path);
            await Assert.That(snapshot.GetProperty("frame").GetInt64()).IsEqualTo(0);
            await Assert
                .That(snapshot.GetProperty("memory").GetProperty("baseAddress").GetInt32())
                .IsEqualTo(0xfff0);
            await Assert
                .That(
                    snapshot.GetProperty("memory").GetProperty("bytes").GetBytesFromBase64().Length
                )
                .IsEqualTo(256);
            await Assert
                .That(snapshot.GetProperty("vram").GetProperty("height").GetInt32())
                .IsEqualTo(cgb ? 384 : 192);
            var bad = await Request(
                new
                {
                    id = ++sequence,
                    command = "joypad",
                    button = "invalid",
                    down = true,
                }
            );
            await Assert.That(bad.GetProperty("ok").GetBoolean()).IsFalse();
            var stepped = await Request(new { id = ++sequence, command = "step" });
            await Assert
                .That(
                    stepped
                        .GetProperty("snapshot")
                        .GetProperty("cpu")
                        .GetProperty("totalTCycles")
                        .GetUInt64()
                )
                .IsGreaterThan(0ul);
            await File.WriteAllBytesAsync(
                path + ".state",
                (await File.ReadAllBytesAsync(path + ".state"))[..100]
            );
            var corrupt = await Request(new { id = ++sequence, command = "restore" });
            await Assert.That(corrupt.GetProperty("ok").GetBoolean()).IsFalse();
            var unchanged = await Request(new { id = ++sequence, command = "snapshot" });
            await Assert
                .That(unchanged.GetProperty("snapshot").GetProperty("cpu").ToString())
                .IsEqualTo(stepped.GetProperty("snapshot").GetProperty("cpu").ToString());
            await Request(
                new
                {
                    id = ++sequence,
                    command = "fastForward",
                    enabled = true,
                }
            );
            await Request(new { id = ++sequence, command = "resume" });
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                snapshot = (
                    await Request(
                        new
                        {
                            id = ++sequence,
                            command = "snapshot",
                            debug = true,
                            address = 0xa000,
                        }
                    )
                ).GetProperty("snapshot");
            } while (
                snapshot.GetProperty("memory").GetProperty("bytes").GetBytesFromBase64()[0] != 0x42
            );
            var stopped = await Request(new { id = ++sequence, command = "pause" });
            long frames = stopped.GetProperty("snapshot").GetProperty("frame").GetInt64();
            await Assert.That(frames).IsGreaterThan(0L);
            var pressed = await Request(
                new
                {
                    id = ++sequence,
                    command = "joypad",
                    button = "A",
                    down = true,
                    debug = true,
                    address = 0xff00,
                }
            );
            await Assert
                .That(
                    pressed
                        .GetProperty("snapshot")
                        .GetProperty("memory")
                        .GetProperty("bytes")
                        .GetBytesFromBase64()[0] & 1
                )
                .IsEqualTo(0);
            var released = await Request(
                new
                {
                    id = ++sequence,
                    command = "releaseInput",
                    debug = true,
                    address = 0xff00,
                }
            );
            await Assert
                .That(
                    released
                        .GetProperty("snapshot")
                        .GetProperty("memory")
                        .GetProperty("bytes")
                        .GetBytesFromBase64()[0] & 1
                )
                .IsEqualTo(1);
            snapshot = (await Request(new { id = ++sequence, command = "snapshot" })).GetProperty(
                "snapshot"
            );
            await Assert.That(snapshot.GetProperty("frame").GetInt64()).IsEqualTo(frames);
            await Assert.That(snapshot.GetProperty("fastForward").GetBoolean()).IsFalse();
            if (ending == "quit")
                await Request(new { id = ++sequence, command = "quit" });
            if (ending == "oversized")
            {
                await process.StandardInput.WriteLineAsync(new string(' ', 16385));
                await process.StandardInput.FlushAsync();
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await Assert.That(process.ExitCode).IsEqualTo(ending == "oversized" ? 1 : 0);
            await Assert
                .That((await errors).Contains("graphics", StringComparison.OrdinalIgnoreCase))
                .IsFalse();
            await Assert.That(await process.StandardOutput.ReadToEndAsync()).IsEqualTo("");
            await Assert
                .That((await File.ReadAllBytesAsync(path + ".sav"))[0])
                .IsEqualTo((byte)0x42);
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
