using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Symbols;

public sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.OrdinalIgnoreCase);
    // Raw identifiers (#keyword) are case-sensitive; stored separately to avoid OrdinalIgnoreCase collisions.
    private readonly Dictionary<string, Symbol> _rawSymbols = new(StringComparer.Ordinal);
    private readonly DiagnosticBag _diagnostics;
    private Symbol? _currentGlobalAnchor;

    // Anonymous label tracking
    private readonly List<Symbol> _anonymousLabels = new();
    private int _anonymousLabelIndex;

    public SymbolTable(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Look up a symbol by name. Local labels (.xxx) are qualified against
    /// the current global anchor.
    /// </summary>
    public Symbol? Lookup(string name)
    {
        var key = QualifyName(name);
        DictFor(key).TryGetValue(key, out var sym);
        return sym;
    }

    /// <summary>
    /// Define a label at the given PC value. Global labels become the new
    /// anchor for subsequent local labels.
    /// </summary>
    public Symbol DefineLabel(string name, long pc, string? section, SyntaxNode? site = null)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);

        if (dict.TryGetValue(key, out var existing))
        {
            if (existing.State == SymbolState.Defined)
            {
                _diagnostics.Report(
                    site != null ? site.FullSpan : default,
                    $"Symbol '{key}' is already defined");
                return existing;
            }

            // Forward ref being resolved
            existing.Define(pc, site);
            existing.Section = section;
        }
        else
        {
            existing = new Symbol(key, SymbolKind.Label);
            existing.Define(pc, site);
            existing.Section = section;
            dict[key] = existing;
        }

        // Global labels advance the anchor
        if (!name.StartsWith('.'))
            _currentGlobalAnchor = existing;

        return existing;
    }

    /// <summary>
    /// Define an EQU constant with a known value.
    /// If the symbol is already defined with the same value (e.g. pre-defined by PreScanEquConstants),
    /// this is a silent no-op. If the symbol is already defined with a different value, reports a
    /// duplicate-definition diagnostic (genuine user-source redefinition error).
    /// </summary>
    public Symbol DefineConstant(string name, long value, SyntaxNode? site = null)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);

        if (dict.TryGetValue(key, out var existing))
        {
            if (existing.State == SymbolState.Defined)
            {
                // Same value: pre-scan idempotency — no error, no change.
                // Different value: genuine source-level duplicate — report error.
                if (existing.Value != value)
                    _diagnostics.Report(
                        site != null ? site.FullSpan : default,
                        $"Symbol '{key}' is already defined");
                return existing;
            }

            existing.Define(value, site);
            return existing;
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value, site);
        dict[key] = sym;
        return sym;
    }

    /// <summary>
    /// Define an EQU constant only if the name is not already registered.
    /// Used during pre-scan passes where EarlyDefineEqu will later run the
    /// definitive definition (with duplicate-definition checking). This avoids
    /// producing a duplicate-definition diagnostic for the pre-scan attempt.
    /// </summary>
    public Symbol? DefineConstantIfAbsent(string name, long value, SyntaxNode? site = null)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);
        if (dict.TryGetValue(key, out var existing))
        {
            // Allow defining a forward-referenced (Undefined) symbol
            if (existing.State == SymbolState.Undefined)
            {
                existing.Value = value;
                existing.State = SymbolState.Defined;
                return existing;
            }
            return null; // already defined — don't redefine
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value, site);
        dict[key] = sym;
        return sym;
    }

    /// <summary>
    /// Define or redefine a constant (used by FOR loop variables).
    /// Bypasses the duplicate-definition guard.
    /// </summary>
    public void DefineOrRedefine(string name, long value)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);
        if (dict.TryGetValue(key, out var existing))
        {
            existing.Value = value;
            existing.State = SymbolState.Defined;
        }
        else
        {
            var sym = new Symbol(key, SymbolKind.Constant);
            sym.Define(value);
            dict[key] = sym;
        }
    }

    /// <summary>
    /// Create or return a placeholder symbol for a forward reference.
    /// </summary>
    /// <param name="name">Raw (possibly local) symbol name.</param>
    /// <param name="kind">
    /// The expected kind for this reference. Defaults to <see cref="SymbolKind.Label"/>.
    /// Pass <see cref="SymbolKind.Constant"/> when the reference appears inside a constant
    /// expression (e.g. an EQU RHS), so the placeholder kind matches what the eventual
    /// definition will produce.
    /// </param>
    /// <param name="referenceSite">Optional red-node site for IDE "find all references".</param>
    public Symbol DeclareForwardRef(string name, SymbolKind kind = SymbolKind.Label,
        SyntaxNode? referenceSite = null)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);

        if (!dict.TryGetValue(key, out var sym))
        {
            sym = new Symbol(key, kind);
            dict[key] = sym;
        }

        if (referenceSite != null)
            sym.AddReference(referenceSite);

        return sym;
    }

    /// <summary>
    /// Remove a symbol by name. Used by PURGE directive.
    /// </summary>
    public bool Remove(string name)
    {
        var key = QualifyName(name);
        return DictFor(key).Remove(key);
    }

    /// <summary>
    /// Returns all symbols that are still undefined after Pass 1.
    /// </summary>
    public IEnumerable<Symbol> GetUndefinedSymbols() =>
        _symbols.Values.Concat(_rawSymbols.Values).Where(s => s.State == SymbolState.Undefined);

    /// <summary>
    /// All defined symbols (for export/linker).
    /// </summary>
    public IEnumerable<Symbol> AllSymbols => _symbols.Values.Concat(_rawSymbols.Values);

    /// <summary>Count of defined symbols, for pre-scan convergence detection.</summary>
    public int DefinedCount =>
        _symbols.Values.Count(s => s.State == SymbolState.Defined) +
        _rawSymbols.Values.Count(s => s.State == SymbolState.Defined);

    /// <summary>
    /// Set the current global anchor manually (used when resuming binding context).
    /// </summary>
    public void SetGlobalAnchor(Symbol? anchor) => _currentGlobalAnchor = anchor;

    /// <summary>Current global anchor name, for recording on PatchEntry.</summary>
    public string? CurrentGlobalAnchorName => _currentGlobalAnchor?.Name;
    public bool HasGlobalAnchor => _currentGlobalAnchor != null;

    /// <summary>
    /// Define an anonymous label at the given PC. Returns the generated symbol.
    /// </summary>
    public Symbol DefineAnonymousLabel(long pc, string? section, SyntaxNode? site = null)
    {
        var name = $"__anon_{_anonymousLabels.Count}";
        var sym = new Symbol(name, SymbolKind.Label);
        sym.Define(pc, site);
        sym.Section = section;
        _symbols[name] = sym;
        _anonymousLabels.Add(sym);
        return sym;
    }

    /// <summary>
    /// Resolve an anonymous label reference. Positive offset = forward (:+, :++),
    /// negative offset = backward (:-, :--).
    /// </summary>
    public Symbol? ResolveAnonymousRef(int offset)
    {
        int target = _anonymousLabelIndex + offset;
        // For forward refs (offset > 0), target is _anonymousLabelIndex + offset - 1
        // because _anonymousLabelIndex points to the next anon label to be defined.
        // For backward refs (offset < 0), target is _anonymousLabelIndex + offset
        // because the previous label is at _anonymousLabelIndex - 1.
        if (offset > 0)
            target = _anonymousLabelIndex + offset - 1;
        else
            target = _anonymousLabelIndex + offset;

        if (target >= 0 && target < _anonymousLabels.Count)
            return _anonymousLabels[target];
        return null;
    }

    /// <summary>
    /// Advance the anonymous label index during Pass 2 when encountering
    /// an anonymous label declaration.
    /// </summary>
    public void AdvanceAnonymousIndex() => _anonymousLabelIndex++;

    /// <summary>Reset the anonymous label index for Pass 2.</summary>
    public void ResetAnonymousIndex() => _anonymousLabelIndex = 0;

    /// <summary>
    /// Direct qualified-name lookup bypassing QualifyName (which depends on
    /// stale _currentGlobalAnchor post-binding). Used by SemanticModel.ResolveSymbol.
    /// </summary>
    public Symbol? LookupQualified(string qualifiedName)
    {
        var dict = DictFor(qualifiedName);
        dict.TryGetValue(qualifiedName, out var sym);
        return sym;
    }

    private string QualifyName(string name)
    {
        if (name.StartsWith('.') && _currentGlobalAnchor != null)
            return $"{_currentGlobalAnchor.Name}{name}";
        return name;
    }

    /// <summary>
    /// Returns the correct symbol dictionary for the given (already-qualified) key.
    /// Raw identifiers (starting with '#') use a case-sensitive dictionary so that
    /// '#DEF' and '#def' remain distinct. All other names use the case-insensitive dictionary.
    /// </summary>
    private Dictionary<string, Symbol> DictFor(string key) =>
        key.StartsWith('#') ? _rawSymbols : _symbols;
}
