using System.Text.Json;
using Koh.Common;
using Koh.Debugger.Dap;
using Koh.Debugger.Dap.Handlers;
using Koh.Debugger.Session;
using Koh.Native;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Debugger.Tests;

public class DwarfDebugInfoTests
{
    private static byte[] Link(string source)
    {
        var model = Compilation
            .Create(SyntaxTree.Parse(SourceText.From(source, "source.asm")))
            .Emit();
        var result = new NativeLinker().Link([new LinkerInput("source.asm", model)]);
        if (!result.Success)
            throw new InvalidOperationException(
                string.Join("; ", result.Diagnostics.Select(d => d.Message))
            );
        return result.DebugData;
    }

    [Test]
    public async Task BanksLongRangesAndSignedConstantsReachTheDebugger()
    {
        var bytes = Link(
            "VALUE EQU -123\nSECTION \"one\", ROMX[$4000], BANK[1]\nOne:: ds 600\nSECTION \"two\", ROMX[$4000], BANK[2]\nTwo:: nop"
        );
        var data = new NativeLinker().ReadDebugInfo(bytes);
        await Assert.That(data.Symbols.Single(s => s.Name == "VALUE").Value).IsEqualTo(-123);
        var loader = new DebugInfoLoader();
        loader.Load(bytes);
        await Assert.That(loader.SymbolMap.Lookup("One")!.Bank).IsEqualTo(1u);
        await Assert.That(loader.SymbolMap.Lookup("Two")!.Bank).IsEqualTo(2u);
        await Assert
            .That(loader.SourceMap.Lookup(new BankedAddress(1, 0x4257))!.Line)
            .IsEqualTo(3u);
        await Assert.That(loader.SourceMap.Lookup(new BankedAddress(1, 0x4258))).IsNull();
        await Assert
            .That(loader.SourceMap.Lookup(new BankedAddress(2, 0x4000))!.Line)
            .IsEqualTo(5u);
        await Assert.That(loader.SymbolMap.Lookup("VALUE")!.Value).IsEqualTo(-123);
    }

    [Test]
    public async Task InvalidDwarfDoesNotReplaceThePreviousMaps()
    {
        var loader = new DebugInfoLoader();
        loader.Load(Link("SECTION \"main\", ROM0[$150]\nMain:: nop"));
        var symbols = loader.SymbolMap;
        var sources = loader.SourceMap;
        bool rejected = false;
        try
        {
            loader.Load(new byte[] { 1, 2, 3 });
        }
        catch (IOException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That(ReferenceEquals(symbols, loader.SymbolMap)).IsTrue();
        await Assert.That(ReferenceEquals(sources, loader.SourceMap)).IsTrue();
        loader.Load(ReadOnlyMemory<byte>.Empty);
        await Assert.That(loader.SymbolMap.Lookup("Main")).IsNull();
        await Assert.That(loader.SourceMap.Lookup(new BankedAddress(0, 0x150))).IsNull();
    }

    [Test]
    public async Task ReaderPreservesWideBanksAndDebuggerRejectsThemExplicitly()
    {
        var bytes = Link("SECTION \"wide\", ROMX[$4000], BANK[256]\nWide:: nop");
        var data = new NativeLinker().ReadDebugInfo(bytes);
        await Assert.That(data.Symbols.Single(s => s.Name == "Wide").Bank).IsEqualTo(256u);
        string error = "";
        try
        {
            new DebugInfoLoader().Load(bytes);
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
        }
        await Assert.That(error).Contains("256");
        await Assert.That(error).Contains("255");
    }

    [Test]
    public async Task SignedConstantsAreValuesAndCannotBecomeCodeBreakpoints()
    {
        var bytes = Link("VALUE EQU -123\nSECTION \"main\", ROM0[$150]\nMain:: nop");
        var session = new DebugSession();
        session.Launch(TestRom.Create(), bytes, Koh.Emulator.HardwareMode.Dmg);
        var dispatcher = new DapDispatcher();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());
        HandlerRegistration.RegisterAll(dispatcher, session, _ => ReadOnlyMemory<byte>.Empty);
        dispatcher.HandleRequest(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    seq = 1,
                    type = "request",
                    command = "evaluate",
                    arguments = new { expression = "VALUE" },
                }
            )
        );
        dispatcher.HandleRequest(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    seq = 2,
                    type = "request",
                    command = "variables",
                    arguments = new { variablesReference = ScopesHandler.SymbolsVariablesRef },
                }
            )
        );
        dispatcher.HandleRequest(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    seq = 3,
                    type = "request",
                    command = "variables",
                    arguments = new
                    {
                        variablesReference = ScopesHandler.SourceContextVariablesRef,
                    },
                }
            )
        );
        dispatcher.HandleRequest(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    seq = 4,
                    type = "request",
                    command = "setFunctionBreakpoints",
                    arguments = new { breakpoints = new[] { new { name = "VALUE" } } },
                }
            )
        );
        using var watch = JsonDocument.Parse(responses[0]);
        await Assert.That(watch.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        await Assert
            .That(watch.RootElement.GetProperty("body").GetProperty("result").GetString())
            .Contains("-123");
        await Assert
            .That(
                watch
                    .RootElement.GetProperty("body")
                    .TryGetProperty("memoryReference", out var memory)
                    && memory.ValueKind != JsonValueKind.Null
            )
            .IsFalse();
        using var symbols = JsonDocument.Parse(responses[1]);
        var constant = symbols
            .RootElement.GetProperty("body")
            .GetProperty("variables")
            .EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == "VALUE");
        await Assert.That(constant.GetProperty("value").GetString()).IsEqualTo("-123");
        using var context = JsonDocument.Parse(responses[2]);
        await Assert.That(context.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        using var breakpoint = JsonDocument.Parse(responses[3]);
        await Assert
            .That(
                breakpoint
                    .RootElement.GetProperty("body")
                    .GetProperty("breakpoints")[0]
                    .GetProperty("verified")
                    .GetBoolean()
            )
            .IsFalse();
    }

    [Test]
    public async Task SingleSourceRangeLargerThanByteIsNotTruncated()
    {
        var section = new SectionData(
            "range",
            SectionType.Rom0,
            0x200,
            0,
            new byte[600],
            [new LineMapEntry(0, 600, "range.asm", 42)]
        );
        var model = new EmitModel([section], [], []);
        var linked = new NativeLinker().Link([new LinkerInput("range.asm", model)]);
        await Assert.That(linked.Success).IsTrue();
        var data = new NativeLinker().ReadDebugInfo(linked.DebugData);
        await Assert.That(data.AddressMap.Single().ByteCount).IsEqualTo(600u);
        var loader = new DebugInfoLoader();
        loader.Load(linked.DebugData);
        await Assert
            .That(loader.SourceMap.Lookup(new BankedAddress(0, 0x457))!.Line)
            .IsEqualTo(42u);
        await Assert.That(loader.SourceMap.Lookup(new BankedAddress(0, 0x458))).IsNull();
    }
}
