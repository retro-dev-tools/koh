using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests;

public class ParallelParseTests
{
    // Collect all token texts from a tree via recursive descent.
    private static List<string> CollectTokenTexts(SyntaxTree tree)
    {
        var result = new List<string>();
        CollectFromNode(tree.Root, result);
        return result;
    }

    private static void CollectFromNode(SyntaxNode node, List<string> result)
    {
        foreach (var token in node.ChildTokens())
            result.Add(token.Text);
        foreach (var child in node.ChildNodes())
            CollectFromNode(child, result);
    }

    [Test]
    public async Task CreateFromSources_ParsesAllFiles()
    {
        var sources = new[]
        {
            SourceText.From("nop", "file1.asm"),
            SourceText.From("ld a, b", "file2.asm"),
            SourceText.From("halt", "file3.asm"),
        };

        var compilation = Compilation.CreateFromSources(sources);

        await Assert.That(compilation.SyntaxTrees.Count).IsEqualTo(3);
        foreach (var tree in compilation.SyntaxTrees)
            await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task CreateFromSources_PreservesFileOrder()
    {
        var sources = new[]
        {
            SourceText.From("nop", "alpha.asm"),
            SourceText.From("halt", "beta.asm"),
            SourceText.From("ld b, c", "gamma.asm"),
        };

        var compilation = Compilation.CreateFromSources(sources);

        var trees = compilation.SyntaxTrees;
        await Assert.That(trees.Count).IsEqualTo(3);
        await Assert.That(trees[0].Text.FilePath).IsEqualTo("alpha.asm");
        await Assert.That(trees[1].Text.FilePath).IsEqualTo("beta.asm");
        await Assert.That(trees[2].Text.FilePath).IsEqualTo("gamma.asm");
    }

    [Test]
    public async Task CreateFromSources_ParserErrorsPerFile()
    {
        var sources = new[]
        {
            SourceText.From("nop", "good.asm"),
            SourceText.From("??? invalid @@@", "bad.asm"),
        };

        var compilation = Compilation.CreateFromSources(sources);

        var trees = compilation.SyntaxTrees;
        await Assert.That(trees.Count).IsEqualTo(2);

        // Good file has no parse diagnostics.
        await Assert.That(trees[0].Diagnostics).IsEmpty();

        // Bad file has parse diagnostics, and they are scoped to that tree only.
        await Assert.That(trees[1].Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task CreateFromSources_ManyFiles()
    {
        var sources = Enumerable.Range(0, 50)
            .Select(i => SourceText.From($"nop ; file {i}", $"file{i}.asm"))
            .ToList();

        var compilation = Compilation.CreateFromSources(sources);

        await Assert.That(compilation.SyntaxTrees.Count).IsEqualTo(50);
    }

    [Test]
    public async Task CreateFromSources_Deterministic_RepeatedCallsProduceIdenticalTrees()
    {
        var sources = Enumerable.Range(0, 20)
            .Select(i => SourceText.From($"nop\nld a, {i}\nhalt", $"file{i}.asm"))
            .ToList();

        // Parse the same sources 5 times.
        var runs = Enumerable.Range(0, 5)
            .Select(_ => Compilation.CreateFromSources(sources))
            .ToList();

        // All runs must produce the same token texts and diagnostic counts per tree.
        var reference = runs[0];
        for (int run = 1; run < runs.Count; run++)
        {
            var current = runs[run];
            for (int i = 0; i < sources.Count; i++)
            {
                var refTokens = CollectTokenTexts(reference.SyntaxTrees[i]);
                var curTokens = CollectTokenTexts(current.SyntaxTrees[i]);
                await Assert.That(curTokens).IsEquivalentTo(refTokens);
                await Assert.That(current.SyntaxTrees[i].Diagnostics.Count)
                    .IsEqualTo(reference.SyntaxTrees[i].Diagnostics.Count);
            }
        }
    }

    [Test]
    public async Task CreateFromSources_MatchesSequentialParse()
    {
        var sources = Enumerable.Range(0, 10)
            .Select(i => SourceText.From($"SECTION \"S{i}\", ROM0\nld a, {i}\nnop", $"file{i}.asm"))
            .ToList();

        var parallel = Compilation.CreateFromSources(sources);
        var sequential = Compilation.Create(sources.Select(SyntaxTree.Parse).ToArray());

        await Assert.That(parallel.SyntaxTrees.Count).IsEqualTo(sequential.SyntaxTrees.Count);

        for (int i = 0; i < sources.Count; i++)
        {
            var parTokens = CollectTokenTexts(parallel.SyntaxTrees[i]);
            var seqTokens = CollectTokenTexts(sequential.SyntaxTrees[i]);
            await Assert.That(parTokens).IsEquivalentTo(seqTokens);
            await Assert.That(parallel.SyntaxTrees[i].Diagnostics.Count)
                .IsEqualTo(sequential.SyntaxTrees[i].Diagnostics.Count);
        }
    }
}
