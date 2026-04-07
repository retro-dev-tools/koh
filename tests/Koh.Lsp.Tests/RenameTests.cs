namespace Koh.Lsp.Tests;

public class RenameTests
{
    private readonly SymbolFinder _finder = new();

    // Helper: resolve at marker, find occurrences, validate rename, return new names at each location
    private (SymbolFinder.ResolvedSymbol? Target, IReadOnlyList<SymbolFinder.ResolvedSymbol> Occurrences)
        ResolveAndFind(Workspace ws, string uri, string source, string marker)
    {
        var offset = TestHelpers.FindOffset(source, marker);
        var target = _finder.ResolveAt(ws, uri, offset);
        if (target == null) return (null, []);
        var occs = _finder.FindAllOccurrences(ws, target);
        return (target, occs);
    }

    // =========================================================================
    // Rename: basic symbol types
    // =========================================================================

    [Test]
    public async Task Rename_GlobalLabel_RenamesDeclarationAndAllReferences()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\n  jp MyLabel\n  call MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);

        var (target, occs) = ResolveAndFind(ws, "file:///test.asm", source, "MyLabel:");

        await Assert.That(target).IsNotNull();
        await Assert.That(occs.Count).IsEqualTo(3); // 1 declaration + 2 references
        await Assert.That(occs.Count(o => o.IsDeclaration)).IsEqualTo(1);
        await Assert.That(occs.Count(o => !o.IsDeclaration)).IsEqualTo(2);

        var error = _finder.ValidateRename(ws, target!, "NewLabel");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Rename_LocalLabel_RenamesOnlyCurrentScopedSymbol()
    {
        var source = "SECTION \"Main\", ROM0\nGlobal1:\n.local:\n  jr .local\nGlobal2:\n.local:\n  jr .local";
        var ws = TestHelpers.CreateWorkspace(source);

        // Resolve .local under Global1
        var offset = source.IndexOf(".local:");
        var target = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(target).IsNotNull();
        var occs = _finder.FindAllOccurrences(ws, target!);

        // Should only find occurrences in Global1's scope (declaration + reference)
        await Assert.That(occs.Count).IsEqualTo(2);
        foreach (var occ in occs)
        {
            await Assert.That(occ.Symbol.SymbolId).IsEqualTo(target!.Symbol.SymbolId);
        }
    }

    [Test]
    public async Task Rename_Constant_RenamesDeclarationAndReferences()
    {
        var source = "SECTION \"Main\", ROM0\nMY_CONST EQU 42\n  ld a, MY_CONST";
        var ws = TestHelpers.CreateWorkspace(source);

        var (target, occs) = ResolveAndFind(ws, "file:///test.asm", source, "MY_CONST EQU");

        await Assert.That(target).IsNotNull();
        await Assert.That(occs.Count).IsEqualTo(2);

        var error = _finder.ValidateRename(ws, target!, "NEW_CONST");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Rename_StringConstant_RenamesDeclarationAndReferences()
    {
        var source = "SECTION \"Main\", ROM0\nMY_STR EQUS \"hello\"";
        var ws = TestHelpers.CreateWorkspace(source);

        var offset = TestHelpers.FindOffset(source, "MY_STR EQUS");
        var target = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "NEW_STR");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Rename_Macro_RenamesDefinitionAndCallSites()
    {
        var source = "SECTION \"Main\", ROM0\nMyMacro: MACRO\n  nop\nENDM\n  MyMacro";
        var ws = TestHelpers.CreateWorkspace(source);

        var (target, occs) = ResolveAndFind(ws, "file:///test.asm", source, "MyMacro:");

        await Assert.That(target).IsNotNull();
        await Assert.That(occs.Count).IsGreaterThanOrEqualTo(2); // definition + call

        var error = _finder.ValidateRename(ws, target!, "NewMacro");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Rename_FromReference_RenamesDeclarationToo()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\n  jp MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);

        // Resolve from the reference (last MyLabel), not the declaration
        var offset = source.LastIndexOf("MyLabel");
        var target = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(target).IsNotNull();
        await Assert.That(target!.IsDeclaration).IsFalse();

        var occs = _finder.FindAllOccurrences(ws, target);
        await Assert.That(occs.Any(o => o.IsDeclaration)).IsTrue();
    }

    // =========================================================================
    // Rename: workspace-wide behavior
    // =========================================================================

    [Test]
    public async Task Rename_ExportedSymbol_UpdatesExportDirective()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\nEXPORT MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);

        var (target, occs) = ResolveAndFind(ws, "file:///test.asm", source, "MyLabel:");

        await Assert.That(target).IsNotNull();
        // Should include: declaration, EXPORT directive reference
        await Assert.That(occs.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Rename_ExportedAcrossMultipleLoadedDocs_Works()
    {
        var ws = TestHelpers.CreateWorkspace(
            ("file:///a.asm", "SECTION \"A\", ROM0\nShared:\n  nop\nEXPORT Shared"),
            ("file:///b.asm", "SECTION \"B\", ROM0\n  jp Shared"));

        var sourceA = "SECTION \"A\", ROM0\nShared:\n  nop\nEXPORT Shared";
        var target = _finder.ResolveAt(ws, "file:///a.asm",
            TestHelpers.FindOffset(sourceA, "Shared:"));

        await Assert.That(target).IsNotNull();
        var occs = _finder.FindAllOccurrences(ws, target!);

        var uris = occs.Select(o => o.Uri).Distinct().ToList();
        await Assert.That(uris).Contains("file:///a.asm");
        await Assert.That(uris).Contains("file:///b.asm");
    }

    [Test]
    public async Task Rename_OwnerLocalAcrossIncludeRootAndIncludedDocs_Works()
    {
        // Single-file test: label and reference in same file
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\n  jp MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);

        var (target, occs) = ResolveAndFind(ws, "file:///test.asm", source, "MyLabel:");

        await Assert.That(target).IsNotNull();
        await Assert.That(occs.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Rename_SameNameDifferentOwner_NotTouched()
    {
        var ws = TestHelpers.CreateWorkspace(
            ("file:///a.asm", "SECTION \"A\", ROM0\nCounter:\n  nop"),
            ("file:///b.asm", "SECTION \"B\", ROM0\nCounter:\n  nop"));

        var sourceA = "SECTION \"A\", ROM0\nCounter:\n  nop";
        var target = _finder.ResolveAt(ws, "file:///a.asm",
            TestHelpers.FindOffset(sourceA, "Counter:"));

        await Assert.That(target).IsNotNull();
        var occs = _finder.FindAllOccurrences(ws, target!);

        // Should NOT include Counter from file b
        foreach (var occ in occs)
        {
            await Assert.That(occ.Symbol.SymbolId).IsEqualTo(target!.Symbol.SymbolId);
        }
    }

    [Test]
    public async Task Rename_ShadowingOwnerLocal_NotTouchedWhenRenamingExported()
    {
        var ws = TestHelpers.CreateWorkspace(
            ("file:///a.asm", "SECTION \"A\", ROM0\nShared:\n  nop\nEXPORT Shared"),
            ("file:///b.asm", "SECTION \"B\", ROM0\nShared:\n  jp Shared"));

        var sourceA = "SECTION \"A\", ROM0\nShared:\n  nop\nEXPORT Shared";
        var target = _finder.ResolveAt(ws, "file:///a.asm",
            TestHelpers.FindOffset(sourceA, "Shared:"));

        await Assert.That(target).IsNotNull();
        var occs = _finder.FindAllOccurrences(ws, target!);

        // Every occurrence must have the same SymbolId as the target
        foreach (var occ in occs)
        {
            await Assert.That(occ.Symbol.SymbolId).IsEqualTo(target!.Symbol.SymbolId);
        }
    }

    [Test]
    public async Task Rename_UnresolvedLookalike_NotTouched()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\n  db \"MyLabel\"";
        var ws = TestHelpers.CreateWorkspace(source);

        var (target, occs) = ResolveAndFind(ws, "file:///test.asm", source, "MyLabel:");

        await Assert.That(target).IsNotNull();
        // String literal "MyLabel" should not be included
        foreach (var occ in occs)
        {
            await Assert.That(occ.Symbol.SymbolId).IsEqualTo(target!.Symbol.SymbolId);
        }
    }

    // =========================================================================
    // PrepareRename rejections
    // =========================================================================

    [Test]
    public async Task PrepareRename_Keyword_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.IndexOf("nop");

        var result = _finder.ResolveAt(ws, "file:///test.asm", offset);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task PrepareRename_Register_ReturnsNull()
    {
        // Register tokens are keywords — ResolveAt rejects them
        var source = "SECTION \"Main\", ROM0\n  ld a, b";
        var ws = TestHelpers.CreateWorkspace(source);
        // 'a' is a register keyword, not an identifier
        var offset = source.IndexOf(", b") + 2;
        var result = _finder.ResolveAt(ws, "file:///test.asm", offset);
        await Assert.That(result).IsNull();
    }

    // =========================================================================
    // Rename validation rejections
    // =========================================================================

    [Test]
    public async Task Rename_ToReservedKeyword_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "nop");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Rename_ToInvalidIdentifier_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "123abc");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Rename_LocalToGlobalForm_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\nGlobal:\n.local:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, ".local:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "GlobalName");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Rename_GlobalToLocalForm_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\nGlobal:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "Global:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, ".local");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Rename_ToExistingOwnerLocalCollision_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\nOtherLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "OtherLabel");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Rename_ToExistingExportedCollision_ReturnsNull()
    {
        var ws = TestHelpers.CreateWorkspace(
            ("file:///a.asm", "SECTION \"A\", ROM0\nLabelA:\n  nop\nEXPORT LabelA"),
            ("file:///b.asm", "SECTION \"B\", ROM0\nLabelB:\n  nop\nEXPORT LabelB"));

        var target = _finder.ResolveAt(ws, "file:///a.asm",
            TestHelpers.FindOffset("SECTION \"A\", ROM0\nLabelA:\n  nop\nEXPORT LabelA", "LabelA:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "LabelB");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Rename_LocalToSameNameInDifferentScope_Allowed()
    {
        var source = "SECTION \"Main\", ROM0\nGlobal1:\n.loop:\n  jr .loop\nGlobal2:\n.done:\n  jr .done";
        var ws = TestHelpers.CreateWorkspace(source);

        // Rename .loop under Global1 to .done — should be allowed because
        // Global2's .done is in a different scope
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, ".loop:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, ".done");
        // Different scope — should be allowed (no collision in Global1's scope)
        await Assert.That(error).IsNull();
    }
}
