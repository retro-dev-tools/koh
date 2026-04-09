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

        // Extend the affected range by one additional child on each side if the
        // edit boundary falls exactly on a child boundary. When the edit removes
        // text up to (or inserts at) the boundary between two children, lexing
        // the affected region in isolation may produce a different token split
        // than if the adjacent child were included (e.g., deleting the newline
        // between "nop\n" and "halt\n" merges them into "nophalt").
        // We conservatively include the neighbouring children so the reparsed
        // region always covers any potential token-merge zone.
        {
            // Compute the current affected range boundaries in old text
            int affectedStart = 0;
            for (int i = 0; i < firstAffected; i++)
                affectedStart += oldGreen.GetChild(i)!.FullWidth;

            int affectedEnd = affectedStart;
            for (int i = firstAffected; i <= lastAffected; i++)
                affectedEnd += oldGreen.GetChild(i)!.FullWidth;

            // If the edit starts at the exact beginning of the affected range,
            // pull in the preceding child to avoid a merge at the left boundary.
            if (change.Span.Start == affectedStart && firstAffected > 0)
                firstAffected--;

            // If the edit ends at the exact end of the affected range,
            // pull in the following child to avoid a merge at the right boundary.
            if (change.Span.End == affectedEnd && lastAffected + 1 < statementCount)
                lastAffected++;
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

        // If the region parse consumed less than the entire region (i.e. left trailing
        // content attached to the EOF as leading trivia), that's fine — but only if
        // the EOF's FullWidth is 0 (no trailing text). Actually, the EOF is a zero-
        // width token possibly with leading trivia. If its FullWidth > 0, it absorbed
        // trailing content as leading trivia, which means the region boundary was wrong.
        // For top-level stitching to be correct, the reparsed statements must cover
        // exactly the region.
        var regionEof = regionGreen.GetChild(regionChildCount - 1)!;
        int regionStatementsWidth = regionGreen.FullWidth - regionEof.FullWidth;
        if (regionStatementsWidth != regionText.Length)
        {
            // The EOF in the region parse absorbed trailing content as leading trivia.
            // This means our region boundary split things incorrectly — fall back.
            return null;
        }

        // --- validate: suffix nodes must match the new text at their new positions ---
        // Reused suffix nodes are byte-for-byte identical to their text in the old tree.
        // If the edit caused content to spill into or out of the suffix boundary (e.g.
        // deleting the newline that separates two statements merges them), the first
        // suffix child's text will not match the new source at reparseEndNew.
        if (lastAffected + 1 < statementCount)
        {
            var firstSuffix = oldGreen.GetChild(lastAffected + 1)!;
            string firstSuffixText = GetFullText(firstSuffix);
            int suffixLen = Math.Min(firstSuffixText.Length, newText.Length - reparseEndNew);
            if (suffixLen < firstSuffixText.Length)
                return null; // not enough room in new text
            string newTextAtSuffix = newText.ToString(new TextSpan(reparseEndNew, firstSuffixText.Length));
            if (firstSuffixText != newTextAtSuffix)
                return null; // suffix text mismatch — the boundary is not clean
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
    /// the change span.
    /// </summary>
    /// <remarks>
    /// Two conditions are required:
    /// <list type="bullet">
    ///   <item>
    ///     <term>Condition 1</term>
    ///     <description>
    ///       <c>childStart &lt; change.End &amp;&amp; change.Start &lt; childEnd</c> —
    ///       handles standard (non-zero-length) overlaps and zero-length insertions
    ///       that fall strictly <em>inside</em> the child span.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Condition 2</term>
    ///     <description>
    ///       <c>change.Length == 0 &amp;&amp; change.Start &gt;= childStart &amp;&amp; change.Start &lt;= childEnd</c> —
    ///       specifically needed for zero-length insertions at the boundary positions
    ///       (<c>change.Start == childStart</c> or <c>change.Start == childEnd</c>).
    ///       At those points <c>change.End == change.Start</c>, so Condition 1 reduces
    ///       to <c>childStart &lt; change.Start &amp;&amp; change.Start &lt; childEnd</c>,
    ///       which is <em>false</em> at both boundary positions. Condition 2 catches them.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// Reconstructs the full source text covered by a green node by walking
    /// its token leaves (including trivia).
    /// </summary>
    private static string GetFullText(GreenNodeBase node)
    {
        if (node is GreenToken token)
        {
            var sb = new System.Text.StringBuilder(node.FullWidth);
            foreach (var t in token.LeadingTrivia)
                sb.Append(t.Text);
            sb.Append(token.Text);
            foreach (var t in token.TrailingTrivia)
                sb.Append(t.Text);
            return sb.ToString();
        }

        // GreenNode: recurse into children
        var builder = new System.Text.StringBuilder(node.FullWidth);
        for (int i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            if (child is not null)
                builder.Append(GetFullText(child));
        }
        return builder.ToString();
    }

    private static bool Overlaps(int childStart, int childEnd, TextSpan change)
    {
        if (childStart < change.End && change.Start < childEnd)
            return true;

        if (change.Length == 0 && change.Start >= childStart && change.Start <= childEnd)
            return true;

        return false;
    }
}
