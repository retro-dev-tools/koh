using Koh.Core.Binding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class BinderSymbolTests
{
    private static BindingResult Bind(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var binder = new Binder();
        return binder.Bind(tree);
    }

    [Test]
    public async Task EquConstant_ResolvedValue()
    {
        var result = Bind("MY_CONST EQU $10");
        var sym = result.Symbols!.Lookup("MY_CONST");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.Value).IsEqualTo(0x10);
    }

    [Test]
    public async Task Label_DefinedAtPC()
    {
        var result = Bind("SECTION \"Main\", ROM0\nmain:\n    nop");
        var sym = result.Symbols!.Lookup("main");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Label);
        await Assert.That(sym.Value).IsEqualTo(0); // first instruction in section, PC = 0
    }

    [Test]
    public async Task LocalLabel_ScopedToGlobal()
    {
        var result = Bind("SECTION \"Main\", ROM0\nmain:\n    nop\n.loop:\n    nop");
        var sym = result.Symbols!.Lookup("main.loop");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Value).IsEqualTo(1); // after one NOP (1 byte)
    }

    [Test]
    public async Task Section_TracksPCCorrectly()
    {
        var result = Bind("SECTION \"Main\", ROM0\nmain:\n    nop\n    nop\nend:");
        var endSym = result.Symbols!.Lookup("end");
        await Assert.That(endSym).IsNotNull();
        await Assert.That(endSym!.Value).IsEqualTo(2); // after two NOPs
    }

    [Test]
    public async Task DuplicateLabel_ProducesDiagnostic()
    {
        var result = Bind("SECTION \"Main\", ROM0\nmain:\n    nop\nmain:");
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("already defined"))).IsTrue();
    }

    [Test]
    public async Task DataDirective_Db_EmitsBytes()
    {
        var result = Bind("SECTION \"Main\", ROM0\ndb $01, $02, $03");
        await Assert.That(result.Success).IsTrue();
        var section = result.Sections!["Main"];
        await Assert.That(section.Bytes[0]).IsEqualTo((byte)0x01);
        await Assert.That(section.Bytes[1]).IsEqualTo((byte)0x02);
        await Assert.That(section.Bytes[2]).IsEqualTo((byte)0x03);
    }

    [Test]
    public async Task DataDirective_Dw_EmitsLittleEndian()
    {
        var result = Bind("SECTION \"Main\", ROM0\ndw $1234");
        var section = result.Sections!["Main"];
        await Assert.That(section.Bytes[0]).IsEqualTo((byte)0x34); // low byte
        await Assert.That(section.Bytes[1]).IsEqualTo((byte)0x12); // high byte
    }

    [Test]
    public async Task DataDirective_Ds_ReservesSpace()
    {
        var result = Bind("SECTION \"Main\", ROM0\nds 5");
        var section = result.Sections!["Main"];
        await Assert.That(section.Bytes.Count).IsEqualTo(5);
    }

    [Test]
    public async Task DataDirective_Ds_WithFill()
    {
        var result = Bind("SECTION \"Main\", ROM0\nds 3, $FF");
        var section = result.Sections!["Main"];
        await Assert.That(section.Bytes[0]).IsEqualTo((byte)0xFF);
        await Assert.That(section.Bytes[1]).IsEqualTo((byte)0xFF);
        await Assert.That(section.Bytes[2]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task DataDirective_Db_Expression()
    {
        var result = Bind("SECTION \"Main\", ROM0\ndb $F0 & $0F");
        var section = result.Sections!["Main"];
        await Assert.That(section.Bytes[0]).IsEqualTo((byte)0x00); // $F0 & $0F = 0
    }

    [Test]
    public async Task EquUsedInData()
    {
        var result = Bind("MY_VAL EQU $42\nSECTION \"Main\", ROM0\ndb MY_VAL");
        var section = result.Sections!["Main"];
        await Assert.That(section.Bytes[0]).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task DataOutsideSection_ProducesDiagnostic()
    {
        var result = Bind("db $01");
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("outside"))).IsTrue();
    }

    [Test]
    public async Task NoErrors_CleanProgram()
    {
        var result = Bind("MY_CONST EQU $10\nSECTION \"Main\", ROM0\nmain:\n    nop");
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task DefFunction_DefinedSymbol_ReturnsOne()
    {
        // DEF(symbol) must return 1 when the symbol is defined.
        var result = Bind("MY_FLAG EQU 1\nSECTION \"Main\", ROM0\ndb DEF(MY_FLAG)");
        await Assert.That(result.Success).IsTrue();
        var section = result.Sections!["Main"];
        await Assert.That(section.Bytes[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task DefFunction_UndefinedSymbol_ReturnsZero()
    {
        // DEF(MISSING) must return 0 without crashing, and must not create a forward-ref
        // entry that causes a spurious "undefined symbol" diagnostic.
        var result = Bind("SECTION \"Main\", ROM0\ndb DEF(MISSING_SYM)");
        await Assert.That(result.Success).IsTrue();
        var section = result.Sections!["Main"];
        await Assert.That(section.Bytes[0]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task ForwardRef_ResolvedByLaterLabel()
    {
        // A label referenced in a DW before it appears in source is resolved by Pass 1.
        // By the time Pass 2 emits the DW, the symbol is already defined — no patch needed.
        // 'target' is at offset 2 (after the 2-byte DW placeholder).
        var result = Bind("SECTION \"Main\", ROM0\ndw target\ntarget:\n    nop");
        await Assert.That(result.Success).IsTrue();
        var sym = result.Symbols!.Lookup("target");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Value).IsEqualTo(2);
        // Pass 1 collected the label, so Pass 2 can emit the value directly — no patch.
        var section = result.Sections!["Main"];
        await Assert.That(section.Patches).HasCount().EqualTo(0);
        await Assert.That(section.Bytes[0]).IsEqualTo((byte)0x02); // low byte of 2
        await Assert.That(section.Bytes[1]).IsEqualTo((byte)0x00); // high byte of 2
    }

    [Test]
    public async Task MultipleSections_BytesIsolated()
    {
        var result = Bind("SECTION \"Alpha\", ROM0\ndb $AA\nSECTION \"Beta\", ROM0\ndb $BB");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Sections!["Alpha"].Bytes[0]).IsEqualTo((byte)0xAA);
        await Assert.That(result.Sections!["Beta"].Bytes[0]).IsEqualTo((byte)0xBB);
        await Assert.That(result.Sections!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SectionResume_AccumulatesBytes()
    {
        var result = Bind(
            "SECTION \"Main\", ROM0\ndb $01\nSECTION \"Other\", ROM0\ndb $02\nSECTION \"Main\", ROM0\ndb $03");
        await Assert.That(result.Success).IsTrue();
        var main = result.Sections!["Main"];
        await Assert.That(main.Bytes[0]).IsEqualTo((byte)0x01);
        await Assert.That(main.Bytes[1]).IsEqualTo((byte)0x03);
        await Assert.That(result.Sections!["Other"].Bytes[0]).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task SectionType_RecordedCorrectly()
    {
        var result = Bind("SECTION \"Work\", WRAM0");
        await Assert.That(result.Sections!["Work"].Type).IsEqualTo(SectionType.Wram0);
    }

    [Test]
    public async Task InvalidHexLiteral_DoesNotCrash()
    {
        // $GG: $ lexes as CurrentAddressToken, GG as IdentifierToken.
        // The binder sees GG as an undefined symbol and reports a diagnostic.
        var result = Bind("SECTION \"Main\", ROM0\ndb $GG");
        await Assert.That(result.Diagnostics).IsNotEmpty();
    }
}
