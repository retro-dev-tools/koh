using System.Text;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Koh.Lsp;

public sealed class KohLanguageServer
{
    private readonly JsonRpc _rpc;
    private readonly Workspace _workspace = new();
    private readonly SymbolFinder _symbolFinder = new();
    private bool _shutdownReceived;
    private string? _rootPath;

    public KohLanguageServer(JsonRpc rpc)
    {
        _rpc = rpc;
    }

    private void Log(string message)
    {
        _ = _rpc.NotifyAsync("window/logMessage", new { type = 3, message }); // 3 = Info
    }

    // =========================================================================
    // Lifecycle
    // =========================================================================

    [JsonRpcMethod("initialize")]
    public JToken Initialize(JToken arg)
    {
        // Set CWD to workspace root so FileSystemResolver resolves INCLUDEs correctly
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
                    TriggerCharacters = new[] { "." },
                    ResolveProvider = false,
                },
            },
        };

        // Add capabilities not available in this version of the LSP protocol package
        var json = JToken.FromObject(result);
        json["capabilities"]!["inlayHintProvider"] = true;
        json["capabilities"]!["signatureHelpProvider"] = JToken.FromObject(new
        {
            triggerCharacters = new[] { "," },
        });
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

    /// <summary>
    /// Convert an LSP document URI to a local file path.
    /// VS Code sends URIs like "file:///c%3A/projekty/foo" where the colon is
    /// percent-encoded. We decode the path component and strip the leading slash
    /// on Windows so "c:\projekty\foo" is returned instead of "/c:/projekty/foo".
    /// </summary>
    private static string ToFilePath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        // On Windows, strip leading "/" from "/c:/..." paths
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..];
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string ToFilePath(string uriString) => ToFilePath(new Uri(uriString));

    private static Uri ToUri(string filePath) => new Uri("file:///" + filePath.Replace('\\', '/'));

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
        Log($"[didOpen] primaryProject = {(ctx != null ? $"{ctx.Name} (entry={ctx.EntrypointPath})" : "null")}");
        PublishDiagnosticsForAllOpen();
    }

    [JsonRpcMethod("textDocument/didChange")]
    public void DidChange(JToken arg)
    {
        var p = arg.ToObject<DidChangeTextDocumentParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        // Full sync: client sends exactly one change with complete text
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
        // Clear diagnostics for closed file
        _ = _rpc.NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticParams
        {
            Uri = ToUri(path),
            Diagnostics = [],
        });
        // Republish diagnostics for remaining open files (closing an include may affect them)
        PublishDiagnosticsForAllOpen();
    }

    [JsonRpcMethod("textDocument/didSave")]
    public void DidSave(JToken arg)
    {
        var uri = arg["textDocument"]?["uri"]?.ToString();
        if (uri == null) return;

        var path = ToFilePath(new Uri(uri));

        if (Path.GetFileName(path).Equals("koh.yaml", StringComparison.OrdinalIgnoreCase) && _rootPath != null)
        {
            _workspace.ReloadConfiguration(_rootPath);
        }

        // Republish diagnostics for all open files (saved file may affect others)
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
        if (state.Text == null || state.Diagnostics == null) return;

        Log($"[diag] {path} → {state.Diagnostics.Count} diagnostics");
        if (state.Diagnostics.Count > 0 && state.Diagnostics.Count <= 10)
        {
            foreach (var d in state.Diagnostics)
                Log($"[diag]   {d.Severity}: {d.Message} (file={d.FilePath ?? "null"})");
        }

        var lspDiags = new List<Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic>();
        foreach (var diag in state.Diagnostics)
            lspDiags.Add(PositionUtilities.ToLspDiagnostic(diag, state.Text));

        _ = _rpc.NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticParams
        {
            Uri = ToUri(path),
            Diagnostics = lspDiags.ToArray(),
        });
    }

    // =========================================================================
    // Hover
    // =========================================================================

    [JsonRpcMethod("textDocument/hover")]
    public Hover? Hover(JToken arg)
    {
        var p = arg.ToObject<TextDocumentPositionParams>()!;
        var doc = _workspace.GetDocument(ToFilePath(p.TextDocument.Uri));
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);
        var token = tree.Root.FindToken(offset);
        if (token == null) return null;

        var content = GetHoverContent(token, ToFilePath(p.TextDocument.Uri));
        if (content == null) return null;

        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = content,
            },
            Range = PositionUtilities.ToLspRange(source, token.Span),
        };
    }

    private string? GetHoverContent(SyntaxToken token, string path)
    {
        if (IsInstructionKeyword(token.Kind))
            return GetInstructionHover(token.Text.ToUpperInvariant());

        if (token.Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken)
        {
            var model = _workspace.GetModel(path);
            if (model == null) return null;
            var sym = model.Symbols.FirstOrDefault(s =>
                s.Name.Equals(token.Text, StringComparison.OrdinalIgnoreCase));
            if (sym == null) return null;
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

    private static string? GetInstructionHover(string mnemonic) => mnemonic switch
    {
        "NOP" => "**NOP** — No operation (1 byte, 4 cycles)",
        "LD" => "**LD** — Load (copy) value between registers/memory",
        "LDH" => "**LDH** — Load to/from high RAM ($FF00+n)",
        "LDI" => "**LDI** — Load and increment HL (LD [HL+], A / LD A, [HL+])",
        "LDD" => "**LDD** — Load and decrement HL (LD [HL-], A / LD A, [HL-])",
        "ADD" => "**ADD** — Add to accumulator or HL/SP",
        "ADC" => "**ADC** — Add with carry",
        "SUB" => "**SUB** — Subtract from accumulator",
        "SBC" => "**SBC** — Subtract with carry",
        "AND" => "**AND** — Bitwise AND with accumulator",
        "OR" => "**OR** — Bitwise OR with accumulator",
        "XOR" => "**XOR** — Bitwise XOR with accumulator",
        "CP" => "**CP** — Compare with accumulator (subtract without storing result)",
        "INC" => "**INC** — Increment register or memory",
        "DEC" => "**DEC** — Decrement register or memory",
        "PUSH" => "**PUSH** — Push register pair onto stack",
        "POP" => "**POP** — Pop register pair from stack",
        "JP" => "**JP** — Jump to address",
        "JR" => "**JR** — Jump relative (-128 to +127)",
        "CALL" => "**CALL** — Call subroutine (push PC, jump)",
        "RET" => "**RET** — Return from subroutine",
        "RETI" => "**RETI** — Return and enable interrupts",
        "RST" => "**RST** — Restart (fast call to fixed address)",
        "HALT" => "**HALT** — Halt CPU until interrupt",
        "STOP" => "**STOP** — Stop CPU and LCD",
        "DI" => "**DI** — Disable interrupts",
        "EI" => "**EI** — Enable interrupts",
        "DAA" => "**DAA** — Decimal adjust accumulator (BCD)",
        "CPL" => "**CPL** — Complement accumulator (flip all bits)",
        "CCF" => "**CCF** — Complement carry flag",
        "SCF" => "**SCF** — Set carry flag",
        "RLCA" => "**RLCA** — Rotate A left circular",
        "RLA" => "**RLA** — Rotate A left through carry",
        "RRCA" => "**RRCA** — Rotate A right circular",
        "RRA" => "**RRA** — Rotate A right through carry",
        "RLC" => "**RLC** — Rotate left circular",
        "RL" => "**RL** — Rotate left through carry",
        "RRC" => "**RRC** — Rotate right circular",
        "RR" => "**RR** — Rotate right through carry",
        "SLA" => "**SLA** — Shift left arithmetic",
        "SRA" => "**SRA** — Shift right arithmetic (preserves sign)",
        "SRL" => "**SRL** — Shift right logical",
        "SWAP" => "**SWAP** — Swap upper and lower nibbles",
        "BIT" => "**BIT** — Test bit",
        "SET" => "**SET** — Set bit",
        "RES" => "**RES** — Reset (clear) bit",
        _ => null,
    };

    private static bool IsInstructionKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.NopKeyword && kind <= SyntaxKind.LdhKeyword;

    // =========================================================================
    // Go-to-definition (recursive tree walk)
    // =========================================================================

    [JsonRpcMethod("textDocument/definition")]
    public Location? Definition(JToken arg)
    {
        var p = arg.ToObject<TextDocumentPositionParams>()!;
        var doc = _workspace.GetDocument(ToFilePath(p.TextDocument.Uri));
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);
        var token = tree.Root.FindToken(offset);
        if (token == null || token.Kind is not SyntaxKind.IdentifierToken and not SyntaxKind.LocalLabelToken)
            return null;

        bool isLocalLabel = token.Kind == SyntaxKind.LocalLabelToken;

        if (isLocalLabel)
        {
            // Local labels are file-scoped — only search current document
            string? scopeName = FindEnclosingGlobalLabel(tree.Root, offset);
            return FindDefinition(tree.Root, token.Text, ToFilePath(p.TextDocument.Uri),
                source, true, scopeName);
        }

        foreach (var uri in _workspace.OpenDocumentUris)
        {
            var otherDoc = _workspace.GetDocument(uri);
            if (otherDoc == null) continue;
            var (otherSource, otherTree) = otherDoc.Value;

            var result = FindDefinition(otherTree.Root, token.Text, uri, otherSource);
            if (result != null) return result;
        }

        return null;
    }

    private static Location? FindDefinition(SyntaxNode node, string name, string uri,
        SourceText source, bool isLocalLabel = false, string? requiredScope = null)
    {
        string? currentScope = null;
        return FindDefinitionWalk(node, name, uri, source, isLocalLabel, requiredScope, ref currentScope);
    }

    private static Location? FindDefinitionWalk(SyntaxNode node, string name, string uri,
        SourceText source, bool isLocalLabel, string? requiredScope, ref string? currentScope)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var childNode = child.AsNode!;

            if (childNode.Kind == SyntaxKind.LabelDeclaration)
            {
                var labelToken = childNode.ChildTokens().FirstOrDefault();
                if (labelToken != null)
                {
                    // Track global label scope
                    if (labelToken.Kind == SyntaxKind.IdentifierToken)
                        currentScope = labelToken.Text;

                    if (labelToken.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        // For local labels, match only LocalLabelTokens in the same scope
                        if (isLocalLabel)
                        {
                            if (labelToken.Kind != SyntaxKind.LocalLabelToken) continue;
                            if (!string.Equals(currentScope, requiredScope, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        return new Location
                        {
                            Uri = new Uri(uri),
                            Range = PositionUtilities.ToLspRange(source, childNode.Span),
                        };
                    }
                }
            }
            else if (childNode.Kind == SyntaxKind.SymbolDirective)
            {
                var nameToken = childNode.ChildTokens().FirstOrDefault();
                if (nameToken != null &&
                    nameToken.Kind == SyntaxKind.IdentifierToken &&
                    nameToken.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return new Location
                    {
                        Uri = new Uri(uri),
                        Range = PositionUtilities.ToLspRange(source, childNode.Span),
                    };
                }
            }

            var found = FindDefinitionWalk(childNode, name, uri, source, isLocalLabel, requiredScope, ref currentScope);
            if (found != null) return found;
        }

        return null;
    }

    // =========================================================================
    // Find references (recursive tree walk)
    // =========================================================================

    [JsonRpcMethod("textDocument/references")]
    public Location[] References(JToken arg)
    {
        var p = arg.ToObject<ReferenceParams>()!;
        var doc = _workspace.GetDocument(ToFilePath(p.TextDocument.Uri));
        if (doc == null) return [];

        var (source, tree) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);
        var token = tree.Root.FindToken(offset);
        if (token == null || token.Kind is not SyntaxKind.IdentifierToken and not SyntaxKind.LocalLabelToken)
            return [];

        bool includeDeclaration = p.Context?.IncludeDeclaration ?? false;
        bool isLocalLabel = token.Kind == SyntaxKind.LocalLabelToken;

        string? scopeName = isLocalLabel
            ? FindEnclosingGlobalLabel(tree.Root, offset)
            : null;

        var locations = new List<Location>();

        if (isLocalLabel)
        {
            // Local labels are file-scoped — only search the current document
            FindReferences(tree.Root, token.Text, new Uri(ToFilePath(p.TextDocument.Uri)),
                source, locations, includeDeclaration, true, scopeName);
        }
        else
        {
            foreach (var uri in _workspace.OpenDocumentUris)
            {
                var otherDoc = _workspace.GetDocument(uri);
                if (otherDoc == null) continue;
                var (otherSource, otherTree) = otherDoc.Value;

                FindReferences(otherTree.Root, token.Text, new Uri(uri), otherSource, locations,
                    includeDeclaration, false, null);
            }
        }

        return locations.ToArray();
    }

    /// <summary>
    /// Find the name of the most recent global label before the given position.
    /// Local labels in RGBDS are scoped to the preceding global label.
    /// </summary>
    private static string? FindEnclosingGlobalLabel(SyntaxNode root, int position)
    {
        string? lastGlobal = null;
        foreach (var child in root.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var node = child.AsNode!;
            if (node.Position > position) break;

            if (node.Kind == SyntaxKind.LabelDeclaration)
            {
                var nameToken = node.ChildTokens().FirstOrDefault();
                if (nameToken != null && nameToken.Kind == SyntaxKind.IdentifierToken)
                    lastGlobal = nameToken.Text;
            }
        }
        return lastGlobal;
    }

    private static void FindReferences(SyntaxNode root, string name, Uri documentUri,
        SourceText source, List<Location> results, bool includeDeclaration,
        bool isLocalLabel, string? requiredScope)
    {
        // For local labels, track the current global label scope as we walk
        string? currentScope = null;
        FindReferencesWalk(root, name, documentUri, source, results,
            includeDeclaration, isLocalLabel, requiredScope, ref currentScope);
    }

    private static void FindReferencesWalk(SyntaxNode node, string name, Uri documentUri,
        SourceText source, List<Location> results, bool includeDeclaration,
        bool isLocalLabel, string? requiredScope, ref string? currentScope)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                var childNode = child.AsNode!;
                // Track global label scope changes at top level
                if (childNode.Kind == SyntaxKind.LabelDeclaration)
                {
                    var labelToken = childNode.ChildTokens().FirstOrDefault();
                    if (labelToken != null && labelToken.Kind == SyntaxKind.IdentifierToken)
                        currentScope = labelToken.Text;
                }

                FindReferencesWalk(childNode, name, documentUri, source, results,
                    includeDeclaration, isLocalLabel, requiredScope, ref currentScope);
                continue;
            }

            if (!child.IsToken) continue;
            var token = child.AsToken!;

            if ((token.Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken) &&
                token.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                // For local labels, only match LocalLabelTokens in the same scope
                if (isLocalLabel)
                {
                    if (token.Kind != SyntaxKind.LocalLabelToken) continue;
                    if (!string.Equals(currentScope, requiredScope, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                bool isDecl = token.Parent?.Kind is SyntaxKind.LabelDeclaration or SyntaxKind.SymbolDirective;
                if (isDecl && !includeDeclaration) continue;

                results.Add(new Location
                {
                    Uri = documentUri,
                    Range = PositionUtilities.ToLspRange(source, token.Span),
                });
            }
        }
    }

    // =========================================================================
    // Document symbols (recursive tree walk)
    // =========================================================================

    [JsonRpcMethod("textDocument/documentSymbol")]
    public DocumentSymbol[]? DocumentSymbol(JToken arg)
    {
        var p = arg.ToObject<DocumentSymbolParams>()!;
        var doc = _workspace.GetDocument(ToFilePath(p.TextDocument.Uri));
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var symbols = new List<DocumentSymbol>();
        CollectSymbols(tree.Root, source, symbols);
        return symbols.ToArray();
    }

    private static void CollectSymbols(SyntaxNode node, SourceText source, List<DocumentSymbol> symbols)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var childNode = child.AsNode!;

            switch (childNode.Kind)
            {
                case SyntaxKind.LabelDeclaration:
                {
                    var nameToken = childNode.ChildTokens().FirstOrDefault();
                    if (nameToken != null)
                    {
                        symbols.Add(new DocumentSymbol
                        {
                            Name = nameToken.Text,
                            Kind = SymbolKind.Function,
                            Range = PositionUtilities.ToLspRange(source, childNode.Span),
                            SelectionRange = PositionUtilities.ToLspRange(source, nameToken.Span),
                        });
                    }
                    break;
                }
                case SyntaxKind.SymbolDirective:
                {
                    var tokens = childNode.ChildTokens().ToList();
                    if (tokens.Count >= 2 && tokens[0].Kind == SyntaxKind.IdentifierToken &&
                        tokens[1].Kind is SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword)
                    {
                        symbols.Add(new DocumentSymbol
                        {
                            Name = tokens[0].Text,
                            Kind = SymbolKind.Constant,
                            Range = PositionUtilities.ToLspRange(source, childNode.Span),
                            SelectionRange = PositionUtilities.ToLspRange(source, tokens[0].Span),
                        });
                    }
                    break;
                }
                case SyntaxKind.SectionDirective:
                {
                    var strToken = childNode.ChildTokens()
                        .FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
                    if (strToken != null)
                    {
                        var sectionName = strToken.Text.Length >= 2 ? strToken.Text[1..^1] : strToken.Text;
                        symbols.Add(new DocumentSymbol
                        {
                            Name = sectionName,
                            Kind = SymbolKind.Module,
                            Range = PositionUtilities.ToLspRange(source, childNode.Span),
                            SelectionRange = PositionUtilities.ToLspRange(source, strToken.Span),
                        });
                    }
                    break;
                }
            }

            // Recurse into child nodes
            CollectSymbols(childNode, source, symbols);
        }
    }

    // =========================================================================
    // Completion
    // =========================================================================

    private static readonly CompletionItem[] StaticCompletionItems = BuildStaticCompletions();

    private static CompletionItem[] BuildStaticCompletions()
    {
        var items = new List<CompletionItem>();

        var mnemonics = new[]
        {
            "nop", "ld", "ldh", "ldi", "ldd", "add", "adc", "sub", "sbc", "and", "or", "xor", "cp",
            "inc", "dec", "push", "pop", "jp", "jr", "call", "ret", "reti", "rst",
            "halt", "stop", "di", "ei", "daa", "cpl", "ccf", "scf",
            "rlca", "rla", "rrca", "rra", "rlc", "rl", "rrc", "rr",
            "sla", "sra", "srl", "swap", "bit", "set", "res",
        };
        foreach (var m in mnemonics)
            items.Add(new CompletionItem { Label = m, Kind = CompletionItemKind.Keyword, Detail = "SM83 instruction" });

        var directives = new[]
        {
            "SECTION", "DB", "DW", "DL", "DS", "EQU", "EQUS", "DEF", "REDEF", "MACRO", "ENDM",
            "IF", "ELIF", "ELSE", "ENDC", "REPT", "FOR", "ENDR",
            "INCLUDE", "INCBIN", "EXPORT", "PURGE", "OPT",
            "CHARMAP", "NEWCHARMAP", "SETCHARMAP", "PUSHC", "POPC",
            "ASSERT", "STATIC_ASSERT", "WARN", "FAIL", "PRINT", "PRINTLN",
            "PUSHS", "POPS", "PUSHO", "POPO", "RSRESET", "RSSET", "RB", "RW", "RL", "ALIGN",
            "UNION", "NEXTU", "ENDU", "LOAD", "ENDL",
        };
        foreach (var d in directives)
            items.Add(new CompletionItem { Label = d, Kind = CompletionItemKind.Keyword, Detail = "Directive" });

        var registers = new[] { "a", "b", "c", "d", "e", "h", "l", "af", "bc", "de", "hl", "sp" };
        foreach (var r in registers)
            items.Add(new CompletionItem { Label = r, Kind = CompletionItemKind.Variable, Detail = "Register" });

        return items.ToArray();
    }

    [JsonRpcMethod("textDocument/completion")]
    public CompletionItem[]? Completion(JToken arg)
    {
        var p = arg.ToObject<CompletionParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        var items = new List<CompletionItem>(StaticCompletionItems);

        var model = _workspace.GetModel(path);
        if (model != null)
        {
            foreach (var sym in model.Symbols)
            {
                items.Add(new CompletionItem
                {
                    Label = sym.Name,
                    Kind = sym.Kind == Core.Symbols.SymbolKind.Constant
                        ? CompletionItemKind.Constant
                        : CompletionItemKind.Function,
                    Detail = $"{sym.Kind}: ${sym.Value:X4}",
                });
            }
        }

        return items.ToArray();
    }

    // =========================================================================
    // Semantic Tokens
    // =========================================================================

    [JsonRpcMethod("textDocument/semanticTokens/full")]
    public SemanticTokens? SemanticTokensFull(JToken arg)
    {
        var p = arg.ToObject<SemanticTokensParams>()!;
        var path = ToFilePath(p.TextDocument.Uri);
        var doc = _workspace.GetDocument(path);
        if (doc == null) return null;

        var (_, tree) = doc.Value;
        var data = SemanticTokenEncoder.Encode(tree);

        return new SemanticTokens { Data = data };
    }

    // =========================================================================
    // Inlay Hints
    // =========================================================================

    [JsonRpcMethod("textDocument/inlayHint")]
    public JToken? InlayHint(JToken arg)
    {
        var uri = new Uri(arg["textDocument"]!["uri"]!.ToString());
        var path = ToFilePath(uri);
        var doc = _workspace.GetDocument(path);
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var model = _workspace.GetSemanticModel(path);
        if (model == null) return null;

        // Parse the requested range
        var startLine = (int)arg["range"]!["start"]!["line"]!;
        var startChar = (int)arg["range"]!["start"]!["character"]!;
        var endLine = (int)arg["range"]!["end"]!["line"]!;
        var endChar = (int)arg["range"]!["end"]!["character"]!;

        var startOffset = PositionUtilities.ToOffset(source, new Position(startLine, startChar));
        var endOffset = PositionUtilities.ToOffset(source, new Position(endLine, endChar));

        var hints = new List<JObject>();
        var seen = new HashSet<int>();

        CollectInlayHints(tree.Root, source, model, startOffset, endOffset, hints, seen);

        return hints.Count > 0 ? JToken.FromObject(hints) : new JArray();
    }

    private static void CollectInlayHints(SyntaxNode node, SourceText source,
        Core.SemanticModel model, int startOffset, int endOffset,
        List<JObject> hints, HashSet<int> seen)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                var childNode = child.AsNode!;
                // Skip if entirely outside range
                if (childNode.Position + childNode.FullSpan.Length < startOffset) continue;
                if (childNode.Position > endOffset) break;

                CollectInlayHints(childNode, source, model, startOffset, endOffset, hints, seen);
                continue;
            }

            if (!child.IsToken) continue;
            var token = child.AsToken!;

            // Only identifier/local label tokens
            if (token.Kind is not SyntaxKind.IdentifierToken and not SyntaxKind.LocalLabelToken)
                continue;

            // Must be within range
            if (token.Span.Start < startOffset || token.Span.Start > endOffset)
                continue;

            // Must be in a reference context, not a declaration
            var parent = token.Parent;
            if (parent == null) continue;
            if (parent.Kind is SyntaxKind.LabelDeclaration or SyntaxKind.SymbolDirective or SyntaxKind.MacroDefinition)
                continue;
            if (parent.Kind is not SyntaxKind.NameExpression and not SyntaxKind.LabelOperand)
                continue;

            // Resolve semantically
            var symbol = model.ResolveSymbol(token.Text, token.Span.Start);
            if (symbol == null) continue;

            // Only Label and Constant get hints — not StringConstant, not Macro
            if (symbol.Kind is not Core.Symbols.SymbolKind.Label and not Core.Symbols.SymbolKind.Constant)
                continue;

            // Dedupe by position
            if (!seen.Add(token.Span.Start)) continue;

            // Build hint
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
                ["kind"] = 1, // Type hint
                ["paddingLeft"] = true,
            });
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
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);

        // Find enclosing MacroCall node
        var token = tree.Root.FindToken(offset);
        if (token == null) return null;

        var macroCall = FindEnclosingMacroCall(token);
        if (macroCall == null) return null;

        // Resolve macro symbol
        var macroNameToken = macroCall.ChildTokens().FirstOrDefault();
        if (macroNameToken == null || macroNameToken.Kind != SyntaxKind.IdentifierToken)
            return null;

        var model = _workspace.GetSemanticModel(path);
        if (model == null) return null;

        var symbol = model.ResolveSymbol(macroNameToken.Text, macroNameToken.Span.Start);
        if (symbol == null || symbol.Kind != Core.Symbols.SymbolKind.Macro)
            return null;

        // Determine arity from macro definition body
        int arity = GetMacroArity(symbol, tree);
        if (arity < 0) return null;

        // Compute active parameter by counting commas before cursor
        int activeParam = ComputeActiveParameter(macroCall, offset);

        // Build parameter list \1..\N
        var parameters = new JArray();
        for (int i = 1; i <= arity; i++)
            parameters.Add(new JObject
            {
                ["label"] = $"\\{i}",
            });

        return new JObject
        {
            ["signatures"] = new JArray
            {
                new JObject
                {
                    ["label"] = $"{macroNameToken.Text}({string.Join(", ", Enumerable.Range(1, arity).Select(i => $"\\{i}"))})",
                    ["parameters"] = parameters,
                    ["activeParameter"] = Math.Min(activeParam, Math.Max(0, arity - 1)),
                },
            },
            ["activeSignature"] = 0,
            ["activeParameter"] = Math.Min(activeParam, Math.Max(0, arity - 1)),
        };
    }

    private static SyntaxNode? FindEnclosingMacroCall(SyntaxToken token)
    {
        var node = token.Parent;
        while (node != null)
        {
            if (node.Kind == SyntaxKind.MacroCall)
                return node;
            node = node.Parent;
        }
        return null;
    }

    /// <summary>
    /// Determine macro arity by scanning the definition body for \1..\9 references.
    /// Returns the highest parameter index found, or 0 if no parameters are used.
    /// </summary>
    private static int GetMacroArity(Core.Symbols.Symbol macroSymbol, SyntaxTree tree)
    {
        var definitionNode = macroSymbol.DefinitionSite;
        if (definitionNode == null) return 0;

        // The DefinitionSite may be the MacroDefinition (MACRO keyword) node.
        // The macro body nodes are siblings between this node and the closing
        // MacroDefinition (ENDM keyword) node in the parent CompilationUnit.
        SyntaxNode? macroStart = null;
        if (definitionNode.Kind == SyntaxKind.MacroDefinition)
            macroStart = definitionNode;
        else if (definitionNode.Kind == SyntaxKind.LabelDeclaration)
        {
            // Find the MacroDefinition sibling after the label
            macroStart = FindNextSiblingOfKind(definitionNode, SyntaxKind.MacroDefinition);
        }

        if (macroStart == null) return 0;

        // Scan all sibling nodes between MACRO and ENDM for parameter references.
        // Red tree nodes are created on-the-fly during iteration, so compare by position.
        int maxParam = 0;
        var parent = macroStart.Parent;
        if (parent == null) return 0;

        bool inBody = false;
        foreach (var child in parent.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var childNode = child.AsNode!;

            if (childNode.Kind == SyntaxKind.MacroDefinition && childNode.Position == macroStart.Position)
            {
                inBody = true;
                continue;
            }
            if (inBody)
            {
                // Stop at the closing ENDM (another MacroDefinition)
                if (childNode.Kind == SyntaxKind.MacroDefinition)
                    break;

                ScanMacroParams(childNode, ref maxParam);
            }
        }

        return maxParam;
    }

    private static SyntaxNode? FindNextSiblingOfKind(SyntaxNode node, SyntaxKind kind)
    {
        var parent = node.Parent;
        if (parent == null) return null;

        bool foundNode = false;
        foreach (var child in parent.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            // Red tree nodes are created on-the-fly; compare by position
            if (child.AsNode!.Position == node.Position && child.AsNode!.Kind == node.Kind)
            { foundNode = true; continue; }
            if (foundNode && child.AsNode!.Kind == kind)
                return child.AsNode;
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

    /// <summary>
    /// Count commas before the cursor position in a macro call to determine the active parameter.
    /// Handles parenthesized expressions and nested commas correctly.
    /// </summary>
    private static int ComputeActiveParameter(SyntaxNode macroCall, int cursorOffset)
    {
        int commaCount = 0;
        int parenDepth = 0;

        foreach (var child in macroCall.ChildNodesAndTokens())
        {
            if (child.IsToken)
            {
                var t = child.AsToken!;
                if (t.Span.Start >= cursorOffset) break;

                if (t.Kind == SyntaxKind.OpenParenToken) parenDepth++;
                else if (t.Kind == SyntaxKind.CloseParenToken) parenDepth--;
                else if (t.Kind == SyntaxKind.CommaToken && parenDepth == 0)
                    commaCount++;
            }
            else
            {
                var n = child.AsNode!;
                if (n.Position >= cursorOffset) break;
                // Don't count commas inside nested expressions
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
        if (doc == null) return null;

        var (source, _) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);

        var resolved = _symbolFinder.ResolveAt(_workspace, path, offset);
        if (resolved == null) return null;

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
        if (doc == null) return null;

        var (source, _) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);

        var target = _symbolFinder.ResolveAt(_workspace, path, offset);
        if (target == null) return null;

        // Validate
        var error = _symbolFinder.ValidateRename(_workspace, target, p.NewName);
        if (error != null) return null;

        // Find all occurrences
        var occurrences = _symbolFinder.FindAllOccurrences(_workspace, target);
        if (occurrences.Count == 0) return null;

        // Build WorkspaceEdit grouped by URI
        var changes = new Dictionary<string, TextEdit[]>();

        foreach (var group in occurrences.GroupBy(o => o.Uri, StringComparer.OrdinalIgnoreCase))
        {
            var groupUri = group.Key;
            var groupDoc = _workspace.GetDocument(groupUri);
            if (groupDoc == null) continue;
            var (groupSource, _) = groupDoc.Value;

            // Determine the replacement text for each occurrence
            var edits = group.Select(occ =>
            {
                // For local labels, the stored name is qualified (e.g. "Global.local")
                // but the token text is just ".local". Preserve the dot prefix form.
                string newText;
                if (occ.Token.Kind == Core.Syntax.SyntaxKind.LocalLabelToken)
                    newText = p.NewName; // caller provides ".newname"
                else
                    newText = p.NewName;

                return new TextEdit
                {
                    Range = PositionUtilities.ToLspRange(groupSource, occ.Token.Span),
                    NewText = newText,
                };
            }).ToArray();

            changes[ToUri(groupUri).ToString()] = edits;
        }

        return new WorkspaceEdit { Changes = changes };
    }
}
