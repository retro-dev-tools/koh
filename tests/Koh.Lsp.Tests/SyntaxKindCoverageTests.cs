using Koh.Core.Syntax;

namespace Koh.Lsp.Tests;

/// <summary>
/// Guard test: every SyntaxKind value must be either explicitly classified
/// or explicitly listed as intentionally unclassified. New SyntaxKind values
/// cannot be added without updating SemanticTokenEncoder.
/// </summary>
public class SyntaxKindCoverageTests
{
    [Test]
    public async Task AllSyntaxKinds_AreCoveredOrExplicitlyExcluded()
    {
        var allKinds = Enum.GetValues<SyntaxKind>().ToHashSet();
        var classified = SemanticTokenEncoder.AllClassifiedKinds;
        var unclassified = SemanticTokenEncoder.IntentionallyUnclassifiedKinds;

        var covered = new HashSet<SyntaxKind>(classified);
        covered.UnionWith(unclassified);

        var missing = allKinds.Except(covered).ToList();

        await Assert.That(missing).IsEmpty()
            .Because($"The following SyntaxKind values are not covered by SemanticTokenEncoder: " +
                     $"{string.Join(", ", missing)}. Add them to AllClassifiedKinds or IntentionallyUnclassifiedKinds.");
    }

    [Test]
    public async Task ClassifiedAndUnclassified_DoNotOverlap()
    {
        var overlap = SemanticTokenEncoder.AllClassifiedKinds
            .Intersect(SemanticTokenEncoder.IntentionallyUnclassifiedKinds)
            .ToList();

        await Assert.That(overlap).IsEmpty()
            .Because($"The following SyntaxKind values appear in both classified and unclassified sets: " +
                     $"{string.Join(", ", overlap)}");
    }
}
