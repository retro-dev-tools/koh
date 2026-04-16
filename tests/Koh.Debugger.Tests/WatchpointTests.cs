using Koh.Debugger.Session;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Debugger.Tests;

public class WatchpointTests
{
    private static (DebugSession session, GameBoySystem gb) Make()
    {
        var session = new DebugSession();
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0x18; rom[0x101] = 0xFE;
        session.Launch(rom, Array.Empty<byte>(), HardwareMode.Dmg);
        return (session, session.System!);
    }

    [Test]
    public async Task WriteWatchpoint_Fires_On_Matching_Address()
    {
        var (session, gb) = Make();
        session.Watchpoints.Write[0xC100] = new WatchpointInfo("$C100", "write");

        gb.Mmu.WriteByte(0xC099, 0x11);     // unrelated — no trip
        await Assert.That(session.PauseRequested).IsFalse();

        gb.Mmu.WriteByte(0xC100, 0x22);
        await Assert.That(session.PauseRequested).IsTrue();
    }

    [Test]
    public async Task ReadWatchpoint_Fires_On_Matching_Address()
    {
        var (session, gb) = Make();
        session.Watchpoints.Read[0xC200] = new WatchpointInfo("$C200", "read");

        _ = gb.Mmu.ReadByte(0xC100);
        await Assert.That(session.PauseRequested).IsFalse();

        _ = gb.Mmu.ReadByte(0xC200);
        await Assert.That(session.PauseRequested).IsTrue();
    }

    [Test]
    public async Task Clear_Removes_All_Registered_Watchpoints()
    {
        var (session, gb) = Make();
        session.Watchpoints.Write[0xC100] = new WatchpointInfo("$C100", "write");
        session.Watchpoints.Clear();

        gb.Mmu.WriteByte(0xC100, 0x42);
        await Assert.That(session.PauseRequested).IsFalse();
    }
}
