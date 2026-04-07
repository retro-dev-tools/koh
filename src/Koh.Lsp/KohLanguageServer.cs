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

    public KohLanguageServer(JsonRpc rpc)
    {
        _rpc = rpc;
    }

    // =========================================================================
    // Lifecycle
    // =========================================================================

    [JsonRpcMethod("initialize")]
    public InitializeResult Initialize(JToken arg)
    {
        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Full,
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
    // Text document sync
    // =========================================================================

    [JsonRpcMethod("textDocument/didOpen")]
    public void DidOpen(JToken arg)
    {
        var p = arg.ToObject<DidOpenTextDocumentParams>()!;
        var uri = p.TextDocument.Uri.ToString();
        _workspace.OpenDocument(uri, p.TextDocument.Text);
        PublishDiagnosticsFor(uri);
    }

    [JsonRpcMethod("textDocument/didChange")]
    public void DidChange(JToken arg)
    {
        var p = arg.ToObject<DidChangeTextDocumentParams>()!;
        var uri = p.TextDocument.Uri.ToString();
        // Full sync: client sends exactly one change with complete text
        if (p.ContentChanges.Length > 0)
            _workspace.ChangeDocument(uri, p.ContentChanges[^1].Text);
        PublishDiagnosticsFor(uri);
    }

    [JsonRpcMethod("textDocument/didClose")]
    public void DidClose(JToken arg)
    {
        var p = arg.ToObject<DidCloseTextDocumentParams>()!;
        var uri = p.TextDocument.Uri.ToString();
        _workspace.CloseDocument(uri);
        // Clear diagnostics for closed file
        _ = _rpc.NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticParams
        {
            Uri = new Uri(uri),
            Diagnostics = [],
        });
    }

    [JsonRpcMethod("textDocument/didSave")]
    public void DidSave(JToken arg) { }

    // =========================================================================
    // Diagnostics
    // =========================================================================

    private void PublishDiagnosticsFor(string uri)
    {
        var state = _workspace.GetDocumentDiagnostics(uri);
        if (state.Text == null || state.Diagnostics == null) return;

        var lspDiags = new List<Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic>();
        foreach (var diag in state.Diagnostics)
            lspDiags.Add(PositionUtilities.ToLspDiagnostic(diag, state.Text));

        _ = _rpc.NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticParams
        {
            Uri = new Uri(uri),
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
        var doc = _workspace.GetDocument(p.TextDocument.Uri.ToString());
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);
        var token = tree.Root.FindToken(offset);
        if (token == null) return null;

        var content = GetHoverContent(token);
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

    private string? GetHoverContent(SyntaxToken token)
    {
        if (IsInstructionKeyword(token.Kind))
            return GetInstructionHover(token.Text.ToUpperInvariant());

        if (token.Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken)
        {
            var model = _workspace.GetModel();
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
        var doc = _workspace.GetDocument(p.TextDocument.Uri.ToString());
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
            return FindDefinition(tree.Root, token.Text, p.TextDocument.Uri.ToString(),
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
        var doc = _workspace.GetDocument(p.TextDocument.Uri.ToString());
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
            FindReferences(tree.Root, token.Text, new Uri(p.TextDocument.Uri.ToString()),
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
        var doc = _workspace.GetDocument(p.TextDocument.Uri.ToString());
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
        var items = new List<CompletionItem>(StaticCompletionItems);

        var model = _workspace.GetModel();
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
        var uri = p.TextDocument.Uri.ToString();
        var doc = _workspace.GetDocument(uri);
        if (doc == null) return null;

        var (_, tree) = doc.Value;
        var data = SemanticTokenEncoder.Encode(tree);

        return new SemanticTokens { Data = data };
    }

    // =========================================================================
    // Rename
    // =========================================================================

    [JsonRpcMethod("textDocument/prepareRename")]
    public object? PrepareRename(JToken arg)
    {
        var p = arg.ToObject<TextDocumentPositionParams>()!;
        var uri = p.TextDocument.Uri.ToString();
        var doc = _workspace.GetDocument(uri);
        if (doc == null) return null;

        var (source, _) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);

        var resolved = _symbolFinder.ResolveAt(_workspace, uri, offset);
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
        var uri = p.TextDocument.Uri.ToString();
        var doc = _workspace.GetDocument(uri);
        if (doc == null) return null;

        var (source, _) = doc.Value;
        var offset = PositionUtilities.ToOffset(source, p.Position);

        var target = _symbolFinder.ResolveAt(_workspace, uri, offset);
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

            changes[groupUri] = edits;
        }

        return new WorkspaceEdit { Changes = changes };
    }
}
