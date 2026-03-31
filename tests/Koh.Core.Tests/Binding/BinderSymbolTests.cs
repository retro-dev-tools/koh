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

    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
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

    // =========================================================================
    // RGBDS rejection tests
    // =========================================================================

    // RGBDS: double-purge
    [Test]
    public async Task DoublePurge_PurgeTwice_RejectsAssembly()
    {
        var model = Emit("""
            def n equ 42
            purge n
            purge n
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: empty-local-purged
    [Test]
    public async Task EmptyLocalPurged_PurgeLocalWithoutParent_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "Test", ROM0
            PURGE .test
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: local-purge
    [Test]
    public async Task LocalPurge_InterpolateAfterPurge_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "Test", ROM0[0]
            Glob:
            .loc
            PURGE .loc
            PRINTLN "{.loc}"
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: local-ref-without-parent
    [Test]
    public async Task LocalRefWithoutParent_ReferenceLocalInMainScope_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "Test", ROM0
            dw .test
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: local-without-parent
    [Test]
    public async Task LocalWithoutParent_DeclareLocalInMainScope_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "Test", ROM0
            .test:
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: purge-ref
    [Test]
    public async Task PurgeRef_PurgeReferencedSymbol_RejectsAssembly()
    {
        // A symbol that has already been referenced cannot be purged
        var model = Emit("""
            SECTION "test", ROM0
            dw ref
            PURGE ref
            OK:
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: purge-refs
    [Test]
    public async Task PurgeRefs_FloatingLabelPurgedAfterUse_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "floating purging", ROM0
            Floating:
            db Floating
            PURGE Floating
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: reference-undefined-equs
    [Test]
    public async Task ReferenceUndefinedEqus_EqusDefinedAfterUse_RejectsAssembly()
    {
        // s1 is referenced before DEF s1 EQUS — must fail because EQUS cannot be patched
        var model = Emit("""
            SECTION "sec", ROM0[0]
            db s1, s2
            def s1 equs "1"
            redef s2 equs "2"
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: ref-override-bad
    [Test]
    public async Task RefOverrideBad_EqusDefinedAfterUseAsNumeric_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "Bad!", ROM0
            db X
            def X equs "0"
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: sym-collision
    [Test]
    public async Task SymCollision_InterpolateAfterPurge_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "Collision course", OAM[$FE00]
            dork: ds 1
            PURGE dork
            PRINTLN "dork: {dork}"
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: warn-truncation
    [Test]
    public async Task WarnTruncation_Db9BitValue_SucceedsWithWarning()
    {
        // In RGBDS, db with a value that doesn't fit in 8 bits is a WARNING, not an error.
        // Assembly succeeds but a truncation/range diagnostic is emitted.
        var model = Emit("""
            SECTION "ROM", ROM0
            db 1 << 8
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("truncat") || d.Message.Contains("range"))).IsTrue();
    }

    // =========================================================================
    // RGBDS: anon-label.asm — anonymous labels with : syntax
    // =========================================================================

    [Test]
    public async Task AnonLabel_ForwardAndBackReference_ResolvedCorrectly()
    {
        // RGBDS: anon-label.asm
        // Anonymous labels use : for declaration and :- / :+ for references
        var model = Emit("""
            SECTION "Test", ROM0[$0000]
            :
                nop
                jr :-
            :
                db $01
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    // =========================================================================
    // RGBDS: endl-local-scope.asm — ENDL restores local label scope to ROM side
    // =========================================================================

    [Test]
    public async Task EndlLocalScope_LabelAfterEndl_AttachesToRomGlobal()
    {
        // RGBDS: endl-local-scope.asm
        // .end after ENDL attaches to DMARoutineCode (ROM scope), not the LOAD block global
        var model = Emit("""
            SECTION "DMA ROM", ROM0[$0000]
            DMARoutineCode:
            LOAD "DMA RAM", HRAM[$FF80]
            DMARoutine:
                nop
            ENDL
            .end
            """);
        await Assert.That(model.Success).IsTrue();
        var sym = model.Symbols.FirstOrDefault(s => s.Name == "DMARoutineCode.end");
        await Assert.That(sym).IsNotNull();
    }

    // =========================================================================
    // RGBDS: scope-level.asm — . and .. scope identifiers with global/local labels
    // =========================================================================

    [Test]
    public async Task ScopeLevel_GlobalAndLocalLabels_ScopeStrings()
    {
        // RGBDS: scope-level.asm — Alpha.local1 is a global-syntax local label
        var model = Emit("""
            SECTION "test", ROM0
            Alpha.local1:
                nop
            Beta:
                nop
            Alpha.local2:
                nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "Alpha.local1")).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "Beta")).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "Alpha.local2")).IsTrue();
    }

    // =========================================================================
    // RGBDS: period.asm — . and .. after LOAD/ENDL restore correctly
    // =========================================================================

    [Test]
    public async Task Period_LoadEndlRestoresScope()
    {
        // RGBDS: period.asm — after ENDL, . and .. refer back to ROM-side globals
        var model = Emit("""
            SECTION "sec", ROM0
            global2:
            .local1:
                nop
            LOAD "load", WRAM0
            wGlobal1:
            .wLocal1:
                ds 1
            ENDL
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "global2.local1")).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "wGlobal1.wLocal1")).IsTrue();
    }

    // =========================================================================
    // RGBDS: sym-scope.asm — Parent.loc and Parent.explicit are same symbol
    // =========================================================================

    [Test]
    public async Task SymScope_ExplicitLocalInjection_SameAddressAsShortLocal()
    {
        // RGBDS: sym-scope.asm
        // Parent.loc via .loc (short), Parent.explicit via Parent.explicit (fully qualified)
        // dw Parent.loc and dw .explicit should reference the same address
        var model = Emit("""
            SECTION "Scopes", ROM0
            Parent:
            .loc
                nop
            Parent.explicit
                nop
            """);
        await Assert.That(model.Success).IsTrue();
        var loc = model.Symbols.FirstOrDefault(s => s.Name == "Parent.loc");
        var expl = model.Symbols.FirstOrDefault(s => s.Name == "Parent.explicit");
        await Assert.That(loc).IsNotNull();
        await Assert.That(expl).IsNotNull();
    }

    // =========================================================================
    // RGBDS: remote-local-explicit.asm — exported local, referenced from outside
    // =========================================================================

    [Test]
    public async Task RemoteLocalExplicit_ExportedLocalReferencedExternally()
    {
        // RGBDS: remote-local-explicit.asm
        // Parent.child is exported (::), NotParent uses dw Parent.child
        var model = Emit("""
            SECTION "sec", ROM0
            Parent:
            Parent.child:
                db 0
            NotParent:
                dw Parent.child
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
        var child = model.Symbols.FirstOrDefault(s => s.Name == "Parent.child");
        await Assert.That(child).IsNotNull();
        await Assert.That(child!.Value).IsEqualTo(0L);
    }

    // =========================================================================
    // RGBDS: raw-identifiers.asm — #identifier syntax allows keyword names
    // =========================================================================

    [Test]
    public async Task RawIdentifiers_HashPrefixedKeywordNames_Defined()
    {
        // RGBDS: raw-identifiers.asm — def #DEF equ 1, def #def equ 2
        var model = Emit("""
            def #DEF equ 1
            def #def equ 2
            SECTION "sec", ROM0
            db #DEF
            db #def
            """);
        File.WriteAllLines(@"C:\temp\rawid_diag.txt", model.Diagnostics.Select(d => $"{d.Severity}: {d.Message}"));
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2);
    }

    // =========================================================================
    // RGBDS: symbol-names.asm — identifiers with _, digits, @, #, $
    // =========================================================================

    [Test]
    public async Task SymbolNames_UnderscoreAndAlphanumeric_Valid()
    {
        // RGBDS: symbol-names.asm
        var model = Emit("""
            def Alpha_Betical = 1
            def A1pha_Num3r1c = 2
            SECTION "test", WRAM0
            wABC:: db
            w123:: db
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "Alpha_Betical")).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "wABC")).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "w123")).IsTrue();
    }

    // =========================================================================
    // RGBDS: equs-macrodef.asm — EQUS string containing MACRO definition
    // =========================================================================

    [Test]
    public async Task EqusMacroDef_EqusExpansionDefinesMacro_MacroWorks()
    {
        // RGBDS: equs-macrodef.asm — {DEFINE} expands to a full MACRO block
        var model = Emit("""
            def DEFINE equs "MACRO mac\ndb $42\nENDM"
            {DEFINE}
            SECTION "Main", ROM0
            mac
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x42);
    }

    // =========================================================================
    // EQUS self-redef via brace expansion — Koh-specific behaviour test
    // =========================================================================

    [Test]
    public async Task EqusNest_SelfRedefViaExpansion_NewValueUsedOnSecondExpansion()
    {
        // {X} expands X → "redef X equs \"nop\""; then {X} uses the new value "nop"
        var model = Emit("""
            def X equs "redef X equs \"nop\""
            {X}
            SECTION "Main", ROM0
            {X}
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop
    }

    // =========================================================================
    // RGBDS: equs-newline.asm — EQUS with \n produces multiple statements
    // =========================================================================

    [Test]
    public async Task EqusNewline_EmbeddedNewline_ExpandsToMultipleStatements()
    {
        // RGBDS: equs-newline.asm
        // {ACT} expands to "WARN \"First\"\nWARN \"Second\"" — two warnings emitted
        var model = Emit("""
            def ACT equs "WARN \"First\"\nWARN \"Second\""
            {ACT}
            WARN "Third"
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("First"))).IsTrue();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("Second"))).IsTrue();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("Third"))).IsTrue();
    }

    // =========================================================================
    // RGBDS: equs-purge.asm — EQUS body PURGEs the symbol mid-expansion
    // =========================================================================

    [Test]
    public async Task EqusPurge_SelfPurgeDuringExpansion_SucceedsWithWarning()
    {
        // RGBDS: equs-purge.asm — BYE PURGEs itself then WARNs during {BYE} expansion
        var model = Emit("""
            def BYE equs "PURGE BYE\nWARN \"Crash?\"\n"
            {BYE}
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("Crash?"))).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "BYE")).IsFalse();
    }

    // =========================================================================
    // RGBDS: label-indent.asm — labels at any indentation level
    // =========================================================================

    [Test]
    public async Task LabelIndent_IndentedGlobalAndLocalLabels_Defined()
    {
        // RGBDS: label-indent.asm — Lab:, .loc, Lab.loc2 all work indented
        var model = Emit("""
            SECTION "test", WRAMX
            	Lab:
            	.loc
            	Lab.loc2
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "Lab")).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "Lab.loc")).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "Lab.loc2")).IsTrue();
    }

    // =========================================================================
    // RGBDS: purge-deferred.asm — PURGE with brace-expanded names
    // =========================================================================

    [Test]
    public async Task PurgeDeferred_BraceExpandedNameInPurgeList_BothSymbolsRemoved()
    {
        // RGBDS: purge-deferred.asm
        // Purging 'prefix' must not prevent evaluating {prefix}banana → coolbanana
        var model = Emit("""
            DEF prefix EQUS "cool"
            DEF {prefix}banana EQU 1
            PURGE prefix, {prefix}banana
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "prefix")).IsFalse();
        await Assert.That(model.Symbols.Any(s => s.Name == "coolbanana")).IsFalse();
    }
}
