using System.Text.Json;
using Koh.Core;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Linker.Core;

namespace Koh.Debugger.Tests;

public class DapGeneratedRomIntegrationTests
{
    private static (byte[] Rom, byte[] Kdbg) AssembleLinkAndDebugInfo(string path, string source)
    {
        var tree = SyntaxTree.Parse(SourceText.From(source, path));
        var model = Compilation.Create(tree).Emit();
        if (!model.Success)
            throw new InvalidOperationException(
                $"assemble failed: {string.Join("; ", model.Diagnostics.Select(d => d.Message))}");

        var result = new Koh.Linker.Core.Linker().Link([new LinkerInput(path, model)]);
        if (!result.Success)
            throw new InvalidOperationException(
                $"link failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");

        var builder = new DebugInfoBuilder();
        DebugInfoPopulator.Populate(builder, result);
        using var stream = new MemoryStream();
        KdbgFileWriter.Write(stream, builder);
        return (result.RomData!, stream.ToArray());
    }

    private static byte[] Encode(int seq, string command, object arguments) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
            ["arguments"] = arguments,
        });

    [Test]
    public async Task GeneratedRomKdbg_DapLaunchBreakpointStackTraceScopesAndVariables()
    {
        const string sourcePath = "src/main.asm";
        var (rom, kdbg) = AssembleLinkAndDebugInfo(sourcePath, """
            SECTION "Entry", ROM0[$0100]
            Entry::
                ld a, $42
                nop
            """);

        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());
        HandlerRegistration.RegisterAll(
            dispatcher,
            session,
            path => path.EndsWith(".kdbg", StringComparison.OrdinalIgnoreCase) ? kdbg : rom);

        dispatcher.HandleRequest(Encode(1, "launch", new { program = "game.gb", debugInfo = "game.kdbg" }));
        await Assert.That(session.IsLaunched).IsTrue();

        dispatcher.HandleRequest(Encode(2, "setBreakpoints", new
        {
            source = new { path = sourcePath },
            breakpoints = new[] { new { line = 3 } },
        }));
        using (var bpDoc = JsonDocument.Parse(responses[^1]))
        {
            var bp = bpDoc.RootElement.GetProperty("body").GetProperty("breakpoints")[0];
            await Assert.That(bp.GetProperty("verified").GetBoolean()).IsTrue();
            await Assert.That(bp.GetProperty("line").GetInt32()).IsEqualTo(3);
        }

        dispatcher.HandleRequest(Encode(3, "stackTrace", new { threadId = 1 }));
        using (var stackDoc = JsonDocument.Parse(responses[^1]))
        {
            var frame = stackDoc.RootElement.GetProperty("body").GetProperty("stackFrames")[0];
            await Assert.That(frame.GetProperty("line").GetInt32()).IsEqualTo(3);
            await Assert.That(frame.GetProperty("source").GetProperty("path").GetString()).IsEqualTo(sourcePath);
        }

        dispatcher.HandleRequest(Encode(4, "scopes", new { frameId = 0 }));
        using (var scopesDoc = JsonDocument.Parse(responses[^1]))
        {
            var scopes = scopesDoc.RootElement.GetProperty("body").GetProperty("scopes");
            await Assert.That(scopes.GetArrayLength()).IsEqualTo(4);
            await Assert.That(scopes[0].GetProperty("name").GetString()).IsEqualTo("Registers");
            await Assert.That(scopes[3].GetProperty("name").GetString()).IsEqualTo("Source Context");
        }

        dispatcher.HandleRequest(Encode(5, "variables", new { variablesReference = 1 }));
        using (var registersDoc = JsonDocument.Parse(responses[^1]))
        {
            var registers = registersDoc.RootElement.GetProperty("body").GetProperty("variables");
            var pc = registers.EnumerateArray().First(v => v.GetProperty("name").GetString() == "PC");
            await Assert.That(pc.GetProperty("value").GetString()).IsEqualTo("$0100");
        }

        dispatcher.HandleRequest(Encode(6, "variables", new { variablesReference = 4 }));
        using (var sourceDoc = JsonDocument.Parse(responses[^1]))
        {
            var vars = sourceDoc.RootElement.GetProperty("body").GetProperty("variables");
            var pc = vars.EnumerateArray().First(v => v.GetProperty("name").GetString() == "PC");
            await Assert.That(pc.GetProperty("value").GetString()).IsEqualTo("$00:$0100");
        }

        dispatcher.HandleRequest(Encode(7, "variables", new { variablesReference = 3 }));
        using (var symbolsDoc = JsonDocument.Parse(responses[^1]))
        {
            var vars = symbolsDoc.RootElement.GetProperty("body").GetProperty("variables");
            await Assert.That(vars.EnumerateArray().Any(v =>
                v.GetProperty("name").GetString() == "Entry")).IsTrue();
        }
    }

    [Test]
    public async Task StackTrace_UsesSourceForPcInsideMultiByteMappedRange()
    {
        var (rom, kdbg) = AssembleLinkAndDebugInfo("range.asm", """
            SECTION "Entry", ROM0[$0100]
                ld a, $42
            """);

        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        session.Launch(rom, kdbg, Koh.Emulator.Core.HardwareMode.Dmg);
        session.System!.Registers.Pc = 0x0101;
        HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());

        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());
        dispatcher.HandleRequest(Encode(1, "stackTrace", new { threadId = 1 }));

        using var doc = JsonDocument.Parse(responses[^1]);
        var frame = doc.RootElement.GetProperty("body").GetProperty("stackFrames")[0];
        await Assert.That(frame.GetProperty("line").GetInt32()).IsEqualTo(2);
        await Assert.That(frame.GetProperty("source").GetProperty("path").GetString()).IsEqualTo("range.asm");
    }
}
