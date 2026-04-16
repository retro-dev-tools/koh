using Koh.Debugger.Dap.Handlers;
using Koh.Debugger.Dap.Messages;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using System.Text.Json;

namespace Koh.Debugger.Tests;

public class WriteMemoryHandlerTests
{
    private static (DebugSession session, WriteMemoryHandler handler) Make()
    {
        var session = new DebugSession();
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0x18; rom[0x101] = 0xFE;
        session.Launch(rom, Array.Empty<byte>(), HardwareMode.Dmg);
        return (session, new WriteMemoryHandler(session));
    }

    private static Koh.Debugger.Dap.Messages.Request MakeReq(object args)
    {
        var json = JsonSerializer.SerializeToElement(args);
        return new Koh.Debugger.Dap.Messages.Request { Seq = 1, Arguments = json };
    }

    [Test]
    public async Task WriteMemory_Writes_Bytes_To_Mmu()
    {
        var (session, handler) = Make();
        var gb = session.System!;
        var data = Convert.ToBase64String(new byte[] { 0xAA, 0xBB, 0xCC });

        var resp = handler.Handle(MakeReq(new
        {
            memoryReference = "$C100",
            offset = 0,
            allowPartial = false,
            data,
        }));

        await Assert.That(resp.Success).IsTrue();
        await Assert.That(gb.Mmu.ReadByte(0xC100)).IsEqualTo((byte)0xAA);
        await Assert.That(gb.Mmu.ReadByte(0xC101)).IsEqualTo((byte)0xBB);
        await Assert.That(gb.Mmu.ReadByte(0xC102)).IsEqualTo((byte)0xCC);
    }

    [Test]
    public async Task WriteMemory_Rejects_Bad_MemoryReference()
    {
        var (_, handler) = Make();
        var resp = handler.Handle(MakeReq(new
        {
            memoryReference = "not-an-address",
            offset = 0,
            data = Convert.ToBase64String(new byte[] { 1 }),
        }));
        await Assert.That(resp.Success).IsFalse();
    }

    [Test]
    public async Task WriteMemory_Rejects_Invalid_Base64()
    {
        var (_, handler) = Make();
        var resp = handler.Handle(MakeReq(new
        {
            memoryReference = "$C100",
            offset = 0,
            data = "!!!not-base64!!!",
        }));
        await Assert.That(resp.Success).IsFalse();
    }
}
