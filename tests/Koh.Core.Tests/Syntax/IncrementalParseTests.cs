using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Syntax;

public class IncrementalParseTests
{
    // ========================================================================
    // Helpers
    // ========================================================================

    /// <summary>
    /// Asserts canonical tree equivalence between two syntax trees.
    /// Checks all 9 criteria from the spec.
    /// </summary>
    private static async Task AssertCanonicalEquivalence(SyntaxTree expected, SyntaxTree actual, string context = "")
    {
        var prefix = string.IsNullOrEmpty(context) ? "" : $"[{context}] ";

        // Collect tokens
        var expectedTokens = CollectTokens(expected.Root).ToList();
        var actualTokens = CollectTokens(actual.Root).ToList();

        // 1. Token kinds in same order
        await Assert.That(actualTokens.Select(t => t.Kind).ToList())
            .IsEquivalentTo(expectedTokens.Select(t => t.Kind).ToList());

        // 2. Token texts
        await Assert.That(actualTokens.Select(t => t.Text).ToList())
            .IsEquivalentTo(expectedTokens.Select(t => t.Text).ToList());

        // 3. Token Span.Start and Span.Length
        for (int i = 0; i < expectedTokens.Count; i++)
        {
            await Assert.That(actualTokens[i].Span.Start)
                .IsEqualTo(expectedTokens[i].Span.Start);
            await Assert.That(actualTokens[i].Span.Length)
                .IsEqualTo(expectedTokens[i].Span.Length);
        }

        // 4. Token FullSpan.Start and FullSpan.Length (covers trivia)
        for (int i = 0; i < expectedTokens.Count; i++)
        {
            await Assert.That(actualTokens[i].FullSpan.Start)
                .IsEqualTo(expectedTokens[i].FullSpan.Start);
            await Assert.That(actualTokens[i].FullSpan.Length)
                .IsEqualTo(expectedTokens[i].FullSpan.Length);
        }

        // 5. Leading trivia: kinds, texts, positions
        for (int i = 0; i < expectedTokens.Count; i++)
        {
            var expectedLeading = expectedTokens[i].LeadingTrivia.ToList();
            var actualLeading = actualTokens[i].LeadingTrivia.ToList();
            await Assert.That(actualLeading.Count).IsEqualTo(expectedLeading.Count);
            for (int j = 0; j < expectedLeading.Count; j++)
            {
                await Assert.That(actualLeading[j].Kind).IsEqualTo(expectedLeading[j].Kind);
                await Assert.That(actualLeading[j].Text).IsEqualTo(expectedLeading[j].Text);
                await Assert.That(actualLeading[j].Position).IsEqualTo(expectedLeading[j].Position);
            }
        }

        // 6. Trailing trivia: kinds, texts, positions
        for (int i = 0; i < expectedTokens.Count; i++)
        {
            var expectedTrailing = expectedTokens[i].TrailingTrivia.ToList();
            var actualTrailing = actualTokens[i].TrailingTrivia.ToList();
            await Assert.That(actualTrailing.Count).IsEqualTo(expectedTrailing.Count);
            for (int j = 0; j < expectedTrailing.Count; j++)
            {
                await Assert.That(actualTrailing[j].Kind).IsEqualTo(expectedTrailing[j].Kind);
                await Assert.That(actualTrailing[j].Text).IsEqualTo(expectedTrailing[j].Text);
                await Assert.That(actualTrailing[j].Position).IsEqualTo(expectedTrailing[j].Position);
            }
        }

        // 7. Node kinds in pre-order traversal
        var expectedNodes = CollectNodesPreOrder(expected.Root).ToList();
        var actualNodes = CollectNodesPreOrder(actual.Root).ToList();
        await Assert.That(actualNodes.Select(n => n.Kind).ToList())
            .IsEquivalentTo(expectedNodes.Select(n => n.Kind).ToList());

        // 8. Node positions
        for (int i = 0; i < expectedNodes.Count; i++)
        {
            await Assert.That(actualNodes[i].Position).IsEqualTo(expectedNodes[i].Position);
        }

        // 9. Diagnostics: Span.Start, Span.Length, Message, Severity
        var expectedDiags = expected.Diagnostics.OrderBy(d => d.Span.Start).ToList();
        var actualDiags = actual.Diagnostics.OrderBy(d => d.Span.Start).ToList();
        await Assert.That(actualDiags.Count).IsEqualTo(expectedDiags.Count);
        for (int i = 0; i < expectedDiags.Count; i++)
        {
            await Assert.That(actualDiags[i].Span.Start).IsEqualTo(expectedDiags[i].Span.Start);
            await Assert.That(actualDiags[i].Span.Length).IsEqualTo(expectedDiags[i].Span.Length);
            await Assert.That(actualDiags[i].Message).IsEqualTo(expectedDiags[i].Message);
            await Assert.That(actualDiags[i].Severity).IsEqualTo(expectedDiags[i].Severity);
        }
    }

