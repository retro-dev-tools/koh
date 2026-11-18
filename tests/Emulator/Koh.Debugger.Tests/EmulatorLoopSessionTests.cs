using Koh.Emulator;
using Koh.Emulator.App;
using Koh.Emulator.Cartridge;

namespace Koh.Debugger.Tests;

public class EmulatorLoopSessionTests
{
    [Test]
    public async Task PausedLoop_ExecutesCommandsWithoutAdvancingTheCpu()
    {
        using var loop = new EmulatorLoop();
        var system = new GameBoySystem(CartridgeFactory.Load(TestRom.Create()));
        loop.SetSystem(system);
        ulong cycles = system.Cpu.TotalTCycles;
        var completed = new TaskCompletionSource<byte>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        loop.Send(sys =>
        {
            sys.DebugWriteByte(0xc000, 0x42);
            completed.SetResult(sys.DebugReadByte(0xc000));
        });
        await Assert
            .That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)))
            .IsEqualTo((byte)0x42);
        await Assert.That(system.Cpu.TotalTCycles).IsEqualTo(cycles);
        await Assert.That(loop.FrameCount).IsEqualTo(0);
    }

    [Test]
    public async Task PausedLoop_SaveAndLoadCompleteBeforeReturning()
    {
        string directory = Path.Combine(Path.GetTempPath(), "koh-state-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var loop = new EmulatorLoop();
            var system = new GameBoySystem(CartridgeFactory.Load(TestRom.Create()));
            loop.SetSystem(system);
            system.DebugWriteByte(0xc000, 0x42);
            string path = Path.Combine(directory, "game.state");
            loop.SaveState(path);
            await Assert.That(File.Exists(path)).IsTrue();
            system.DebugWriteByte(0xc000, 0x19);
            loop.LoadState(path);
            await Assert.That(system.DebugReadByte(0xc000)).IsEqualTo((byte)0x42);
            await Assert.That(loop.IsPaused).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
