using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Linker.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Debugger.Tests;

public class StepHandlersTests
{
    private static (DapDispatcher dispatcher, DebugSession session) MakeSessionWithProgram(params byte[] program)
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        Array.Copy(program, 0, rom, 0x0100, program.Length);

        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        session.Launch(rom, Array.Empty<byte>(), Koh.Emulator.Core.HardwareMode.Dmg);
        HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());
        return (dispatcher, session);
    }

    private static byte[] Encode(int seq, string command) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
            ["arguments"] = new { },
        });

    [Test]
    public async Task StepIn_Advances_One_Instruction()
    {
        // NOP ; LD A,$42
        var (dispatcher, session) = MakeSessionWithProgram(0x00, 0x3E, 0x42);
        dispatcher.HandleRequest(Encode(1, "stepIn"));

        await Assert.That(session.System!.Registers.Pc).IsEqualTo((ushort)0x0101);
    }

    [Test]
    public async Task Next_Steps_Over_Call()
    {
        // $0100: CALL $0200
        // $0103: LD A,$42
        // $0200: RET
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0xCD; rom[0x101] = 0x00; rom[0x102] = 0x02;  // CALL $0200
        rom[0x103] = 0x3E; rom[0x104] = 0x42;                     // LD A,$42
        rom[0x200] = 0xC9;                                         // RET

        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        session.Launch(rom, Array.Empty<byte>(), Koh.Emulator.Core.HardwareMode.Dmg);
        HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());

        dispatcher.HandleRequest(Encode(1, "next"));

        // next should leave us AT $0103 (just past the CALL, not inside $0200).
        await Assert.That(session.System!.Registers.Pc).IsEqualTo((ushort)0x0103);
    }

    [Test]
    public async Task StepOut_Runs_Until_Return()
    {
        // $0100: CALL $0200
        // $0103: NOP
        // $0200: NOP ; NOP ; RET
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0xCD; rom[0x101] = 0x00; rom[0x102] = 0x02;
        rom[0x103] = 0x00;
        rom[0x200] = 0x00;
        rom[0x201] = 0x00;
        rom[0x202] = 0xC9;

        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        session.Launch(rom, Array.Empty<byte>(), Koh.Emulator.Core.HardwareMode.Dmg);
        HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());

        // Step into the CALL first so we're inside the function.
        dispatcher.HandleRequest(Encode(1, "stepIn"));
        await Assert.That(session.System!.Registers.Pc).IsEqualTo((ushort)0x0200);

        // Now step-out should run the NOP ; NOP ; RET and land at $0103.
        dispatcher.HandleRequest(Encode(2, "stepOut"));
        await Assert.That(session.System!.Registers.Pc).IsEqualTo((ushort)0x0103);
    }

    [Test]
    public async Task StackTrace_Returns_At_Least_Current_Pc_Frame()
    {
        var (dispatcher, session) = MakeSessionWithProgram(0x00);
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        dispatcher.HandleRequest(Encode(1, "stackTrace"));

        using var doc = JsonDocument.Parse(responses[0]);
        var body = doc.RootElement.GetProperty("body");
        var frames = body.GetProperty("stackFrames");
        await Assert.That(frames.GetArrayLength()).IsGreaterThanOrEqualTo(1);
        string firstName = frames[0].GetProperty("name").GetString() ?? "";
        await Assert.That(firstName).IsEqualTo("$0100");
    }

    [Test]
    public async Task StackTrace_Includes_Source_When_DebugInfo_Maps_Current_Pc()
    {
        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(bank: 0, address: 0x0100, byteCount: 1,
            sourceFile: "src/main.asm", line: 12);
        using var kdbgStream = new MemoryStream();
        KdbgFileWriter.Write(kdbgStream, builder);

        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0x00;

        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        session.Launch(rom, kdbgStream.ToArray(), Koh.Emulator.Core.HardwareMode.Dmg);
        HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());

        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        dispatcher.HandleRequest(Encode(1, "stackTrace"));

        using var doc = JsonDocument.Parse(responses[0]);
        var frame = doc.RootElement.GetProperty("body").GetProperty("stackFrames")[0];
        await Assert.That(frame.GetProperty("line").GetInt32()).IsEqualTo(12);
        await Assert.That(frame.GetProperty("source").GetProperty("path").GetString()).IsEqualTo("src/main.asm");
    }
}
