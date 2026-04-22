using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Binding;

public class BinderLineMapTests
{
    private static BindingResult BindFromFile(string path, string source)
    {
        var tree = SyntaxTree.Parse(SourceText.From(source, path));
        return new Binder().Bind(tree);
    }

    [Test]
    public async Task NopsOnDifferentLines_ProduceOneEntryPerLine()
    {
        // Section opens at line 1, __main__ label at line 2, three nops
        // follow. Each nop is a distinct source line, so we get three
        // line-map entries at offsets 0, 1, 2.
        var src =
            "SECTION \"Main\", ROM0\n" +   // line 1
            "__main__:\n" +                // line 2
            "    nop\n" +                  // line 3 → byte 0
            "    nop\n" +                  // line 4 → byte 1
            "    nop\n";                   // line 5 → byte 2
        var result = BindFromFile("test.asm", src);
        await Assert.That(result.Success).IsTrue();

        var map = result.Sections!["Main"].LineMap;
        await Assert.That(map.Count).IsEqualTo(3);
        await Assert.That(map[0]).IsEqualTo(new LineMapEntry(0, 1, "test.asm", 3));
        await Assert.That(map[1]).IsEqualTo(new LineMapEntry(1, 1, "test.asm", 4));
        await Assert.That(map[2]).IsEqualTo(new LineMapEntry(2, 1, "test.asm", 5));
    }

    [Test]
    public async Task MultiByteInstruction_MapsAllBytesToOneLine()
    {
        // LD HL, $BEEF is three bytes (opcode + little-endian operand)
        // on one source line — a single line-map entry covering [0,3).
        var src =
            "SECTION \"Main\", ROM0\n" +
            "    ld hl, $BEEF\n";
        var result = BindFromFile("foo.asm", src);
        await Assert.That(result.Success).IsTrue();

        var map = result.Sections!["Main"].LineMap;
        await Assert.That(map.Count).IsEqualTo(1);
        await Assert.That(map[0].Offset).IsEqualTo(0);
        await Assert.That(map[0].ByteCount).IsEqualTo(3);
        await Assert.That(map[0].Line).IsEqualTo(2u);
        await Assert.That(map[0].File).IsEqualTo("foo.asm");
    }

    [Test]
    public async Task DbLine_CoalescesBytesIntoSingleEntry()
    {
        // db with multiple literal bytes — one source line, one entry.
        var src =
            "SECTION \"Main\", ROM0\n" +
            "    db $01, $02, $03, $04\n";
        var result = BindFromFile("bar.asm", src);
        await Assert.That(result.Success).IsTrue();

        var map = result.Sections!["Main"].LineMap;
        await Assert.That(map.Count).IsEqualTo(1);
        await Assert.That(map[0].ByteCount).IsEqualTo(4);
        await Assert.That(map[0].Line).IsEqualTo(2u);
    }

    [Test]
    public async Task BlankLinesAndComments_DoNotCreateEntries()
    {
        // Comments / blank lines don't emit bytes — the next nop after
        // them should still pick up the right line number.
        var src =
            "SECTION \"Main\", ROM0\n" +   // 1
            "\n" +                          // 2 blank
            "; a comment here\n" +          // 3
            "    nop\n" +                   // 4 → byte 0
            "\n" +                          // 5 blank
            "    nop\n";                    // 6 → byte 1
        var result = BindFromFile("x.asm", src);
        await Assert.That(result.Success).IsTrue();

        var map = result.Sections!["Main"].LineMap;
        await Assert.That(map.Count).IsEqualTo(2);
        await Assert.That(map[0].Line).IsEqualTo(4u);
        await Assert.That(map[1].Line).IsEqualTo(6u);
    }

    [Test]
    public async Task JumpToForwardLabel_StillMapsAllThreeBytesToJpLine()
    {
        // JP forward uses an unresolved operand (ReserveWord) during
        // Pass 2. Those reserved bytes belong to the JP instruction's
        // source line — regression against Reserve* not being counted.
        var src =
            "SECTION \"Main\", ROM0\n" +   // 1
            "__main__:\n" +                 // 2
            "    jp target\n" +             // 3 → 3 bytes at offsets 0..2
            "target:\n" +                   // 4
            "    nop\n";                    // 5 → 1 byte at offset 3
        var result = BindFromFile("j.asm", src);
        await Assert.That(result.Success).IsTrue();

        var map = result.Sections!["Main"].LineMap;
        await Assert.That(map.Count).IsEqualTo(2);
        await Assert.That(map[0]).IsEqualTo(new LineMapEntry(0, 3, "j.asm", 3));
        await Assert.That(map[1]).IsEqualTo(new LineMapEntry(3, 1, "j.asm", 5));
    }
}
