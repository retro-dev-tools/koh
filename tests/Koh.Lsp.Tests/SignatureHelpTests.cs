using Koh.Core.Syntax;
using Koh.Core.Text;
using Newtonsoft.Json.Linq;

namespace Koh.Lsp.Tests;

public class SignatureHelpTests
{
    /// <summary>
    /// Get signature help by replicating the server handler logic.
    /// </summary>
    private static JToken? GetSignatureHelp(Workspace ws, string uri, int offset)
    {
        var doc = ws.GetDocument(uri);
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var token = tree.Root.FindToken(offset);
        if (token == null) return null;

        // Find enclosing MacroCall
        var macroCall = FindEnclosingMacroCall(token);
        if (macroCall == null) return null;

        var macroNameToken = macroCall.ChildTokens().FirstOrDefault();
        if (macroNameToken == null || macroNameToken.Kind != SyntaxKind.IdentifierToken)
            return null;

        var model = ws.GetSemanticModel(uri);
        if (model == null) return null;

        var symbol = model.ResolveSymbol(macroNameToken.Text, macroNameToken.Span.Start);
        if (symbol == null || symbol.Kind != Core.Symbols.SymbolKind.Macro)
            return null;

        // Find arity from definition
        var definitionNode = symbol.DefinitionSite;
        int arity = 0;
        if (definitionNode != null)
        {
            SyntaxNode? macroStart = null;
            if (definitionNode.Kind == SyntaxKind.MacroDefinition)
                macroStart = definitionNode;
            else if (definitionNode.Kind == SyntaxKind.LabelDeclaration)
            {
                // Find the MacroDefinition sibling after the label
                var parent2 = definitionNode.Parent;
                if (parent2 != null)
                {
                    bool foundLabel = false;
                    foreach (var child in parent2.ChildNodesAndTokens())
                    {
                        if (!child.IsNode) continue;
                        if (child.AsNode! == definitionNode) { foundLabel = true; continue; }
                        if (foundLabel && child.AsNode!.Kind == SyntaxKind.MacroDefinition)
                        { macroStart = child.AsNode; break; }
                    }
                }
            }
            if (macroStart?.Parent != null)
            {
                int maxParam = 0;
                bool inBody = false;
                foreach (var child in macroStart.Parent.ChildNodesAndTokens())
                {
                    if (!child.IsNode) continue;
                    // Red tree nodes are created on-the-fly; compare by position
                    if (child.AsNode!.Kind == SyntaxKind.MacroDefinition && child.AsNode!.Position == macroStart.Position)
                    { inBody = true; continue; }
                    if (inBody)
                    {
                        if (child.AsNode!.Kind == SyntaxKind.MacroDefinition) break;
                        ScanMacroParams(child.AsNode!, ref maxParam);
                    }
                }
                arity = maxParam;
            }
        }

        // Compute active parameter
        int commaCount = 0;
        int parenDepth = 0;
        foreach (var child in macroCall.ChildNodesAndTokens())
        {
            if (child.IsToken)
            {
                var t = child.AsToken!;
                if (t.Span.Start >= offset) break;
                if (t.Kind == SyntaxKind.OpenParenToken) parenDepth++;
                else if (t.Kind == SyntaxKind.CloseParenToken) parenDepth--;
                else if (t.Kind == SyntaxKind.CommaToken && parenDepth == 0) commaCount++;
            }
            else if (child.AsNode!.Position >= offset) break;
        }

        var parameters = new JArray();
        for (int i = 1; i <= arity; i++)
            parameters.Add(new JObject { ["label"] = $"\\{i}" });

        return new JObject
        {
            ["signatures"] = new JArray
            {
                new JObject
                {
                    ["label"] = $"{macroNameToken.Text}({string.Join(", ", Enumerable.Range(1, arity).Select(i => $"\\{i}"))})",
                    ["parameters"] = parameters,
                    ["activeParameter"] = Math.Min(commaCount, Math.Max(0, arity - 1)),
                },
            },
            ["activeSignature"] = 0,
            ["activeParameter"] = Math.Min(commaCount, Math.Max(0, arity - 1)),
        };
    }

    private static SyntaxNode? FindEnclosingMacroCall(SyntaxToken token)
    {
        var node = token.Parent;
        while (node != null)
        {
            if (node.Kind == SyntaxKind.MacroCall) return node;
            node = node.Parent;
        }
        return null;
    }

    private static void ScanMacroParams(SyntaxNode node, ref int maxParam)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsToken)
            {
                var t = child.AsToken!;
                if (t.Kind == SyntaxKind.MacroParamToken && t.Text.Length == 2 &&
                    t.Text[0] == '\\' && t.Text[1] >= '1' && t.Text[1] <= '9')
                {
                    int idx = t.Text[1] - '0';
                    if (idx > maxParam) maxParam = idx;
                }
            }
            else
            {
                ScanMacroParams(child.AsNode!, ref maxParam);
            }
        }
    }

    [Test]
    public async Task MacroCall_ShowsCorrectArity()
    {
        var source = "MyMacro: MACRO\n  ld a, \\1\n  ld b, \\2\nENDM\n  MyMacro $42, $FF";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.IndexOf("$42");

        var result = GetSignatureHelp(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNotNull();
        var sig = result!["signatures"]![0]!;
        var paramCount = ((JArray)sig["parameters"]!).Count;
        await Assert.That(paramCount).IsEqualTo(2);
    }

    [Test]
    public async Task MacroCall_SecondArg_ReportsActiveParameter1()
    {
        var source = "MyMacro: MACRO\n  ld a, \\1\n  ld b, \\2\nENDM\n  MyMacro $42, $FF";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.IndexOf("$FF");

        var result = GetSignatureHelp(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNotNull();
        var activeParam = (int)result!["activeParameter"]!;
        await Assert.That(activeParam).IsEqualTo(1);
    }

    [Test]
    public async Task ZeroArgMacro_ShowsZeroParams()
    {
        var source = "MyMacro: MACRO\n  nop\nENDM\n  MyMacro";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.LastIndexOf("MyMacro");

        var result = GetSignatureHelp(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNotNull();
        var sig = result!["signatures"]![0]!;
        var paramCount = ((JArray)sig["parameters"]!).Count;
        await Assert.That(paramCount).IsEqualTo(0);
    }

    [Test]
    public async Task UndefinedMacro_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\n  UndefinedMacro $42";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.IndexOf("UndefinedMacro");

        var result = GetSignatureHelp(ws, "file:///test.asm", offset);

        // Should return null — macro is undefined
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task NotInMacroCall_ReturnsNull()
    {
        var source = "SECTION \"Main\", ROM0\n  nop";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.IndexOf("nop");

        var result = GetSignatureHelp(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MacroCall_FirstArg_ReportsActiveParameter0()
    {
        var source = "MyMacro: MACRO\n  ld a, \\1\n  ld b, \\2\nENDM\n  MyMacro $42, $FF";
        var ws = TestHelpers.CreateWorkspace(source);
        var offset = source.IndexOf("$42");

        var result = GetSignatureHelp(ws, "file:///test.asm", offset);

        await Assert.That(result).IsNotNull();
        var activeParam = (int)result!["activeParameter"]!;
        await Assert.That(activeParam).IsEqualTo(0);
    }
}
