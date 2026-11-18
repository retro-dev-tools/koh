using Koh.Common;
using Koh.Native;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Linker.Tests;

public class DwarfFileWriterTests
{
    private static byte[] Link(string source)
    {
        var model = Compilation
            .Create(SyntaxTree.Parse(SourceText.From(source, "main.asm")))
            .Emit();
        var result = new NativeLinker().Link([new LinkerInput("main.asm", model)]);
        if (!result.Success)
            throw new InvalidOperationException(
                string.Join("; ", result.Diagnostics.Select(d => d.Message))
            );
        return result.DebugData;
    }

    [Test]
    public async Task ElfHeaderAndDwarfProfile()
    {
        var bytes = Link("SECTION \"Main\", ROM0\nnop");
        await Assert.That(bytes.Length).IsGreaterThanOrEqualTo(32);
        await Assert.That(Convert.ToHexString(bytes.AsSpan(0, 4))).IsEqualTo("7F454C46");
        await Assert.That(bytes[4]).IsEqualTo((byte)1);
        await Assert.That(bytes[5]).IsEqualTo((byte)1);
        await Assert.That(new NativeLinker().ReadDebugInfo(bytes).AddressMap.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Symbol_Round_Trip()
    {
        var parsed = new NativeLinker().ReadDebugInfo(
            Link("SECTION \"Main\", ROM0[$0150]\nmain::\nld hl, $1234")
        );
        var symbol = parsed.Symbols.Single(s => s.Name == "main");
        await Assert.That(symbol.Bank).IsEqualTo((byte)0);
        await Assert.That(symbol.Address).IsEqualTo((ushort)0x150);
        await Assert.That(symbol.DefinitionFile).IsEqualTo("main.asm");
        await Assert.That(symbol.DefinitionLine).IsEqualTo(2u);
    }

    [Test]
    public async Task AddressMap_Round_Trip_Without_Expansion()
    {
        var parsed = new NativeLinker().ReadDebugInfo(
            Link("SECTION \"Main\", ROM0[$0100]\nnop\nnop")
        );
        var first = parsed.AddressMap.Single(e => e.Line == 2);
        var second = parsed.AddressMap.Single(e => e.Line == 3);
        await Assert.That(first.Address).IsEqualTo((ushort)0x100);
        await Assert.That(second.Address).IsEqualTo((ushort)0x101);
        await Assert.That(first.ExpansionStack.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AddressMap_Round_Trip_With_Expansion_Stack()
    {
        var parsed = new NativeLinker().ReadDebugInfo(
            Link(
                "MACRO inner\n nop\nENDM\nMACRO outer\n inner\nENDM\nSECTION \"Main\", ROM0[$100]\n outer"
            )
        );
        var entry = parsed.AddressMap.Single(e => e.Address == 0x100);
        await Assert.That(entry.ExpansionStack.Count).IsEqualTo(2);
        await Assert.That(entry.ExpansionStack.All(frame => frame.File == "main.asm")).IsTrue();
        await Assert
            .That(entry.ExpansionStack.Select(frame => frame.Line).ToArray())
            .IsEquivalentTo(new uint[] { 8, 5 });
    }
}
