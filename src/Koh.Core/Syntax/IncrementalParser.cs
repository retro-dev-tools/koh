using Koh.Core.Diagnostics;
using Koh.Core.Syntax.InternalSyntax;
using Koh.Core.Text;

namespace Koh.Core.Syntax;

/// <summary>
/// Attempts region-based incremental reparse for single-region edits.
/// Falls back to <c>null</c> (meaning: caller must do a full reparse) whenever
/// the edit cannot be safely contained in a contiguous range of top-level
/// statements.
/// </summary>
internal static class IncrementalParser
{
    /// <summary>
    /// Tries to reparse only the affected top-level statements after a single
    /// <paramref name="change"/> has been applied to produce <paramref name="newText"/>.
    /// Returns <c>null</c> when incremental reparse is not safe; the caller
    /// must then fall back to a full parse.
    /// </summary>
    public static SyntaxTree? TryReparse(SyntaxTree oldTree, TextChange change, SourceText newText)
    {
        // --- guard: need a compilation-unit root with children ---
        var oldRoot = oldTree.Root;
        if (oldRoot.Kind != SyntaxKind.CompilationUnit)
            return null;

        var oldGreen = (GreenNode)oldRoot.Green;
        int childCount = oldGreen.ChildCount;
        if (childCount < 2) // at minimum: one statement + EOF
            return null;

        // The last child is always the EOF token. We never reparse it — we
        // manufacture a fresh one from the new text. So for overlap detection
        // we only look at children [0 .. childCount-2].
        int statementCount = childCount - 1;

        // --- find the range of top-level children whose FullSpan overlaps the change ---
        int firstAffected = -1;
        int lastAffected = -1;
        int offset = 0;

        for (int i = 0; i < statementCount; i++)
        {
            var child = oldGreen.GetChild(i)!;
            int childStart = offset;
            int childEnd = offset + child.FullWidth;

            bool overlaps = Overlaps(childStart, childEnd, change.Span);

            if (overlaps)
            {
                if (firstAffected < 0) firstAffected = i;
                lastAffected = i;
            }

            offset += child.FullWidth;
        }

        if (firstAffected < 0)
        {
            // Change is entirely inside the EOF token's leading trivia, or
            // outside all children — not safe to handle incrementally.
            return null;
        }

        // If all statements are affected, no benefit from incremental parse.
        if (firstAffected == 0 && lastAffected == statementCount - 1)
            return null;

        // --- compute reparse region in new text ---
        int changeDelta = change.NewText.Length - change.Span.Length;

        // The reparse region start = position of first affected child (unchanged prefix).
        int reparseStart = 0;
        for (int i = 0; i < firstAffected; i++)
            reparseStart += oldGreen.GetChild(i)!.FullWidth;

        // Old end = end of last affected child in old text.
        int reparseEndOld = reparseStart;
        for (int i = firstAffected; i <= lastAffected; i++)
            reparseEndOld += oldGreen.GetChild(i)!.FullWidth;

        // New end = old end shifted by the change delta.
        int reparseEndNew = reparseEndOld + changeDelta;

        if (reparseStart < 0 || reparseEndNew < reparseStart || reparseEndNew > newText.Length)
            return null;

        // --- parse the affected substring ---
        string regionText = newText.ToString(new TextSpan(reparseStart, reparseEndNew - reparseStart));
        var regionTree = SyntaxTree.Parse(SourceText.From(regionText));
        var regionGreen = (GreenNode)regionTree.Root.Green;
        int regionChildCount = regionGreen.ChildCount;

        // The region parse produces its own CompilationUnit with an EOF at the end.
        // We take all children except the EOF.
        int regionStatementCount = regionChildCount - 1;
        if (regionStatementCount < 0)
            return null;

        // --- validate: total width of reparsed region must match the region text length ---
        // (The region tree's root FullWidth should equal regionText.Length.)
        if (regionGreen.FullWidth != regionText.Length)
            return null;

        // Also: the statements (excluding EOF) must consume exactly the region text
        // minus the EOF's FullWidth. The EOF might have leading trivia width of 0.
        var regionEof = regionGreen.GetChild(regionChildCount - 1)!;
        int regionStatementsWidth = regionGreen.FullWidth - regionEof.FullWidth;
        if (regionStatementsWidth != regionText.Length - regionEof.FullWidth)
            return null; // shouldn't happen, but be safe

        // If the region parse consumed less than the entire region (i.e. left trailing
        // content attached to the EOF as leading trivia), that's fine — but only if
        // the EOF's FullWidth is 0 (no trailing text). Actually, the EOF is a zero-
        // width token possibly with leading trivia. If its FullWidth > 0, it absorbed
        // trailing content as leading trivia, which means the region boundary was wrong.
        // For top-level stitching to be correct, the reparsed statements must cover
        // exactly the region.
        if (regionStatementsWidth != regionText.Length)
        {
            // The EOF in the region parse absorbed trailing content as leading trivia.
            // This means our region boundary split things incorrectly — fall back.
            return null;
        }

        // --- stitch green tree ---
        var newChildren = new List<GreenNodeBase>();

        // 1. Prefix: unchanged children before the affected range
        for (int i = 0; i < firstAffected; i++)
            newChildren.Add(oldGreen.GetChild(i)!);

        // 2. Reparsed children (all except the region's EOF)
        for (int i = 0; i < regionStatementCount; i++)
            newChildren.Add(regionGreen.GetChild(i)!);

        // 3. Suffix: unchanged children after the affected range
        for (int i = lastAffected + 1; i < statementCount; i++)
            newChildren.Add(oldGreen.GetChild(i)!);

        // 4. EOF token from the original tree (it has no positional data in green)
        newChildren.Add(oldGreen.GetChild(childCount - 1)!);

        var stitchedGreen = new GreenNode(SyntaxKind.CompilationUnit, newChildren.ToArray());

        // --- validate total width ---
        if (stitchedGreen.FullWidth != newText.Length)
            return null;

        // --- build red tree ---
        var redRoot = new SyntaxNode(stitchedGreen, null, 0);

        // --- collect diagnostics ---
        // We must combine:
        // - Old diagnostics from the prefix region (span entirely before reparseStart)
        // - New diagnostics from the reparsed region (shifted by reparseStart)
        // - Old diagnostics from the suffix region (shifted by changeDelta)
        var diagnostics = new List<Diagnostic>();

        foreach (var diag in oldTree.Diagnostics)
        {
            if (diag.Span.End <= reparseStart)
            {
                // Prefix region — keep as-is
                diagnostics.Add(diag);
            }
            else if (diag.Span.Start >= reparseEndOld)
            {
                // Suffix region — shift by changeDelta
                diagnostics.Add(new Diagnostic(
                    new TextSpan(diag.Span.Start + changeDelta, diag.Span.Length),
                    diag.Message,
                    diag.Severity,
                    diag.FilePath));
            }
            // else: inside the old affected range — drop (replaced by new diagnostics)
        }

        // Add diagnostics from the reparsed region, shifted by reparseStart
        foreach (var diag in regionTree.Diagnostics)
        {
            diagnostics.Add(new Diagnostic(
                new TextSpan(diag.Span.Start + reparseStart, diag.Span.Length),
                diag.Message,
                diag.Severity,
                diag.FilePath));
        }

        return SyntaxTree.Create(newText, redRoot, diagnostics);
    }

    /// <summary>
    /// Checks whether the child span [childStart, childEnd) overlaps or touches
    /// the change span. We include "touching" because an insertion (zero-length
    /// change) at a boundary should affect the adjacent child.
    /// </summary>
    private static bool Overlaps(int childStart, int childEnd, TextSpan change)
    {
        // Standard overlap: childStart < change.End && change.Start < childEnd
        // Plus: zero-length insertion at boundary — change.Length == 0 and
        //       change.Start >= childStart && change.Start <= childEnd
        if (childStart < change.End && change.Start < childEnd)
            return true;

        if (change.Length == 0 && change.Start >= childStart && change.Start <= childEnd)
            return true;

        return false;
    }
}
