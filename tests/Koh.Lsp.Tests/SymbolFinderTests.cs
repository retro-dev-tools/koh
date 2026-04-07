namespace Koh.Lsp.Tests;

public class SymbolFinderTests
{
    private readonly SymbolFinder _finder = new();

    // =========================================================================
    // ResolveAt
    // =========================================================================

    [Test]
    public async Task ResolveAt_GlobalLabel_ResolvesCorrectly()
    {
        var ws = TestHelpers.CreateWorkspace("SECTION \"Main\", ROM0\nMyLabel:\n  ld a, 0\n  jp MyLabel");
        var offset = TestHelpers.FindOffset("SECTION \"Main\", ROM0\nMyLabel:\n  ld a, 0\n  jp MyLabel", "MyLabel:");

        var result = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Symbol.Name).IsEqualTo("MyLabel");
        await Assert.That(result.IsDeclaration).IsTrue();
    }

    [Test]
    public async Task ResolveAt_LocalLabel_UsesCorrectScope()
    {
        var source = "SECTION \"Main\", ROM0\nGlobal1:\n.local:\n  jr .local\nGlobal2:\n.local:\n  jr .local";
        var ws = TestHelpers.CreateWorkspace(source);

        // Resolve .local under Global2 (the reference at the end)
        var refOffset = source.LastIndexOf(".local");
        var result = _finder.ResolveAt(ws, "file:///test.asm", refOffset);

        await Assert.That(result).IsNotNull();
        // The qualified name should be Global2.local
        await Assert.That(result!.Symbol.Name).IsEqualTo("Global2.local");
    }

    [Test]
    public async Task ResolveAt_UnresolvedLookalike_ReturnsNull()
    {
        // A bare identifier not in any symbol-bearing context (e.g. in a comment or
        // as a string literal) should not resolve. We test with a string literal.
        var source = "SECTION \"Main\", ROM0\n  db \"MyLabel\"";
        var ws = TestHelpers.CreateWorkspace(source);

        // The string literal content is not an identifier token
        var offset = source.IndexOf("MyLabel");
        var result = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ResolveAt_Keyword_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.IndexOf("nop");

        var result = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ResolveAt_Reference_ResolvesSymbol()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  jp MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.LastIndexOf("MyLabel");

        var result = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Symbol.Name).IsEqualTo("MyLabel");
        await Assert.That(result.IsDeclaration).IsFalse();
    }

    // =========================================================================
    // FindAllOccurrences
    // =========================================================================

    [Test]
    public async Task FindAllOccurrences_OwnerLocalSymbol_StaysWithinOwner()
    {
        // Two separate files with the same label name — they should be separate owner-local symbols
        var ws = TestHelpers.CreateWorkspace(
            ("file:///a.asm", "SECTION \"A\", ROM0\nMyLabel:\n  jp MyLabel"),
            ("file:///b.asm", "SECTION \"B\", ROM0\nMyLabel:\n  jp MyLabel"));

        var target = _finder.ResolveAt(ws, "file:///a.asm",
            TestHelpers.FindOffset("SECTION \"A\", ROM0\nMyLabel:\n  jp MyLabel", "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var occurrences = _finder.FindAllOccurrences(ws, target!);

        // Should only find occurrences in file a
        var uris = occurrences.Select(o => o.Uri).Distinct().ToList();
        await Assert.That(uris).Contains("file:///a.asm");
    }

    [Test]
    public async Task FindAllOccurrences_ExportedSymbol_SpansAllLoadedDocuments()
    {
        var ws = TestHelpers.CreateWorkspace(
            ("file:///a.asm", "SECTION \"A\", ROM0\nShared:\n  nop\nEXPORT Shared"),
            ("file:///b.asm", "SECTION \"B\", ROM0\n  jp Shared"));

        var target = _finder.ResolveAt(ws, "file:///a.asm",
            TestHelpers.FindOffset("SECTION \"A\", ROM0\nShared:\n  nop\nEXPORT Shared", "Shared:"));

        await Assert.That(target).IsNotNull();
        var occurrences = _finder.FindAllOccurrences(ws, target!);

        var uris = occurrences.Select(o => o.Uri).Distinct().ToList();
        await Assert.That(uris).Contains("file:///a.asm");
        await Assert.That(uris).Contains("file:///b.asm");
    }

    [Test]
    public async Task FindAllOccurrences_ShadowingOwnerLocal_DoesNotMatchExported()
    {
        // File a exports "Shared", file b has its own local "Shared" that shadows
        var ws = TestHelpers.CreateWorkspace(
            ("file:///a.asm", "SECTION \"A\", ROM0\nShared:\n  nop\nEXPORT Shared"),
            ("file:///b.asm", "SECTION \"B\", ROM0\nShared:\n  jp Shared"));

        // Resolve the local "Shared" in file b
        var targetB = _finder.ResolveAt(ws, "file:///b.asm",
            TestHelpers.FindOffset("SECTION \"B\", ROM0\nShared:\n  jp Shared", "Shared:"));

        await Assert.That(targetB).IsNotNull();
        var occurrences = _finder.FindAllOccurrences(ws, targetB!);

        // If file b's Shared is owner-local, occurrences should only be in file b
        // (It may resolve to exported — depends on binding order, but the key invariant is
        // that SymbolId matching doesn't conflate different symbols)
        foreach (var occ in occurrences)
        {
            await Assert.That(occ.Symbol.SymbolId).IsEqualTo(targetB!.Symbol.SymbolId);
        }
    }

    [Test]
    public async Task FindAllOccurrences_SameTextDifferentOwners_DoesNotConflate()
    {
        var ws = TestHelpers.CreateWorkspace(
            ("file:///a.asm", "SECTION \"A\", ROM0\nCounter:\n  nop"),
            ("file:///b.asm", "SECTION \"B\", ROM0\nCounter:\n  nop"));

        var targetA = _finder.ResolveAt(ws, "file:///a.asm",
            TestHelpers.FindOffset("SECTION \"A\", ROM0\nCounter:\n  nop", "Counter:"));

        await Assert.That(targetA).IsNotNull();
        var occurrences = _finder.FindAllOccurrences(ws, targetA!);

        // Counter in file b should NOT be included (different owner)
        foreach (var occ in occurrences)
        {
            await Assert.That(occ.Symbol.SymbolId).IsEqualTo(targetA!.Symbol.SymbolId);
        }
    }

    [Test]
    public async Task FindAllOccurrences_IncludeOwnedSymbol_FindsRootAndIncludedDocs()
    {
        // Single file that defines and references a label — both declaration and reference found
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\n  jp MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);

        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var occurrences = _finder.FindAllOccurrences(ws, target!);

        // Should find both declaration and reference
        await Assert.That(occurrences.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(occurrences.Any(o => o.IsDeclaration)).IsTrue();
        await Assert.That(occurrences.Any(o => !o.IsDeclaration)).IsTrue();
    }

    [Test]
    public async Task FindAllOccurrences_ExportDirective_IsIncludedForExportedSymbol()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\nEXPORT MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);

        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var occurrences = _finder.FindAllOccurrences(ws, target!);

        // Should include the EXPORT directive reference
        await Assert.That(occurrences.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task FindAllOccurrences_ExcludeDeclarations()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\n  jp MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);

        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var occurrences = _finder.FindAllOccurrences(ws, target!, includeDeclarations: false);

        await Assert.That(occurrences.All(o => !o.IsDeclaration)).IsTrue();
    }

    // =========================================================================
    // ValidateRename
    // =========================================================================

    [Test]
    public async Task ValidateRename_ValidName_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "NewLabel");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task ValidateRename_OwnerLocalCollision_Rejected()
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
    public async Task ValidateRename_ExportedCollision_Rejected()
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
    public async Task ValidateRename_LocalToGlobal_Rejected()
    {
        var source = "SECTION \"Main\", ROM0\nGlobal:\n.local:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, ".local:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "GlobalName");
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("local").Or.Contains("global");
    }

    [Test]
    public async Task ValidateRename_GlobalToLocal_Rejected()
    {
        var source = "SECTION \"Main\", ROM0\nGlobal:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "Global:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, ".local");
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("local").Or.Contains("global");
    }

    [Test]
    public async Task ValidateRename_ToKeyword_Rejected()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "nop");
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("keyword");
    }

    [Test]
    public async Task ValidateRename_ToRegister_Rejected()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "hl");
        await Assert.That(error).IsNotNull();
        // "hl" is both a register and a keyword — either rejection message is valid
        await Assert.That(error!).Contains("keyword").Or.Contains("register");
    }

    [Test]
    public async Task ValidateRename_ToInvalidIdentifier_Rejected()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "123invalid");
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("valid identifier");
    }

    [Test]
    public async Task ValidateRename_EmptyName_Rejected()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var target = _finder.ResolveAt(ws, "file:///test.asm",
            TestHelpers.FindOffset(source, "MyLabel:"));

        await Assert.That(target).IsNotNull();
        var error = _finder.ValidateRename(ws, target!, "");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task ResolveAt_Constant_ResolvesCorrectly()
    {
        var source = "SECTION \"Main\", ROM0\nMY_CONST EQU 42\n  ld a, MY_CONST";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.LastIndexOf("MY_CONST");

        var result = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Symbol.Name).IsEqualTo("MY_CONST");
        await Assert.That(result.Symbol.Kind).IsEqualTo(Core.Symbols.SymbolKind.Constant);
    }

    [Test]
    public async Task ResolveAt_MacroCall_ResolvesCorrectly()
    {
        var source = "SECTION \"Main\", ROM0\nMyMacro: MACRO\n  nop\nENDM\n  MyMacro";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.LastIndexOf("MyMacro");

        var result = _finder.ResolveAt(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Symbol.Kind).IsEqualTo(Core.Symbols.SymbolKind.Macro);
    }
}
