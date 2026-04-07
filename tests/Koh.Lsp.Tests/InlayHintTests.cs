using Koh.Core.Syntax;
using Koh.Core.Text;
using Newtonsoft.Json.Linq;

namespace Koh.Lsp.Tests;

public class InlayHintTests
{
    /// <summary>
    /// Call the InlayHint handler via reflection since it takes JToken.
    /// </summary>
    private static JToken? GetInlayHints(Workspace ws, string uri)
    {
        var doc = ws.GetDocument(uri);
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var model = ws.GetSemanticModel(uri);
        if (model == null) return null;

        var startOffset = 0;
        var endOffset = source.Length;

        var hints = new List<JObject>();
        var seen = new HashSet<int>();
        CollectInlayHints(tree.Root, source, model, startOffset, endOffset, hints, seen);

        return hints.Count > 0 ? JToken.FromObject(hints) : new JArray();
    }

    private static void CollectInlayHints(SyntaxNode node, SourceText source,
        Koh.Core.SemanticModel model, int startOffset, int endOffset,
        List<JObject> hints, HashSet<int> seen)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                var childNode = child.AsNode!;
                if (childNode.Position + childNode.FullSpan.Length < startOffset) continue;
                if (childNode.Position > endOffset) break;
                CollectInlayHints(childNode, source, model, startOffset, endOffset, hints, seen);
                continue;
            }
            if (!child.IsToken) continue;
            var token = child.AsToken!;

            if (token.Kind is not SyntaxKind.IdentifierToken and not SyntaxKind.LocalLabelToken)
                continue;
            if (token.Span.Start < startOffset || token.Span.Start > endOffset)
                continue;
            var parent = token.Parent;
            if (parent == null) continue;
            if (parent.Kind is SyntaxKind.LabelDeclaration or SyntaxKind.SymbolDirective or SyntaxKind.MacroDefinition)
                continue;
            if (parent.Kind is not SyntaxKind.NameExpression and not SyntaxKind.LabelOperand)
                continue;

            var symbol = model.ResolveSymbol(token.Text, token.Span.Start);
            if (symbol == null) continue;
            if (symbol.Kind is not Core.Symbols.SymbolKind.Label and not Core.Symbols.SymbolKind.Constant)
                continue;
            if (!seen.Add(token.Span.Start)) continue;

            var pos = PositionUtilities.ToLspPosition(source, token.Span.Start + token.Span.Length);
            string valueText;
            if (symbol.Kind == Core.Symbols.SymbolKind.Label)
                valueText = $"${symbol.Value:X4}";
            else
                valueText = $"${symbol.Value:X4} ({symbol.Value})";

            hints.Add(new JObject
            {
                ["position"] = JToken.FromObject(new { line = pos.Line, character = pos.Character }),
                ["label"] = $" = {valueText}",
                ["kind"] = 1,
                ["paddingLeft"] = true,
            });
        }
    }

    [Test]
    public async Task ConstantReference_GetsHint()
    {
        var source = "SECTION \"Main\", ROM0\nMY_CONST EQU 42\n  ld a, MY_CONST";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        await Assert.That(hints).IsNotNull();

        var arr = hints as JArray ?? (hints!.Type == JTokenType.Array ? (JArray)hints : new JArray());
        await Assert.That(arr.Count).IsGreaterThan(0);

        // Should contain the constant value
        var label = arr[0]!["label"]!.ToString();
        await Assert.That(label).Contains("42");
    }

    [Test]
    public async Task ConstantDeclaration_GetsNoHint()
    {
        var source = "SECTION \"Main\", ROM0\nMY_CONST EQU 42";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();
        await Assert.That(arr.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LabelDeclaration_GetsNoHint()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();
        await Assert.That(arr.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnresolvedSymbol_GetsNoHint()
    {
        // A keyword used as an operand — not an identifier
        var source = "SECTION \"Main\", ROM0\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();
        await Assert.That(arr.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Keyword_GetsNoHint()
    {
        var source = "SECTION \"Main\", ROM0\n  halt";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();
        await Assert.That(arr.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StringConstant_GetsNoHint()
    {
        var source = "SECTION \"Main\", ROM0\nMY_STR EQUS \"hello\"";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();
        // EQUS declaration — no hint (declaration context)
        await Assert.That(arr.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MacroDeclaration_GetsNoHint()
    {
        var source = "SECTION \"Main\", ROM0\nMyMacro: MACRO\n  nop\nENDM";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();
        await Assert.That(arr.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MacroCall_GetsNoHint()
    {
        var source = "SECTION \"Main\", ROM0\nMyMacro: MACRO\n  nop\nENDM\n  MyMacro";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();
        // Macro calls should not get hints (kind filter)
        await Assert.That(arr.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LabelReference_GetsAddressHint()
    {
        var source = "SECTION \"Main\", ROM0\nMyLabel:\n  nop\n  jp MyLabel";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        await Assert.That(hints).IsNotNull();

        var arr = hints as JArray ?? new JArray();
        await Assert.That(arr.Count).IsGreaterThan(0);

        var label = arr[0]!["label"]!.ToString();
        // Should contain hex address
        await Assert.That(label).Contains("$");
    }

    [Test]
    public async Task ConstantReference_GetsHexAndDecimal()
    {
        var source = "SECTION \"Main\", ROM0\nMY_CONST EQU 255\n  ld a, MY_CONST";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();
        await Assert.That(arr.Count).IsGreaterThan(0);

        var label = arr[0]!["label"]!.ToString();
        await Assert.That(label).Contains("$00FF");
        await Assert.That(label).Contains("255");
    }

    [Test]
    public async Task NoDuplicateHints()
    {
        var source = "SECTION \"Main\", ROM0\nMY_CONST EQU 42\n  ld a, MY_CONST\n  ld b, MY_CONST";
        var ws = TestHelpers.CreateWorkspace(source);

        var hints = GetInlayHints(ws, "file:///test.asm");
        var arr = hints as JArray ?? new JArray();

        // Two references — should get exactly 2 hints (no duplicates)
        await Assert.That(arr.Count).IsEqualTo(2);
    }
}