    private static IEnumerable<SyntaxToken> CollectTokens(SyntaxNode node)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsToken)
                yield return child.AsToken!;
            else
                foreach (var t in CollectTokens(child.AsNode!))
                    yield return t;
        }
    }

    private static IEnumerable<SyntaxNode> CollectNodesPreOrder(SyntaxNode node)
    {
        yield return node;
        foreach (var child in node.ChildNodes())
            foreach (var n in CollectNodesPreOrder(child))
                yield return n;
    }

    /// <summary>
    /// Performs an incremental edit and asserts canonical equivalence with full reparse.
    /// Returns true if the incremental path was taken (non-null from TryReparse).
    /// </summary>
    private static async Task<bool> AssertIncrementalMatchesFullReparse(string oldCode, string newCode)
    {
        var oldSource = SourceText.From(oldCode);
        var oldTree = SyntaxTree.Parse(oldSource);
        var newSource = SourceText.From(newCode);

        // Full reparse (the gold standard)
        var fullTree = SyntaxTree.Parse(newSource);

        // Incremental reparse via WithChanges
        var incrementalTree = oldTree.WithChanges(newSource);

        await AssertCanonicalEquivalence(fullTree, incrementalTree, $"old={Truncate(oldCode)} new={Truncate(newCode)}");

        // Check if incremental path was actually taken
        var change = SyntaxTree.ComputeChange(oldSource, newSource);
        if (change is null) return false;
        var tryResult = IncrementalParser.TryReparse(oldTree, change.Value, newSource);
        return tryResult is not null;
    }

    private static string Truncate(string s, int maxLen = 40)
        => s.Length <= maxLen ? s.Replace("\n", "\\n") : s[..maxLen].Replace("\n", "\\n") + "...";

    // ========================================================================
    // ComputeChange tests
    // ========================================================================

    [Test]
    public async Task ComputeChange_IdenticalTexts_ReturnsNull()
    {
        var text = SourceText.From("nop\nret");
        var result = SyntaxTree.ComputeChange(text, text);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ComputeChange_Insertion_ReturnsCorrectSpan()
    {
        var old = SourceText.From("nop\nret");
        var @new = SourceText.From("nop\nhalt\nret");
        var change = SyntaxTree.ComputeChange(old, @new);
        await Assert.That(change).IsNotNull();
        await Assert.That(change!.Value.Span.Start).IsEqualTo(4);
        await Assert.That(change!.Value.Span.Length).IsEqualTo(0);
        await Assert.That(change!.Value.NewText).IsEqualTo("halt\n");
    }

    [Test]
    public async Task ComputeChange_Deletion_ReturnsCorrectSpan()
    {
        var old = SourceText.From("nop\nhalt\nret");
        var @new = SourceText.From("nop\nret");
        var change = SyntaxTree.ComputeChange(old, @new);
        await Assert.That(change).IsNotNull();
        await Assert.That(change!.Value.Span.Start).IsEqualTo(4);
        await Assert.That(change!.Value.Span.Length).IsEqualTo(5); // "halt\n"
        await Assert.That(change!.Value.NewText).IsEqualTo("");
    }

    [Test]
    public async Task ComputeChange_Replacement_ReturnsCorrectSpan()
    {
        var old = SourceText.From("nop\nhalt\nret");
        var @new = SourceText.From("nop\nstop\nret");
        var change = SyntaxTree.ComputeChange(old, @new);
        await Assert.That(change).IsNotNull();
        await Assert.That(change!.Value.Span.Start).IsEqualTo(4);
        await Assert.That(change!.Value.Span.Length).IsEqualTo(4); // "halt"
        await Assert.That(change!.Value.NewText).IsEqualTo("stop");
    }

    [Test]
    public async Task ComputeChange_AmbiguousDiff_IsDeterministic()
    {
        // "aaa" -> "aaaa" — could be insert at 0, 1, 2, or 3.
        // Our algorithm always picks the leftmost (after longest common prefix).
        var old = SourceText.From("aaa");
        var @new = SourceText.From("aaaa");
        var change = SyntaxTree.ComputeChange(old, @new);
        await Assert.That(change).IsNotNull();
        // Prefix = 3 ("aaa"), suffix = 0 (since maxSuffix = 3-3 = 0)
        // So: insert at position 3
        await Assert.That(change!.Value.Span.Start).IsEqualTo(3);
        await Assert.That(change!.Value.Span.Length).IsEqualTo(0);
        await Assert.That(change!.Value.NewText).IsEqualTo("a");
    }

    // ========================================================================
    // WithChanges — identical text
    // ========================================================================

    [Test]
    public async Task WithChanges_IdenticalText_ReturnsSameTree()
    {
        var source = SourceText.From("nop\nret");
        var tree = SyntaxTree.Parse(source);
        var same = tree.WithChanges(source);
        // Should return the same object reference
        await Assert.That(ReferenceEquals(tree, same)).IsTrue();
    }

    // ========================================================================
    // Incremental reparse — canonical equivalence
    // ========================================================================

    [Test]
    public async Task Incremental_InsertMiddleStatement()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nret",
            "nop\nhalt\nret");
    }

    [Test]
    public async Task Incremental_DeleteMiddleStatement()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nret",
            "nop\nret");
    }

    [Test]
    public async Task Incremental_ReplaceMiddleStatement()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nret",
            "nop\nstop\nret");
    }

    [Test]
    public async Task Incremental_InsertAtBeginning()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nret",
            "halt\nnop\nret");
    }

    [Test]
    public async Task Incremental_InsertAtEnd()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nret",
            "nop\nret\nhalt");
    }

    [Test]
    public async Task Incremental_EditWithComments()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop ; comment1\nhalt\nret ; comment2",
            "nop ; comment1\nstop\nret ; comment2");
    }

    [Test]
    public async Task Incremental_EditWithLabels()
    {
        await AssertIncrementalMatchesFullReparse(
            "start:\n    nop\n    halt\nend:\n    ret",
            "start:\n    nop\n    stop\nend:\n    ret");
    }

    [Test]
    public async Task Incremental_AddLabel()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nret",
            "nop\nmiddle:\nhalt\nret");
    }

    [Test]
    public async Task Incremental_EditSingleInstruction()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nld a, b\nret",
            "nop\nld a, c\nret");
    }

    [Test]
    public async Task Incremental_EmptyToSomething()
    {
        await AssertIncrementalMatchesFullReparse(
            "",
            "nop");
    }

    [Test]
    public async Task Incremental_SomethingToEmpty()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop",
            "");
    }

    [Test]
    public async Task Incremental_SingleStatementEdit()
    {
        // Single statement: incremental should fall back since all children are affected
        await AssertIncrementalMatchesFullReparse(
            "nop",
            "halt");
    }

    [Test]
    public async Task Incremental_MultipleStatementsEditFirst()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nret",
            "stop\nhalt\nret");
    }

    [Test]
    public async Task Incremental_MultipleStatementsEditLast()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nret",
            "nop\nhalt\nstop");
    }

    [Test]
    public async Task Incremental_WithSection()
    {
        await AssertIncrementalMatchesFullReparse(
            "SECTION \"test\", ROM0\nnop\nhalt\nret",
            "SECTION \"test\", ROM0\nnop\nstop\nret");
    }

    [Test]
    public async Task Incremental_InsertMultipleStatements()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nret",
            "nop\nhalt\nstop\nret");
    }

    [Test]
    public async Task Incremental_DeleteMultipleStatements()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nstop\nret",
            "nop\nret");
    }

    [Test]
    public async Task Incremental_WithBlankLines()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\n\nhalt\n\nret",
            "nop\n\nstop\n\nret");
    }

    [Test]
    public async Task Incremental_WithWhitespaceChange()
    {
        await AssertIncrementalMatchesFullReparse(
            "nop\n    halt\nret",
            "nop\n        halt\nret");
    }

    [Test]
    public async Task Incremental_TriviaPreservedAroundBoundary()
    {
        // Trivia (whitespace, comments) around the edit boundary must be preserved.
        var oldCode = "nop ; keep this\nhalt ; edit here\nret ; keep this too";
        var newCode = "nop ; keep this\nstop ; changed\nret ; keep this too";
        await AssertIncrementalMatchesFullReparse(oldCode, newCode);
    }

    [Test]
    public async Task Incremental_LargerFile()
    {
        // A file with many statements, editing one in the middle
        var lines = new List<string>();
        for (int i = 0; i < 20; i++)
            lines.Add($"nop ; line {i}");
        var oldCode = string.Join("\n", lines);

        lines[10] = "halt ; changed line 10";
        var newCode = string.Join("\n", lines);

        await AssertIncrementalMatchesFullReparse(oldCode, newCode);
    }

    [Test]
    public async Task Incremental_FallbackProducesCorrectResult()
    {
        // Even when incremental path can't be used, WithChanges still produces
        // a correct tree via fallback.
        var oldCode = "nop";
        var newCode = "halt";
        var oldTree = SyntaxTree.Parse(SourceText.From(oldCode));
        var newSource = SourceText.From(newCode);
        var result = oldTree.WithChanges(newSource);
        var expected = SyntaxTree.Parse(newSource);
        await AssertCanonicalEquivalence(expected, result);
    }

    // ========================================================================
    // Diagnostics handling
    // ========================================================================

    [Test]
    public async Task Incremental_DiagnosticsInUnchangedPrefix()
    {
        // An error in the prefix region should be preserved with correct span
        await AssertIncrementalMatchesFullReparse(
            "123badtoken\nnop\nhalt\nret",
            "123badtoken\nnop\nstop\nret");
    }

    [Test]
    public async Task Incremental_DiagnosticsInUnchangedSuffix()
    {
        // An error in the suffix region should be shifted correctly
        await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\n123badtoken\nret",
            "nop\nstop\n123badtoken\nret");
    }

    [Test]
    public async Task Incremental_DiagnosticsInChangedRegion()
    {
        // An error in the changed region should be replaced by new diagnostics
        await AssertIncrementalMatchesFullReparse(
            "nop\n123badtoken\nret",
            "nop\nhalt\nret");
    }

    // ========================================================================
    // Verify incremental path is actually taken
    // ========================================================================

    [Test]
    public async Task Incremental_PathActuallyTaken_MiddleEdit()
    {
        // With 3+ statements and a middle edit, incremental should succeed
        var wasIncremental = await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nret",
            "nop\nstop\nret");
        await Assert.That(wasIncremental).IsTrue();
    }

    [Test]
    public async Task Incremental_PathActuallyTaken_InsertMiddle()
    {
        var wasIncremental = await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nret",
            "nop\nhalt\nstop\nret");
        await Assert.That(wasIncremental).IsTrue();
    }

    [Test]
    public async Task Incremental_FallsBackForSingleStatement()
    {
        // With only 1 statement, all children are affected -> should fall back
        var wasIncremental = await AssertIncrementalMatchesFullReparse(
            "nop",
            "halt");
        await Assert.That(wasIncremental).IsFalse();
    }

    [Test]
    public async Task Incremental_EditInEofTrailingWhitespace_FallsBackCorrectly()
    {
        // Trailing whitespace after last statement — belongs to EOF trivia, no statement overlap
        var wasIncremental = await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\n   ",
            "nop\nhalt\n      ");
        // Falls back because no top-level children overlap the edit (it's in EOF trivia)
        await Assert.That(wasIncremental).IsFalse();
    }

    [Test]
    public async Task Incremental_DeleteNewlineMergingLines_MatchesFull()
    {
        // Delete newline between two statements — merges them into one parse unit
        await AssertIncrementalMatchesFullReparse(
            "nop\nhalt\nstop\n",
            "nophalt\nstop\n");
    }
}
