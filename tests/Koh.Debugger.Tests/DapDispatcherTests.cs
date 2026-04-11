using System.IO;
using System.Text;
using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Linker.Core;

namespace Koh.Debugger.Tests;

public class DapDispatcherTests
{
    private static (DapDispatcher, DebugSession, List<byte[]> responses) Build()
    {
        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        HandlerRegistration.RegisterAll(
            dispatcher,
            session,
            loadFile: _ => Array.Empty<byte>());

        return (dispatcher, session, responses);
    }

    private static byte[] EncodeRequest(int seq, string command, object? args = null)
    {
        var obj = new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
        };
        if (args is not null) obj["arguments"] = args;
        return JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    private static JsonDocument Parse(byte[] bytes) => JsonDocument.Parse(bytes);

    [Test]
    public async Task Initialize_Returns_Phase1_Capabilities()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(EncodeRequest(1, "initialize", new { clientID = "test" }));

        await Assert.That(responses.Count).IsEqualTo(1);
        using var doc = Parse(responses[0]);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        var body = root.GetProperty("body");
        await Assert.That(body.GetProperty("supportsConfigurationDoneRequest").GetBoolean()).IsTrue();
        await Assert.That(body.GetProperty("supportsReadMemoryRequest").GetBoolean()).IsFalse();
        await Assert.That(body.GetProperty("supportsDisassembleRequest").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Scopes_Returns_Registers_And_Hardware()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(EncodeRequest(1, "scopes", new { frameId = 0 }));

        await Assert.That(responses.Count).IsEqualTo(1);
        using var doc = Parse(responses[0]);
        var scopes = doc.RootElement.GetProperty("body").GetProperty("scopes");
        await Assert.That(scopes.GetArrayLength()).IsEqualTo(2);
        await Assert.That(scopes[0].GetProperty("name").GetString()).IsEqualTo("Registers");
        await Assert.That(scopes[1].GetProperty("name").GetString()).IsEqualTo("Hardware");
    }

    [Test]
    public async Task UnknownCommand_Returns_ErrorResponse()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(EncodeRequest(1, "definitelyNotAThing"));

        await Assert.That(responses.Count).IsEqualTo(1);
        using var doc = Parse(responses[0]);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Pause_Sets_Session_PauseRequested()
    {
        var (dispatcher, session, _) = Build();
        dispatcher.HandleRequest(EncodeRequest(1, "pause", new { threadId = 1 }));
        await Assert.That(session.PauseRequested).IsTrue();
    }

    [Test]
    public async Task Continue_Clears_Session_PauseRequested()
    {
        var (dispatcher, session, _) = Build();
        session.PauseRequested = true;
        dispatcher.HandleRequest(EncodeRequest(1, "continue", new { threadId = 1 }));
        await Assert.That(session.PauseRequested).IsFalse();
    }

    [Test]
    public async Task SetBreakpoints_Returns_Verified_When_Kdbg_Maps_Line()
    {
        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        // Build a tiny .kdbg with one address mapping on line 10 of src/main.asm.
        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(bank: 0, address: 0x0150, byteCount: 1,
            sourceFile: "src/main.asm", line: 10);
        using var kdbgStream = new MemoryStream();
        KdbgFileWriter.Write(kdbgStream, builder);
        byte[] kdbgBytes = kdbgStream.ToArray();

        // Build a tiny ROM (RomOnly) that loads cleanly.
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;

        HandlerRegistration.RegisterAll(
            dispatcher, session,
            loadFile: path => path.EndsWith(".kdbg") ? kdbgBytes : rom);

        // Launch to populate the SourceMap.
        dispatcher.HandleRequest(EncodeRequest(1, "launch",
            new { program = "game.gb", debugInfo = "game.kdbg" }));

        dispatcher.HandleRequest(EncodeRequest(2, "setBreakpoints", new
        {
            source = new { path = "src/main.asm" },
            breakpoints = new[] { new { line = 10 } }
        }));

        var last = responses[^1];
        using var doc = Parse(last);
        var bps = doc.RootElement.GetProperty("body").GetProperty("breakpoints");
        await Assert.That(bps.GetArrayLength()).IsEqualTo(1);
        await Assert.That(bps[0].GetProperty("verified").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task SetBreakpoints_Returns_Unverified_When_Line_Has_No_Code()
    {
        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(bank: 0, address: 0x0150, byteCount: 1,
            sourceFile: "src/main.asm", line: 10);
        using var kdbgStream = new MemoryStream();
        KdbgFileWriter.Write(kdbgStream, builder);
        byte[] kdbgBytes = kdbgStream.ToArray();

        var rom = new byte[0x8000];
        rom[0x147] = 0x00;

        HandlerRegistration.RegisterAll(
            dispatcher, session,
            loadFile: path => path.EndsWith(".kdbg") ? kdbgBytes : rom);

        dispatcher.HandleRequest(EncodeRequest(1, "launch",
            new { program = "game.gb", debugInfo = "game.kdbg" }));

        dispatcher.HandleRequest(EncodeRequest(2, "setBreakpoints", new
        {
            source = new { path = "src/main.asm" },
            breakpoints = new[] { new { line = 999 } }
        }));

        var last = responses[^1];
        using var doc = Parse(last);
        var bps = doc.RootElement.GetProperty("body").GetProperty("breakpoints");
        await Assert.That(bps[0].GetProperty("verified").GetBoolean()).IsFalse();
    }
}
