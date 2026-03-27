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
                DocumentSymbolProvider = true,
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

    private static Location? FindDefinition(SyntaxNode node, string name, string uri, SourceText source)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var childNode = child.AsNode!;

            if (childNode.Kind == SyntaxKind.LabelDeclaration)
            {
                var labelToken = childNode.ChildTokens().FirstOrDefault();
                if (labelToken != null &&
                    labelToken.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return new Location
                    {
                        Uri = new Uri(uri),
                        Range = PositionUtilities.ToLspRange(source, childNode.Span),
                    };
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

            // Recurse into child nodes (conditionals, macros, repeats, etc.)
            var found = FindDefinition(childNode, name, uri, source);
            if (found != null) return found;
        }

        return null;
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
            "PUSHS", "POPS", "RSRESET", "RSSET", "RB", "RW", "RL",
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
}
