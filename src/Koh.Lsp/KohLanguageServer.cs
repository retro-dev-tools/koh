using System.Collections.Frozen;
using System.Text;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Koh.Lsp;

public sealed class KohLanguageServer(JsonRpc rpc)
{
    private readonly Workspace _workspace = new();
    private readonly SymbolFinder _symbolFinder = new();
    private bool _shutdownReceived;
    private string? _rootPath;

    private void Log(string message) =>
        _ = rpc.NotifyAsync("window/logMessage", new { type = 3, message });

    // =========================================================================
    // Lifecycle
    // =========================================================================

    [JsonRpcMethod("initialize")]
    public JToken Initialize(JToken arg)
    {
        var rootUri = arg["rootUri"]?.ToString();
        Log($"[init] rootUri = {rootUri}");

        if (rootUri != null)
        {
            try
            {
                var rootPath = ToFilePath(rootUri);
                Directory.SetCurrentDirectory(rootPath);
                _rootPath = rootPath;
                Log($"[init] rootPath = {rootPath}");
                _workspace.InitializeFolder(rootPath);
                Log($"[init] workspace mode = {_workspace.CurrentMode}");
            }
            catch (Exception ex)
            {
                Log($"[init] ERROR: {ex.Message}");
            }
        }

        var result = new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Full,
                    Save = new SaveOptions { IncludeText = false },
                },
                HoverProvider = true,
                DefinitionProvider = true,
                ReferencesProvider = true,
                DocumentSymbolProvider = true,
                SemanticTokensOptions = new SemanticTokensOptions
                {
                    Full = true,
                    Legend = new SemanticTokensLegend
                    {
                        TokenTypes = SemanticTokenEncoder.TokenTypes,
                        TokenModifiers = SemanticTokenEncoder.TokenModifiers,
                    },
                },
                RenameProvider = new RenameOptions { PrepareProvider = true },
                CompletionProvider = new CompletionOptions
                {
                    TriggerCharacters = ["."],
                    ResolveProvider = false,
                },
            },
        };

        var json = JToken.FromObject(result);
        json["capabilities"]!["inlayHintProvider"] = true;
        json["capabilities"]!["signatureHelpProvider"] = JToken.FromObject(
            new { triggerCharacters = new[] { "," } }
        );
        return json;
    }

    [JsonRpcMethod("initialized")]
    public void Initialized() { }

    [JsonRpcMethod("shutdown")]
    public object? Shutdown()
    {
        _shutdownReceived = true;
        return null;
    }

    [JsonRpcMethod("exit")]
    public void Exit() => Environment.Exit(_shutdownReceived ? 0 : 1);

    // =========================================================================
    // URI / path conversion
    // =========================================================================

    private static string ToFilePath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..];
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string ToFilePath(string uriString) => ToFilePath(new Uri(uriString));

    private static Uri ToUri(string filePath) => new("file:///" + filePath.Replace('\\', '/'));

    // =========================================================================
    // Text document sync
    // =========================================================================

    [JsonRpcMethod("textDocument/didOpen")]
    public void DidOpen(JToken arg)
    {
        var p = arg.ToObject<DidOpenTextDocumentParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        Log($"[didOpen] path = {path}");
        _workspace.OpenDocument(path, p.TextDocument.Text);
        var ctx = _workspace.GetPrimaryProjectContext(path);
        Log(
            $"[didOpen] primaryProject = {(ctx != null ? $"{ctx.Name} (entry={ctx.EntrypointPath})" : "null")}"
        );
        PublishDiagnosticsForAllOpen();
    }

    [JsonRpcMethod("textDocument/didChange")]
    public void DidChange(JToken arg)
    {
        var p = arg.ToObject<DidChangeTextDocumentParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        if (p.ContentChanges.Length > 0)
            _workspace.ChangeDocument(path, p.ContentChanges[^1].Text);
        PublishDiagnosticsForAllOpen();
    }

    [JsonRpcMethod("textDocument/didClose")]
    public void DidClose(JToken arg)
    {
        var p = arg.ToObject<DidCloseTextDocumentParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        _workspace.CloseDocument(path);
        _ = rpc.NotifyAsync(
            "textDocument/publishDiagnostics",
            new PublishDiagnosticParams { Uri = ToUri(path), Diagnostics = [] }
        );
        PublishDiagnosticsForAllOpen();
    }

    [JsonRpcMethod("textDocument/didSave")]
    public void DidSave(JToken arg)
    {
        var uri = arg["textDocument"]?["uri"]?.ToString();
        if (uri == null)
            return;
        var path = ToFilePath(new Uri(uri));
        if (
            Path.GetFileName(path).Equals("koh.yaml", StringComparison.OrdinalIgnoreCase)
            && _rootPath != null
        )
            _workspace.ReloadConfiguration(_rootPath);
        PublishDiagnosticsForAllOpen();
    }

    // =========================================================================
    // Diagnostics
    // =========================================================================

    private void PublishDiagnosticsForAllOpen()
    {
        foreach (var uri in _workspace.OpenDocumentUris)
            PublishDiagnosticsFor(uri);
    }

    private void PublishDiagnosticsFor(string path)
    {
        var state = _workspace.GetDocumentDiagnostics(path);
        if (state.Text == null || state.Diagnostics == null)
            return;

        Log($"[diag] {path} → {state.Diagnostics.Count} diagnostics");
        if (state.Diagnostics.Count is > 0 and <= 10)
        {
            foreach (var d in state.Diagnostics)
                Log($"[diag]   {d.Severity}: {d.Message} (file={d.FilePath ?? "null"})");
        }

        _ = rpc.NotifyAsync(
            "textDocument/publishDiagnostics",
            new PublishDiagnosticParams
            {
                Uri = ToUri(path),
                Diagnostics = state
                    .Diagnostics.Select(d => PositionUtilities.ToLspDiagnostic(d, state.Text))
                    .ToArray(),
            }
        );
    }

    // =========================================================================
    // Struct visitor pattern — zero-allocation tree walking
    //
    // WalkCore<TVisitor> is a static generic method constrained to struct so the
    // JIT can devirtualise and inline Visit() at each call site. Passing visitor
    // by ref avoids copying the struct on every recursive frame and allows
    // accumulating results (e.g. a found Location) without heap allocation.
    // Returning true from Visit() short-circuits the entire traversal.
    // =========================================================================

    private interface ITokenVisitor
    {
        bool Visit(SyntaxToken token, string? scope);
    }

    private static void WalkTokensWithScope<TVisitor>(SyntaxNode root, ref TVisitor visitor)
        where TVisitor : struct, ITokenVisitor
    {
        string? scope = null;
        WalkCore(root, ref visitor, ref scope);
    }

    private static bool WalkCore<TVisitor>(SyntaxNode node, ref TVisitor visitor, ref string? scope)
        where TVisitor : struct, ITokenVisitor
    {
        if (node.Kind == SyntaxKind.LabelDeclaration)
        {
            var first = node.ChildTokens().FirstOrDefault();
            if (first?.Kind == SyntaxKind.IdentifierToken)
                scope = first.Text;
        }

        foreach (var child in node.ChildNodesAndTokens())
        {
            var stop = child.IsToken
                ? visitor.Visit(child.AsToken!, scope)
                : WalkCore(child.AsNode!, ref visitor, ref scope);
            if (stop)
                return true;
        }

        return false;
    }

    private struct DefinitionFinder(
        string name,
        string uri,
        SourceText source,
        bool isLocalLabel,
        string? requiredScope
    ) : ITokenVisitor
    {
        public Location? Result;

        public bool Visit(SyntaxToken token, string? scope)
        {
            var parent = token.Parent;
            if (parent is null)
                return false;

            var isLabelDecl = parent.Kind == SyntaxKind.LabelDeclaration;
            var isSymbolDecl = parent.Kind == SyntaxKind.SymbolDirective;
            if (!isLabelDecl && !isSymbolDecl)
                return false;
            if (!token.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                return false;

            if (isLocalLabel)
            {
                if (token.Kind != SyntaxKind.LocalLabelToken)
                    return false;
                if (!string.Equals(scope, requiredScope, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else
            {
                if (token.Kind != SyntaxKind.IdentifierToken)
                    return false;
                if (
                    isSymbolDecl
                    && parent.ChildTokens().FirstOrDefault()?.Span.Start != token.Span.Start
                )
                    return false;
            }

            Result = new Location
            {
                Uri = new Uri(uri),
                Range = PositionUtilities.ToLspRange(source, parent.Span),
            };
            return true;
        }
    }

    private struct ReferenceFinder(
        string name,
        Uri documentUri,
        SourceText source,
        List<Location> results,
        bool includeDeclaration,
        bool isLocalLabel,
        string? requiredScope
    ) : ITokenVisitor
    {
        public bool Visit(SyntaxToken token, string? scope)
        {
            if (token.Kind is not (SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken))
                return false;
            if (!token.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                return false;

            if (isLocalLabel)
            {
                if (token.Kind != SyntaxKind.LocalLabelToken)
                    return false;
                if (!string.Equals(scope, requiredScope, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var isDecl =
                token.Parent?.Kind is SyntaxKind.LabelDeclaration or SyntaxKind.SymbolDirective;
            if (isDecl && !includeDeclaration)
                return false;

            results.Add(
                new Location
                {
                    Uri = documentUri,
                    Range = PositionUtilities.ToLspRange(source, token.Span),
                }
            );
            return false;
        }
    }

    private struct GlobalLabelFinder(int position) : ITokenVisitor
    {
        public string? LastGlobal;

        public bool Visit(SyntaxToken token, string? scope)
        {
            if (token.Span.Start >= position)
                return true;
            if (
                token.Kind == SyntaxKind.IdentifierToken
                && token.Parent?.Kind == SyntaxKind.LabelDeclaration
            )
                LastGlobal = token.Text;
            return false;
        }
    }

    // =========================================================================
    // Hover
    // =========================================================================

    [JsonRpcMethod("textDocument/hover")]
    public Hover? Hover(JToken arg)
    {
        var p = arg.ToObject<TextDocumentPositionParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        var doc = _workspace.GetDocument(path);
        if (doc == null)
            return null;

        var (source, tree) = doc.Value;
        var token = tree.Root.FindToken(PositionUtilities.ToOffset(source, p.Position));
        if (token == null)
            return null;

        var content = GetHoverContent(token, path);
        if (content == null)
            return null;

        return new Hover
        {
            Contents = new MarkupContent { Kind = MarkupKind.Markdown, Value = content },
            Range = PositionUtilities.ToLspRange(source, token.Span),
        };
    }

    private string? GetHoverContent(SyntaxToken token, string path)
    {
        if (IsInstructionKeyword(token.Kind))
            return GetInstructionHover(token.Text.ToUpperInvariant());

        if (token.Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken)
        {
            var sym = _workspace
                .GetModel(path)
                ?.Symbols.FirstOrDefault(s =>
                    s.Name.Equals(token.Text, StringComparison.OrdinalIgnoreCase)
                );
            if (sym == null)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine($"**{sym.Name}** ({sym.Kind})");
            sb.AppendLine($"- Value: `${sym.Value:X4}` ({sym.Value})");
            if (sym.Section != null)
                sb.AppendLine($"- Section: `{sym.Section}`");
            return sb.ToString();
        }

        if (token.Kind == SyntaxKind.NumberLiteral)
        {
            var val = Core.Binding.ExpressionEvaluator.ParseNumber(token.Text);
            if (val.HasValue)
                return $"`${val.Value:X4}` = {val.Value} = `%{Convert.ToString(val.Value, 2)}`";
        }

        return null;
    }

    private static readonly FrozenDictionary<string, string> InstructionHovers = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["NOP"] = "**NOP** — No operation (1 byte, 4 cycles)",
        ["LD"] = "**LD** — Load (copy) value between registers/memory",
        ["LDH"] = "**LDH** — Load to/from high RAM ($FF00+n)",
        ["LDI"] = "**LDI** — Load and increment HL (LD [HL+], A / LD A, [HL+])",
        ["LDD"] = "**LDD** — Load and decrement HL (LD [HL-], A / LD A, [HL-])",
        ["ADD"] = "**ADD** — Add to accumulator or HL/SP",
        ["ADC"] = "**ADC** — Add with carry",
        ["SUB"] = "**SUB** — Subtract from accumulator",
        ["SBC"] = "**SBC** — Subtract with carry",
        ["AND"] = "**AND** — Bitwise AND with accumulator",
        ["OR"] = "**OR** — Bitwise OR with accumulator",
        ["XOR"] = "**XOR** — Bitwise XOR with accumulator",
        ["CP"] = "**CP** — Compare with accumulator (subtract without storing result)",
        ["INC"] = "**INC** — Increment register or memory",
        ["DEC"] = "**DEC** — Decrement register or memory",
        ["PUSH"] = "**PUSH** — Push register pair onto stack",
        ["POP"] = "**POP** — Pop register pair from stack",
        ["JP"] = "**JP** — Jump to address",
        ["JR"] = "**JR** — Jump relative (-128 to +127)",
        ["CALL"] = "**CALL** — Call subroutine (push PC, jump)",
        ["RET"] = "**RET** — Return from subroutine",
        ["RETI"] = "**RETI** — Return and enable interrupts",
        ["RST"] = "**RST** — Restart (fast call to fixed address)",
        ["HALT"] = "**HALT** — Halt CPU until interrupt",
        ["STOP"] = "**STOP** — Stop CPU and LCD",
        ["DI"] = "**DI** — Disable interrupts",
        ["EI"] = "**EI** — Enable interrupts",
        ["DAA"] = "**DAA** — Decimal adjust accumulator (BCD)",
        ["CPL"] = "**CPL** — Complement accumulator (flip all bits)",
        ["CCF"] = "**CCF** — Complement carry flag",
        ["SCF"] = "**SCF** — Set carry flag",
        ["RLCA"] = "**RLCA** — Rotate A left circular",
        ["RLA"] = "**RLA** — Rotate A left through carry",
        ["RRCA"] = "**RRCA** — Rotate A right circular",
        ["RRA"] = "**RRA** — Rotate A right through carry",
        ["RLC"] = "**RLC** — Rotate left circular",
        ["RL"] = "**RL** — Rotate left through carry",
        ["RRC"] = "**RRC** — Rotate right circular",
        ["RR"] = "**RR** — Rotate right through carry",
        ["SLA"] = "**SLA** — Shift left arithmetic",
        ["SRA"] = "**SRA** — Shift right arithmetic (preserves sign)",
        ["SRL"] = "**SRL** — Shift right logical",
        ["SWAP"] = "**SWAP** — Swap upper and lower nibbles",
        ["BIT"] = "**BIT** — Test bit",
        ["SET"] = "**SET** — Set bit",
        ["RES"] = "**RES** — Reset (clear) bit",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static string? GetInstructionHover(string mnemonic) =>
        InstructionHovers.GetValueOrDefault(mnemonic);

    private static bool IsInstructionKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.NopKeyword && kind <= SyntaxKind.LdhKeyword;

    // =========================================================================
    // Go-to-definition
    // =========================================================================

    [JsonRpcMethod("textDocument/definition")]
    public Location? Definition(JToken arg)
    {
        var p = arg.ToObject<TextDocumentPositionParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        var doc = _workspace.GetDocument(path);
        if (doc == null)
            return null;

        var (source, tree) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);
        var token = tree.Root.FindToken(offset);
        if (token?.Kind is not (SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken))
            return null;

        if (token.Kind == SyntaxKind.LocalLabelToken)
        {
            var scope = FindEnclosingGlobalLabel(tree.Root, offset);
            return FindDefinition(tree.Root, token.Text, path, source, isLocalLabel: true, scope);
        }

        var model = _workspace.GetSemanticModel(path);
        if (model != null)
        {
            var symbol = model.ResolveSymbol(token.Text, offset);
            if (symbol?.DefinitionSite != null && symbol.DefinitionFilePath != null)
            {
                var defText =
                    _workspace.GetDocument(symbol.DefinitionFilePath) is { } defDoc ? defDoc.Text
                    : File.Exists(symbol.DefinitionFilePath)
                        ? SourceText.From(
                            File.ReadAllText(symbol.DefinitionFilePath),
                            symbol.DefinitionFilePath
                        )
                    : null;

                if (defText != null)
                    return new Location
                    {
                        Uri = ToUri(symbol.DefinitionFilePath),
                        Range = PositionUtilities.ToLspRange(
                            defText,
                            symbol.DefinitionSite.FullSpan
                        ),
                    };
            }
        }

        foreach (var uri in _workspace.OpenDocumentUris)
        {
            var otherDoc = _workspace.GetDocument(uri);
            if (otherDoc == null)
                continue;
            var (otherSource, otherTree) = otherDoc.Value;
            var result = FindDefinition(otherTree.Root, token.Text, uri, otherSource);
            if (result != null)
                return result;
        }

        return null;
    }

    private static Location? FindDefinition(
        SyntaxNode root,
        string name,
        string uri,
        SourceText source,
        bool isLocalLabel = false,
        string? requiredScope = null
    )
    {
        var finder = new DefinitionFinder(name, uri, source, isLocalLabel, requiredScope);
        WalkTokensWithScope(root, ref finder);
        return finder.Result;
    }

    // =========================================================================
    // Find references
    // =========================================================================

    [JsonRpcMethod("textDocument/references")]
    public Location[] References(JToken arg)
    {
        var p = arg.ToObject<ReferenceParams>()!;
        var doc = _workspace.GetDocument(ToFilePath(p.TextDocument.Uri));
        if (doc == null)
            return [];

        var (source, tree) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);
        var token = tree.Root.FindToken(offset);
        if (token?.Kind is not (SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken))
            return [];

        bool includeDeclaration = p.Context?.IncludeDeclaration ?? false;
        bool isLocalLabel = token.Kind == SyntaxKind.LocalLabelToken;
        string? scopeName = isLocalLabel ? FindEnclosingGlobalLabel(tree.Root, offset) : null;

        var locations = new List<Location>();

        if (isLocalLabel)
        {
            FindReferences(
                tree.Root,
                token.Text,
                new Uri(ToFilePath(p.TextDocument.Uri)),
                source,
                locations,
                includeDeclaration,
                isLocalLabel: true,
                scopeName
            );
        }
        else
        {
            foreach (var uri in _workspace.OpenDocumentUris)
            {
                var otherDoc = _workspace.GetDocument(uri);
                if (otherDoc == null)
                    continue;
                var (otherSource, otherTree) = otherDoc.Value;
                FindReferences(
                    otherTree.Root,
                    token.Text,
                    new Uri(uri),
                    otherSource,
                    locations,
                    includeDeclaration,
                    isLocalLabel: false,
                    requiredScope: null
                );
            }
        }

        return [.. locations];
    }

    private static string? FindEnclosingGlobalLabel(SyntaxNode root, int position)
    {
        var finder = new GlobalLabelFinder(position);
        WalkTokensWithScope(root, ref finder);
        return finder.LastGlobal;
    }

    private static void FindReferences(
        SyntaxNode root,
        string name,
        Uri documentUri,
        SourceText source,
        List<Location> results,
        bool includeDeclaration,
        bool isLocalLabel,
        string? requiredScope
    )
    {
        var finder = new ReferenceFinder(
            name,
            documentUri,
            source,
            results,
            includeDeclaration,
            isLocalLabel,
            requiredScope
        );
        WalkTokensWithScope(root, ref finder);
    }

    // =========================================================================
    // Document symbols
    // =========================================================================

    [JsonRpcMethod("textDocument/documentSymbol")]
    public DocumentSymbol[]? DocumentSymbol(JToken arg)
    {
        var p = arg.ToObject<DocumentSymbolParams>()!;
        var doc = _workspace.GetDocument(ToFilePath(p.TextDocument.Uri));
        if (doc == null)
            return null;

        var (source, tree) = doc.Value;
        var symbols = new List<DocumentSymbol>();
        CollectSymbols(tree.Root, source, symbols);
        return [.. symbols];
    }

    private static void CollectSymbols(
        SyntaxNode node,
        SourceText source,
        List<DocumentSymbol> symbols
    )
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (!child.IsNode)
                continue;
            var childNode = child.AsNode!;

            switch (childNode.Kind)
            {
                case SyntaxKind.LabelDeclaration:
                {
                    var nameToken = childNode.ChildTokens().FirstOrDefault();
                    if (nameToken != null)
                        symbols.Add(
                            new DocumentSymbol
                            {
                                Name = nameToken.Text,
                                Kind = SymbolKind.Function,
                                Range = PositionUtilities.ToLspRange(source, childNode.Span),
                                SelectionRange = PositionUtilities.ToLspRange(
                                    source,
                                    nameToken.Span
                                ),
                            }
                        );
                    break;
                }
                case SyntaxKind.SymbolDirective:
                {
                    using var e = childNode.ChildTokens().GetEnumerator();
                    if (!e.MoveNext())
                        break;
                    var nameToken = e.Current;
                    if (nameToken.Kind != SyntaxKind.IdentifierToken)
                        break;
                    if (!e.MoveNext())
                        break;
                    if (e.Current.Kind is not (SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword))
                        break;
                    symbols.Add(
                        new DocumentSymbol
                        {
                            Name = nameToken.Text,
                            Kind = SymbolKind.Constant,
                            Range = PositionUtilities.ToLspRange(source, childNode.Span),
                            SelectionRange = PositionUtilities.ToLspRange(source, nameToken.Span),
                        }
                    );
                    break;
                }
                case SyntaxKind.SectionDirective:
                {
                    var strToken = childNode
                        .ChildTokens()
                        .FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
                    if (strToken != null)
                    {
                        var name = strToken.Text.Length >= 2 ? strToken.Text[1..^1] : strToken.Text;
                        symbols.Add(
                            new DocumentSymbol
                            {
                                Name = name,
                                Kind = SymbolKind.Module,
                                Range = PositionUtilities.ToLspRange(source, childNode.Span),
                                SelectionRange = PositionUtilities.ToLspRange(
                                    source,
                                    strToken.Span
                                ),
                            }
                        );
                    }
                    break;
                }
            }

            CollectSymbols(childNode, source, symbols);
        }
    }

    // =========================================================================
    // Completion
    // =========================================================================

    private static readonly CompletionItem[] StaticCompletionItems = BuildStaticCompletions();

    private static CompletionItem[] BuildStaticCompletions()
    {
        string[] mnemonics =
        [
            "nop",
            "ld",
            "ldh",
            "ldi",
            "ldd",
            "add",
            "adc",
            "sub",
            "sbc",
            "and",
            "or",
            "xor",
            "cp",
            "inc",
            "dec",
            "push",
            "pop",
            "jp",
            "jr",
            "call",
            "ret",
            "reti",
            "rst",
            "halt",
            "stop",
            "di",
            "ei",
            "daa",
            "cpl",
            "ccf",
            "scf",
            "rlca",
            "rla",
            "rrca",
            "rra",
            "rlc",
            "rl",
            "rrc",
            "rr",
            "sla",
            "sra",
            "srl",
            "swap",
            "bit",
            "set",
            "res",
        ];
        string[] directives =
        [
            "SECTION",
            "DB",
            "DW",
            "DL",
            "DS",
            "EQU",
            "EQUS",
            "DEF",
            "REDEF",
            "MACRO",
            "ENDM",
            "IF",
            "ELIF",
            "ELSE",
            "ENDC",
            "REPT",
            "FOR",
            "ENDR",
            "INCLUDE",
            "INCBIN",
            "EXPORT",
            "PURGE",
            "OPT",
            "CHARMAP",
            "NEWCHARMAP",
            "SETCHARMAP",
            "PUSHC",
            "POPC",
            "ASSERT",
            "STATIC_ASSERT",
            "WARN",
            "FAIL",
            "PRINT",
            "PRINTLN",
            "PUSHS",
            "POPS",
            "PUSHO",
            "POPO",
            "RSRESET",
            "RSSET",
            "RB",
            "RW",
            "RL",
            "ALIGN",
            "UNION",
            "NEXTU",
            "ENDU",
            "LOAD",
            "ENDL",
        ];
        string[] registers = ["a", "b", "c", "d", "e", "h", "l", "af", "bc", "de", "hl", "sp"];

        var items = new List<CompletionItem>(
            mnemonics.Length + directives.Length + registers.Length
        );
        foreach (var m in mnemonics)
            items.Add(
                new CompletionItem
                {
                    Label = m,
                    Kind = CompletionItemKind.Keyword,
                    Detail = "SM83 instruction",
                }
            );
        foreach (var d in directives)
            items.Add(
                new CompletionItem
                {
                    Label = d,
                    Kind = CompletionItemKind.Keyword,
                    Detail = "Directive",
                }
            );
        foreach (var r in registers)
            items.Add(
                new CompletionItem
                {
                    Label = r,
                    Kind = CompletionItemKind.Variable,
                    Detail = "Register",
                }
            );

        return [.. items];
    }

    [JsonRpcMethod("textDocument/completion")]
    public CompletionItem[]? Completion(JToken arg)
    {
        var p = arg.ToObject<CompletionParams>()!;
        var model = _workspace.GetModel(ToFilePath(p.TextDocument.Uri));
        if (model == null)
            return StaticCompletionItems;

        var dynamic = model
            .Symbols.Select(sym => new CompletionItem
            {
                Label = sym.Name,
                Kind =
                    sym.Kind == Core.Symbols.SymbolKind.Constant
                        ? CompletionItemKind.Constant
                        : CompletionItemKind.Function,
                Detail = $"{sym.Kind}: ${sym.Value:X4}",
            })
            .ToArray();

        var result = new CompletionItem[StaticCompletionItems.Length + dynamic.Length];
        StaticCompletionItems.CopyTo(result, 0);
        dynamic.CopyTo(result, StaticCompletionItems.Length);
        return result;
    }

    // =========================================================================
    // Semantic Tokens
    // =========================================================================

    [JsonRpcMethod("textDocument/semanticTokens/full")]
    public SemanticTokens? SemanticTokensFull(JToken arg)
    {
        var p = arg.ToObject<SemanticTokensParams>()!;
        var doc = _workspace.GetDocument(ToFilePath(p.TextDocument.Uri));
        if (doc == null)
            return null;
        return new SemanticTokens { Data = SemanticTokenEncoder.Encode(doc.Value.Item2) };
    }

    // =========================================================================
    // Inlay Hints
    // =========================================================================

    [JsonRpcMethod("textDocument/inlayHint")]
    public JToken? InlayHint(JToken arg)
    {
        var path = ToFilePath(new Uri(arg["textDocument"]!["uri"]!.ToString()));
        var doc = _workspace.GetDocument(path);
        if (doc == null)
            return null;

        var (source, tree) = doc.Value;
        var model = _workspace.GetSemanticModel(path);
        if (model == null)
            return null;

        var startOffset = PositionUtilities.ToOffset(
            source,
            new Position(
                (int)arg["range"]!["start"]!["line"]!,
                (int)arg["range"]!["start"]!["character"]!
            )
        );
        var endOffset = PositionUtilities.ToOffset(
            source,
            new Position(
                (int)arg["range"]!["end"]!["line"]!,
                (int)arg["range"]!["end"]!["character"]!
            )
        );

        var hints = new List<JObject>();
        var seen = new HashSet<int>();
        CollectInlayHints(tree.Root, source, model, startOffset, endOffset, hints, seen);

        return hints.Count > 0 ? JToken.FromObject(hints) : new JArray();
    }

    private static void CollectInlayHints(
        SyntaxNode node,
        SourceText source,
        Core.SemanticModel model,
        int startOffset,
        int endOffset,
        List<JObject> hints,
        HashSet<int> seen
    )
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                var n = child.AsNode!;
                if (n.Position + n.FullSpan.Length < startOffset)
                    continue;
                if (n.Position > endOffset)
                    break;
                CollectInlayHints(n, source, model, startOffset, endOffset, hints, seen);
                continue;
            }

            if (!child.IsToken)
                continue;
            var token = child.AsToken!;

            if (token.Kind is not (SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken))
                continue;
            if (token.Span.Start < startOffset || token.Span.Start > endOffset)
                continue;

            var parent = token.Parent;
            if (parent is null)
                continue;
            if (
                parent.Kind
                is SyntaxKind.LabelDeclaration
                    or SyntaxKind.SymbolDirective
                    or SyntaxKind.MacroDefinition
            )
                continue;
            if (parent.Kind is not (SyntaxKind.NameExpression or SyntaxKind.LabelOperand))
                continue;

            var symbol = model.ResolveSymbol(token.Text, token.Span.Start);
            if (symbol is null)
                continue;
            if (
                symbol.Kind
                is not (Core.Symbols.SymbolKind.Label or Core.Symbols.SymbolKind.Constant)
            )
                continue;
            if (!seen.Add(token.Span.Start))
                continue;

            var pos = PositionUtilities.ToLspPosition(source, token.Span.Start + token.Span.Length);
            var valueText =
                symbol.Kind == Core.Symbols.SymbolKind.Label
                    ? $"${symbol.Value:X4}"
                    : $"${symbol.Value:X4} ({symbol.Value})";

            hints.Add(
                new JObject
                {
                    ["position"] = JToken.FromObject(
                        new { line = pos.Line, character = pos.Character }
                    ),
                    ["label"] = $" = {valueText}",
                    ["kind"] = 1,
                    ["paddingLeft"] = true,
                }
            );
        }
    }

    // =========================================================================
    // Signature Help
    // =========================================================================

    [JsonRpcMethod("textDocument/signatureHelp")]
    public JToken? SignatureHelp(JToken arg)
    {
        var p = arg.ToObject<TextDocumentPositionParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        var doc = _workspace.GetDocument(path);
        if (doc == null)
            return null;

        var (source, tree) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);

        var token = tree.Root.FindToken(offset);
        if (token == null)
            return null;

        var macroCall = FindEnclosingMacroCall(token);
        if (macroCall == null)
            return null;

        var macroNameToken = macroCall.ChildTokens().FirstOrDefault();
        if (macroNameToken?.Kind != SyntaxKind.IdentifierToken)
            return null;

        var symbol = _workspace
            .GetSemanticModel(path)
            ?.ResolveSymbol(macroNameToken.Text, macroNameToken.Span.Start);
        if (symbol?.Kind != Core.Symbols.SymbolKind.Macro)
            return null;

        int arity = GetMacroArity(symbol, tree);
        if (arity < 0)
            return null;

        int activeParam = Math.Min(
            ComputeActiveParameter(macroCall, offset),
            Math.Max(0, arity - 1)
        );
        var paramLabels = Enumerable.Range(1, arity).Select(i => $"\\{i}").ToArray();
        var parameters = new JArray(paramLabels.Select(l => new JObject { ["label"] = l }));

        return new JObject
        {
            ["signatures"] = new JArray
            {
                new JObject
                {
                    ["label"] = $"{macroNameToken.Text}({string.Join(", ", paramLabels)})",
                    ["parameters"] = parameters,
                    ["activeParameter"] = activeParam,
                },
            },
            ["activeSignature"] = 0,
            ["activeParameter"] = activeParam,
        };
    }

    private static SyntaxNode? FindEnclosingMacroCall(SyntaxToken token)
    {
        var node = token.Parent;
        while (node is not null)
        {
            if (node.Kind == SyntaxKind.MacroCall)
                return node;
            node = node.Parent;
        }
        return null;
    }

    private static int GetMacroArity(Core.Symbols.Symbol macroSymbol, SyntaxTree tree)
    {
        var definitionNode = macroSymbol.DefinitionSite;
        if (definitionNode == null)
            return 0;

        var macroStart =
            definitionNode.Kind == SyntaxKind.MacroDefinition
                ? definitionNode
                : FindNextSiblingOfKind(definitionNode, SyntaxKind.MacroDefinition);

        if (macroStart?.Parent is not { } parent)
            return 0;

        int maxParam = 0;
        bool inBody = false;

        foreach (var child in parent.ChildNodesAndTokens())
        {
            if (!child.IsNode)
                continue;
            var n = child.AsNode!;

            if (!inBody)
            {
                if (n.Kind == SyntaxKind.MacroDefinition && n.Position == macroStart.Position)
                    inBody = true;
                continue;
            }

            if (n.Kind == SyntaxKind.MacroDefinition)
                break;
            ScanMacroParams(n, ref maxParam);
        }

        return maxParam;
    }

    private static SyntaxNode? FindNextSiblingOfKind(SyntaxNode node, SyntaxKind kind)
    {
        if (node.Parent is not { } parent)
            return null;

        bool found = false;
        foreach (var child in parent.ChildNodesAndTokens())
        {
            if (!child.IsNode)
                continue;
            var n = child.AsNode!;
            if (!found)
            {
                if (n.Position == node.Position && n.Kind == node.Kind)
                    found = true;
                continue;
            }
            if (n.Kind == kind)
                return n;
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
                if (
                    t.Kind == SyntaxKind.MacroParamToken
                    && t.Text.Length == 2
                    && t.Text[0] == '\\'
                    && t.Text[1] is >= '1' and <= '9'
                )
                    maxParam = Math.Max(maxParam, t.Text[1] - '0');
            }
            else
            {
                ScanMacroParams(child.AsNode!, ref maxParam);
            }
        }
    }

    private static int ComputeActiveParameter(SyntaxNode macroCall, int cursorOffset)
    {
        int commaCount = 0;
        int parenDepth = 0;

        foreach (var child in macroCall.ChildNodesAndTokens())
        {
            if (child.IsToken)
            {
                var t = child.AsToken!;
                if (t.Span.Start >= cursorOffset)
                    break;
                if (t.Kind == SyntaxKind.OpenParenToken)
                    parenDepth++;
                else if (t.Kind == SyntaxKind.CloseParenToken)
                    parenDepth--;
                else if (t.Kind == SyntaxKind.CommaToken && parenDepth == 0)
                    commaCount++;
            }
            else
            {
                if (child.AsNode!.Position >= cursorOffset)
                    break;
            }
        }

        return commaCount;
    }

    // =========================================================================
    // Rename
    // =========================================================================

    [JsonRpcMethod("textDocument/prepareRename")]
    public object? PrepareRename(JToken arg)
    {
        var p = arg.ToObject<TextDocumentPositionParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        var doc = _workspace.GetDocument(path);
        if (doc == null)
            return null;

        var (source, _) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);
        var resolved = _symbolFinder.ResolveAt(_workspace, path, offset);
        Log($"[prepareRename] path={path} offset={offset} resolved={resolved != null}");

        if (resolved == null)
        {
            Log($"[prepareRename] semanticModel={_workspace.GetSemanticModel(path) != null}");
            return null;
        }

        return new
        {
            range = PositionUtilities.ToLspRange(source, resolved.Token.Span),
            placeholder = resolved.Token.Text,
        };
    }

    [JsonRpcMethod("textDocument/rename")]
    public WorkspaceEdit? Rename(JToken arg)
    {
        var p = arg.ToObject<RenameParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        var doc = _workspace.GetDocument(path);
        if (doc == null)
            return null;

        var (source, _) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);
        var target = _symbolFinder.ResolveAt(_workspace, path, offset);
        if (target == null)
            return null;

        if (_symbolFinder.ValidateRename(_workspace, target, p.NewName) != null)
            return null;

        var occurrences = _symbolFinder.FindAllOccurrences(_workspace, target);
        if (occurrences.Count == 0)
            return null;

        var changes = occurrences
            .GroupBy(o => o.Uri, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => ToUri(g.Key).ToString(),
                g =>
                {
                    if (_workspace.GetDocument(g.Key) is not { } groupDoc)
                        return [];
                    var (groupSource, _) = groupDoc;
                    return g.Select(occ => new TextEdit
                        {
                            Range = PositionUtilities.ToLspRange(groupSource, occ.Token.Span),
                            NewText = p.NewName,
                        })
                        .ToArray();
                }
            );

        return new WorkspaceEdit { Changes = changes };
    }
}
