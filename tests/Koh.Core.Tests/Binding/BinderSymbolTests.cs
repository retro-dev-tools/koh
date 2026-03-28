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
        var result = Bind("SECTION \"Main\", ROM0\ndb $GG");
        await Assert.That(result.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task Section_FixedAddress_LabelHasCorrectPC()
    {
        // SECTION at $0100 — label defined immediately should have PC = $0100
        var result = Bind("SECTION \"Entry\", ROM0[$0100]\nentry:\nnop");
        await Assert.That(result.Success).IsTrue();
        var sym = result.Symbols!.Lookup("entry");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Value).IsEqualTo(0x0100);
    }

    [Test]
    public async Task Section_FixedAddress_LabelAfterInstructionCorrect()
    {
        var result = Bind("SECTION \"Entry\", ROM0[$0100]\nnop\nnop\nend:\nhalt");
        await Assert.That(result.Success).IsTrue();
        var sym = result.Symbols!.Lookup("end");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Value).IsEqualTo(0x0102); // $0100 + 2 nops
    }

    [Test]
    public async Task BareAssignment_DefinesConstant()
    {
        var result = Bind("FOO = $42\nSECTION \"Main\", ROM0\ndb FOO");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Sections!["Main"].Bytes[0]).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task BareAssignment_Reassignable()
    {
        var result = Bind("COUNTER = 0\nCOUNTER = 1\nCOUNTER = 2\nSECTION \"Main\", ROM0\ndb COUNTER");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Sections!["Main"].Bytes[0]).IsEqualTo((byte)2);
    }

    [Test]
    public async Task DefEqu_DefinesConstant()
    {
        var result = Bind("DEF MY_CONST EQU $10\nSECTION \"Main\", ROM0\ndb MY_CONST");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Sections!["Main"].Bytes[0]).IsEqualTo((byte)0x10);
    }

    [Test]
    public async Task DefEqu_AvailableInIfCondition()
    {
        var result = Bind("DEF ENABLED EQU 1\nIF ENABLED\nSECTION \"Main\", ROM0\ndb $AA\nENDC");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Sections!["Main"].Bytes[0]).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task Equ_DuplicateDefinition_ReportsError()
    {
        var result = Bind("FOO EQU $10\nFOO EQU $20\nSECTION \"Main\", ROM0\nnop");
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("already defined"))).IsTrue();
    }

    // =========================================================================
    // Local labels without trailing colon — binding
    // =========================================================================

    [Test]
    public async Task LocalLabel_WithoutColon_DefinedAndResolvable()
    {
        var result = Bind("""
            SECTION "Main", ROM0
            start:
            .noColon
                nop
                jr .noColon
            """);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task LocalLabel_WithoutColon_JrTarget()
    {
        var result = Bind("""
            SECTION "Main", ROM0
            main:
                jr .target
                nop
            .target
                ret
            """);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
        // JR offset should be 1 (skip the nop)
        var bytes = result.Sections!["Main"].Bytes;
        await Assert.That(bytes[0]).IsEqualTo((byte)0x18); // jr
        await Assert.That(bytes[1]).IsEqualTo((byte)0x01); // skip 1 nop
    }

    // =========================================================================
    // Pass 2 global scope reset — local labels across sections
    // Regression: without resetting the global anchor between passes,
    // local label references in Pass 2 resolved against the wrong global scope.
    // =========================================================================

    [Test]
    public async Task LocalLabels_AcrossSections_ResolveCorrectly()
    {
        var result = Bind("""
            SECTION "First", ROM0[$0000]
            FirstRoutine:
            .local:
                nop
                jr .local

            SECTION "Second", ROM0[$0100]
            SecondRoutine:
            .local:
                nop
                jr .local
            """);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task LocalLabels_Pass2Scope_JrResolvesInCorrectGlobal()
    {
        // Two global labels with identically-named local labels.
        // Pass 2 must track the global anchor so .loop resolves
        // to the correct scope in each context.
        var result = Bind("""
            SECTION "Main", ROM0
            FuncA:
            .loop:
                nop
                jr .loop
            FuncB:
            .loop:
                nop
                jr .loop
            """);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();

        var bytes = result.Sections!["Main"].Bytes;
        // FuncA: nop(1) + jr .loop(2) = 3 bytes, jr offset = -3 (0xFD)
        await Assert.That(bytes[1]).IsEqualTo((byte)0x18); // jr
        await Assert.That(bytes[2]).IsEqualTo((byte)0xFD); // -3

        // FuncB: nop(1) + jr .loop(2) = same pattern
        await Assert.That(bytes[4]).IsEqualTo((byte)0x18); // jr
        await Assert.That(bytes[5]).IsEqualTo((byte)0xFD); // -3
    }

    [Test]
    public async Task SectionDirective_ResetsLocalLabelScope()
    {
        // RGBDS resets local label scope on each SECTION directive.
        // .local in SectionB must not resolve against GlobalA from SectionA.
        var result = Bind("""
            SECTION "A", ROM0[$0000]
            GlobalA:
            .local:
                nop
                jr .local

            SECTION "B", ROM0[$0100]
            GlobalB:
            .local:
                nop
                jr .local
            """);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    /// <summary>
    /// Regression: PatchResolver must restore the global anchor from the patch site
    /// before evaluating deferred local label references. Without this, forward-referenced
    /// local labels in multi-scope code resolve against the wrong global scope.
    /// </summary>
    [Test]
    public async Task PatchResolver_DeferredLocalLabel_ResolvesInCorrectScope()
    {
        // FuncA and FuncB each have a forward-referenced .end via dw.
        // The dw patches are deferred to PatchResolver. Without anchor restoration,
        // both patches would resolve against FuncB (the last global label).
        var result = Bind("""
            SECTION "Main", ROM0
            FuncA:
                dw .end
                nop
            .end:
                ret
            FuncB:
                dw .end
                nop
            .end:
                ret
            """);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();

        var bytes = result.Sections!["Main"].Bytes;
        // FuncA: dw .end(2) + nop(1) = 3 bytes before .end label at offset 3
        // FuncA.end address = 3
        await Assert.That(bytes[0]).IsEqualTo((byte)0x03); // low byte of FuncA.end
        await Assert.That(bytes[1]).IsEqualTo((byte)0x00); // high byte

        // FuncB starts at offset 4 (after ret): dw .end(2) + nop(1) = offset 7 for .end
        // FuncB.end address = 4 + 3 = 7
        await Assert.That(bytes[4]).IsEqualTo((byte)0x07); // low byte of FuncB.end
        await Assert.That(bytes[5]).IsEqualTo((byte)0x00); // high byte
    }
}
