using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;

namespace Koh.Debugger.Tests;

public class EvaluateHandlerTests
{
    private static (DapDispatcher, DebugSession, List<byte[]>) Build()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0x3E; rom[0x101] = 0x42;  // LD A,$42

        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());
        session.Launch(rom, Array.Empty<byte>(), Koh.Emulator.Core.HardwareMode.Dmg);
        HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());
        return (dispatcher, session, responses);
    }

    private static byte[] Encode(int seq, string expr) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = "evaluate",
            ["arguments"] = new Dictionary<string, object?> { ["expression"] = expr },
        });

    [Test]
    public async Task Evaluates_Hex_Dollar_Prefix()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(Encode(1, "$100"));

        using var doc = JsonDocument.Parse(responses[0]);
        string result = doc.RootElement.GetProperty("body").GetProperty("result").GetString()!;
        await Assert.That(result).Contains("$0100");
    }

    [Test]
    public async Task Evaluates_Decimal()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(Encode(1, "42"));

        using var doc = JsonDocument.Parse(responses[0]);
        string result = doc.RootElement.GetProperty("body").GetProperty("result").GetString()!;
        await Assert.That(result).Contains("42");
    }

    [Test]
    public async Task Evaluates_Register_A()
    {
        var (dispatcher, session, responses) = Build();
        // Step the LD A,$42 to put $42 in A.
        dispatcher.HandleRequest(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["seq"] = 1, ["type"] = "request", ["command"] = "stepIn", ["arguments"] = new { },
        }));
        responses.Clear();
        dispatcher.HandleRequest(Encode(2, "A"));

        using var doc = JsonDocument.Parse(responses[0]);
        string result = doc.RootElement.GetProperty("body").GetProperty("result").GetString()!;
        await Assert.That(result).Contains("$42");
    }

    [Test]
    public async Task Unknown_Identifier_Fails()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(Encode(1, "nonexistent_symbol"));

        using var doc = JsonDocument.Parse(responses[0]);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Sets_Instruction_Breakpoint_By_Address()
    {
        var (dispatcher, session, _) = Build();
        var bp = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["seq"] = 1,
            ["type"] = "request",
            ["command"] = "setInstructionBreakpoints",
            ["arguments"] = new Dictionary<string, object?>
            {
                ["breakpoints"] = new object[]
                {
                    new Dictionary<string, object?> { ["instructionReference"] = "0x0100" }
                },
            },
        });
        dispatcher.HandleRequest(bp);
        await Assert.That(session.Breakpoints.Count).IsEqualTo(1);
    }
}
