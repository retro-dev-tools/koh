using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Symbols;

public sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Symbol> _rawSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<(string OwnerId, string Name), Symbol> _ownerLocalSymbols = new(
        OwnerNameComparer.Instance
    );
    private readonly Dictionary<(string OwnerId, string Name), Symbol> _stringConstantSymbols = new(
        OwnerNameComparer.Instance
    );
    private readonly Dictionary<string, Symbol> _exportedSymbols = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<(string OwnerId, string Name), Symbol> _macroSymbols = new(
        OwnerNameComparer.Instance
    );
    private readonly Dictionary<string, Symbol> _charmapSymbols = new(
        StringComparer.OrdinalIgnoreCase
    );

    // All Symbol instances exactly once — replaces Concat().Distinct() throughout.
    private readonly HashSet<Symbol> _allSymbols = new(ReferenceEqualityComparer.Instance);

    // Reverse index: qualified name → (OwnerId, Name) keys across the three owner-keyed dicts.
    // Turns Remove() from O(n dict scans) into O(k) where k = owning file count (usually 1).
    private readonly Dictionary<string, List<(string OwnerId, string Name)>> _ownerKeysByName = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly DiagnosticBag _diagnostics;
    private Symbol? _currentGlobalAnchor;
    private SymbolResolutionContext _currentContext;
    private readonly List<Symbol> _anonymousLabels = [];
    private int _anonymousLabelIndex;

    public SymbolTable(DiagnosticBag diagnostics) => _diagnostics = diagnostics;

    public void SetCurrentContext(SymbolResolutionContext context) => _currentContext = context;

    // =========================================================================
    // Private helpers
    // =========================================================================

    private string QualifyName(string name) =>
        name.StartsWith('.') && _currentGlobalAnchor != null
            ? string.Concat(_currentGlobalAnchor.Name, name)
            : name;

    private Dictionary<string, Symbol> DictFor(string key) =>
        key.StartsWith('#') ? _rawSymbols : _symbols;

    private void Track(Symbol sym) => _allSymbols.Add(sym);

    private void RecordOwnerKey((string OwnerId, string Name) key)
    {
        if (!_ownerKeysByName.TryGetValue(key.Name, out var list))
            _ownerKeysByName[key.Name] = list = [];
        list.Add(key);
    }

    private void EraseOwnerKey((string OwnerId, string Name) key)
    {
        if (!_ownerKeysByName.TryGetValue(key.Name, out var list))
            return;
        list.Remove(key);
        if (list.Count == 0)
            _ownerKeysByName.Remove(key.Name);
    }

    private Symbol? FindOrAdopt(string key, (string OwnerId, string Name) ownerKey)
    {
        if (_ownerLocalSymbols.TryGetValue(ownerKey, out var existing))
            return existing;

        var dict = DictFor(key);
        if (!dict.TryGetValue(key, out var legacy))
            return null;

        if (legacy.OwnerId == ownerKey.OwnerId)
            return legacy;

        if (legacy.OwnerId == null && legacy.Visibility != SymbolVisibility.Exported)
        {
            legacy.OwnerId = ownerKey.OwnerId;
            _ownerLocalSymbols[ownerKey] = legacy;
            RecordOwnerKey(ownerKey);
            return legacy;
        }

        return null;
    }

    private void Register(Symbol sym, string key, (string OwnerId, string Name) ownerKey)
    {
        _ownerLocalSymbols[ownerKey] = sym;
        DictFor(key)[key] = sym;
        RecordOwnerKey(ownerKey);
        Track(sym);
    }

    // =========================================================================
    // Legacy (non-context-aware) API
    // =========================================================================

    public Symbol? Lookup(string name)
    {
        var key = QualifyName(name);
        if (_currentContext.OwnerId != null)
        {
            if (_ownerLocalSymbols.TryGetValue((_currentContext.OwnerId, key), out var local))
                return local;
            if (_exportedSymbols.TryGetValue(key, out var exported))
                return exported;
        }
        DictFor(key).TryGetValue(key, out var sym);
        return sym;
    }

    public Symbol DefineLabel(string name, long pc, string? section, SyntaxNode? site = null)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);

        if (dict.TryGetValue(key, out var existing))
        {
            if (existing.State == SymbolState.Defined)
            {
                _diagnostics.Report(
                    site?.FullSpan ?? default,
                    $"Symbol '{key}' is already defined"
                );
                return existing;
            }
            existing.Define(pc, site);
            existing.Section = section;
        }
        else
        {
            existing = new Symbol(key, SymbolKind.Label);
            existing.Define(pc, site);
            existing.Section = section;
            dict[key] = existing;
            Track(existing);
        }

        if (!name.StartsWith('.'))
            _currentGlobalAnchor = existing;
        return existing;
    }

    public Symbol DefineConstant(string name, long value, SyntaxNode? site = null)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);

        if (dict.TryGetValue(key, out var existing))
        {
            if (existing.State == SymbolState.Defined)
            {
                if (existing.Value != value)
                    _diagnostics.Report(
                        site?.FullSpan ?? default,
                        $"Symbol '{key}' is already defined"
                    );
                return existing;
            }
            existing.Define(value, site);
            return existing;
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value, site);
        dict[key] = sym;
        Track(sym);
        return sym;
    }

    public Symbol? DefineConstantIfAbsent(string name, long value, SyntaxNode? site = null)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);

        if (dict.TryGetValue(key, out var existing))
        {
            if (existing.State != SymbolState.Undefined)
                return null;
            existing.Value = value;
            existing.State = SymbolState.Defined;
            return existing;
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value, site);
        dict[key] = sym;
        Track(sym);
        return sym;
    }

    public void DefineOrRedefine(string name, long value)
    {
        var key = QualifyName(name);
        var dict = DictFor(key);

        if (dict.TryGetValue(key, out var existing))
        {
            existing.Value = value;
            existing.State = SymbolState.Defined;
            return;
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value);
        dict[key] = sym;
        Track(sym);
    }

    public Symbol DeclareForwardRef(
        string name,
        SymbolKind kind = SymbolKind.Label,
        SyntaxNode? referenceSite = null
    )
    {
        var key = QualifyName(name);

        if (_currentContext.OwnerId != null)
        {
            var ownerKey = (_currentContext.OwnerId, key);
            if (!_ownerLocalSymbols.TryGetValue(ownerKey, out var ownerSym))
            {
                var dict = DictFor(key);
                if (
                    dict.TryGetValue(key, out var legacy)
                    && (
                        legacy.OwnerId == _currentContext.OwnerId
                        || (
                            legacy.OwnerId == null && legacy.Visibility != SymbolVisibility.Exported
                        )
                    )
                )
                {
                    legacy.OwnerId = _currentContext.OwnerId;
                    _ownerLocalSymbols[ownerKey] = legacy;
                    RecordOwnerKey(ownerKey);
                    ownerSym = legacy;
                }
                else
                {
                    ownerSym = new Symbol(key, kind) { OwnerId = _currentContext.OwnerId };
                    _ownerLocalSymbols[ownerKey] = ownerSym;
                    DictFor(key)[key] = ownerSym;
                    RecordOwnerKey(ownerKey);
                    Track(ownerSym);
                }
            }
            if (referenceSite != null)
                ownerSym.AddReference(referenceSite);
            return ownerSym;
        }

        var globalDict = DictFor(key);
        if (!globalDict.TryGetValue(key, out var sym))
        {
            sym = new Symbol(key, kind);
            globalDict[key] = sym;
            Track(sym);
        }
        if (referenceSite != null)
            sym.AddReference(referenceSite);
        return sym;
    }

    public bool Remove(string name)
    {
        var key = QualifyName(name);
        var removed = false;

        if (DictFor(key).TryGetValue(key, out var sym))
        {
            DictFor(key).Remove(key);
            _allSymbols.Remove(sym);
            removed = true;
        }
        if (_exportedSymbols.TryGetValue(key, out sym))
        {
            _exportedSymbols.Remove(key);
            _allSymbols.Remove(sym);
            removed = true;
        }

        if (_ownerKeysByName.TryGetValue(key, out var ownerKeys))
        {
            foreach (var k in ownerKeys)
            {
                if (_ownerLocalSymbols.TryGetValue(k, out sym))
                {
                    _ownerLocalSymbols.Remove(k);
                    _allSymbols.Remove(sym);
                    removed = true;
                }
                if (_stringConstantSymbols.TryGetValue(k, out sym))
                {
                    _stringConstantSymbols.Remove(k);
                    _allSymbols.Remove(sym);
                    removed = true;
                }
                if (_macroSymbols.TryGetValue(k, out sym))
                {
                    _macroSymbols.Remove(k);
                    _allSymbols.Remove(sym);
                    removed = true;
                }
            }
            _ownerKeysByName.Remove(key);
        }

        return removed;
    }

    // =========================================================================
    // Enumeration — all backed by _allSymbols, no Concat/Distinct
    // =========================================================================

    public IEnumerable<Symbol> GetUndefinedSymbols() =>
        _allSymbols.Where(s => s.State == SymbolState.Undefined);

    public IEnumerable<Symbol> AllSymbols => _allSymbols;

    public int DefinedCount => _allSymbols.Count(s => s.State == SymbolState.Defined);

    // =========================================================================
    // Global anchor / anonymous labels
    // =========================================================================

    public void SetGlobalAnchor(Symbol? anchor) => _currentGlobalAnchor = anchor;

    public string? CurrentGlobalAnchorName => _currentGlobalAnchor?.Name;
    public bool HasGlobalAnchor => _currentGlobalAnchor != null;

    public Symbol DefineAnonymousLabel(long pc, string? section, SyntaxNode? site = null)
    {
        var name = $"__anon_{_anonymousLabels.Count}";
        var sym = new Symbol(name, SymbolKind.Label);
        sym.Define(pc, site);
        sym.Section = section;
        _symbols[name] = sym;
        _anonymousLabels.Add(sym);
        Track(sym);
        return sym;
    }

    /// <summary>
    /// Resolve an anonymous label reference. Positive offset = forward (:+, :++),
    /// negative offset = backward (:-, :--).
    /// </summary>
    public Symbol? ResolveAnonymousRef(int offset)
    {
        var target = offset > 0 ? _anonymousLabelIndex + offset - 1 : _anonymousLabelIndex + offset;
        return (uint)target < (uint)_anonymousLabels.Count ? _anonymousLabels[target] : null;
    }

    public void AdvanceAnonymousIndex() => _anonymousLabelIndex++;

    public void ResetAnonymousIndex() => _anonymousLabelIndex = 0;

    // =========================================================================
    // Direct qualified lookup
    // =========================================================================

    /// <summary>
    /// Direct qualified-name lookup bypassing QualifyName (which depends on
    /// stale _currentGlobalAnchor post-binding). Used by SemanticModel.ResolveSymbol.
    /// </summary>
    public Symbol? LookupQualified(string qualifiedName)
    {
        if (_currentContext.OwnerId != null)
        {
            if (
                _ownerLocalSymbols.TryGetValue(
                    (_currentContext.OwnerId, qualifiedName),
                    out var local
                )
            )
                return local;
            if (_exportedSymbols.TryGetValue(qualifiedName, out var exported))
                return exported;
            if (_macroSymbols.TryGetValue((_currentContext.OwnerId, qualifiedName), out var macro))
                return macro;
        }
        if (_charmapSymbols.TryGetValue(qualifiedName, out var charmap))
            return charmap;
        DictFor(qualifiedName).TryGetValue(qualifiedName, out var sym);
        return sym;
    }

    // =========================================================================
    // Owner-aware API
    // =========================================================================

    /// <summary>
    /// Look up a symbol by raw name within an owner context.
    /// Local labels (.xxx) are qualified against the current global anchor.
    /// Checks owner-local first, then exported.
    /// </summary>
    public Symbol? Lookup(string rawName, SymbolResolutionContext context)
    {
        var key = QualifyName(rawName);
        if (_ownerLocalSymbols.TryGetValue((context.OwnerId, key), out var local))
            return local;
        if (_stringConstantSymbols.TryGetValue((context.OwnerId, key), out var strConst))
            return strConst;
        if (_exportedSymbols.TryGetValue(key, out var exported))
            return exported;
        DictFor(key).TryGetValue(key, out var sym);
        return sym;
    }

    /// <summary>
    /// Direct qualified-name lookup within an owner context.
    /// Checks owner-local first, then exported.
    /// </summary>
    public Symbol? LookupQualified(string qualifiedName, SymbolResolutionContext context)
    {
        if (_ownerLocalSymbols.TryGetValue((context.OwnerId, qualifiedName), out var local))
            return local;
        if (_stringConstantSymbols.TryGetValue((context.OwnerId, qualifiedName), out var strConst))
            return strConst;
        if (_exportedSymbols.TryGetValue(qualifiedName, out var exported))
            return exported;
        if (_macroSymbols.TryGetValue((context.OwnerId, qualifiedName), out var macro))
            return macro;
        if (_charmapSymbols.TryGetValue(qualifiedName, out var charmap))
            return charmap;
        DictFor(qualifiedName).TryGetValue(qualifiedName, out var sym);
        return sym;
    }

    /// <summary>
    /// Look up a symbol in the exported namespace only.
    /// </summary>
    public Symbol? LookupExportedOnly(string qualifiedName)
    {
        _exportedSymbols.TryGetValue(qualifiedName, out var sym);
        return sym;
    }

    /// <summary>
    /// Define a label at the given PC value within an owner context.
    /// Global labels become the new anchor for subsequent local labels.
    /// </summary>
    public Symbol DefineLabel(
        string name,
        long pc,
        string? section,
        SyntaxNode? site,
        SymbolResolutionContext context
    )
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);
        var existing = FindOrAdopt(key, ownerKey);

        if (existing != null)
        {
            if (existing.State == SymbolState.Defined)
            {
                _diagnostics.Report(
                    site?.FullSpan ?? default,
                    $"Symbol '{key}' is already defined"
                );
                return existing;
            }
            existing.Define(pc, site);
            existing.SetDefinitionFilePath(_diagnostics.CurrentFilePath);
            existing.Section = section;
        }
        else
        {
            existing = new Symbol(key, SymbolKind.Label);
            existing.Define(pc, site);
            existing.SetDefinitionFilePath(_diagnostics.CurrentFilePath);
            existing.Section = section;
            existing.OwnerId = context.OwnerId;
            Register(existing, key, ownerKey);
        }

        if (!name.StartsWith('.'))
            _currentGlobalAnchor = existing;
        return existing;
    }

    /// <summary>
    /// Define an EQU constant within an owner context.
    /// If the symbol is already defined with the same value, this is a silent no-op.
    /// If already defined with a different value, reports a duplicate-definition diagnostic.
    /// </summary>
    public Symbol DefineConstant(
        string name,
        long value,
        SyntaxNode? site,
        SymbolResolutionContext context
    )
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);
        var existing = FindOrAdopt(key, ownerKey);

        if (existing != null)
        {
            if (existing.State == SymbolState.Defined)
            {
                if (existing.Value != value)
                    _diagnostics.Report(
                        site?.FullSpan ?? default,
                        $"Symbol '{key}' is already defined"
                    );
                return existing;
            }
            existing.Define(value, site);
            existing.SetDefinitionFilePath(_diagnostics.CurrentFilePath);
            return existing;
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value, site);
        sym.SetDefinitionFilePath(_diagnostics.CurrentFilePath);
        sym.OwnerId = context.OwnerId;
        Register(sym, key, ownerKey);
        return sym;
    }

    /// <summary>
    /// Define an EQUS string constant within an owner context.
    /// </summary>
    public Symbol DefineStringConstant(
        string name,
        string value,
        SyntaxNode? site,
        SymbolResolutionContext context
    )
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);

        if (_stringConstantSymbols.TryGetValue(ownerKey, out var existing))
            return existing;

        if (
            _ownerLocalSymbols.TryGetValue(ownerKey, out var conflict)
            && conflict.State == SymbolState.Defined
        )
        {
            _diagnostics.Report(site?.FullSpan ?? default, $"Symbol '{key}' is already defined");
            return conflict;
        }

        var sym = new Symbol(key, SymbolKind.StringConstant);
        sym.Define(0, site);
        sym.OwnerId = context.OwnerId;
        _stringConstantSymbols[ownerKey] = sym;
        RecordOwnerKey(ownerKey);
        Track(sym);
        return sym;
    }

    /// <summary>
    /// Define a macro symbol within an owner context.
    /// Macros live in a separate namespace from constants/labels and do not
    /// collide with them. Duplicate checks apply only within the macro namespace.
    /// </summary>
    public Symbol DefineMacro(string name, SyntaxNode? site, SymbolResolutionContext context)
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);

        if (_macroSymbols.TryGetValue(ownerKey, out var existing))
        {
            if (existing.State == SymbolState.Defined)
            {
                _diagnostics.Report(site?.FullSpan ?? default, $"Macro '{key}' is already defined");
                return existing;
            }
            existing.Define(0, site);
            existing.SetDefinitionFilePath(_diagnostics.CurrentFilePath);
            return existing;
        }

        var sym = new Symbol(key, SymbolKind.Macro);
        sym.Define(0, site);
        sym.SetDefinitionFilePath(_diagnostics.CurrentFilePath);
        sym.OwnerId = context.OwnerId;
        _macroSymbols[ownerKey] = sym;
        RecordOwnerKey(ownerKey);
        Track(sym);
        return sym;
    }

    /// <summary>
    /// Define a charmap symbol in the charmap namespace.
    /// Charmap names do not collide with constants, labels, or macros.
    /// </summary>
    public Symbol DefineCharMap(string name, SyntaxNode? site)
    {
        if (_charmapSymbols.TryGetValue(name, out var existing))
            return existing;

        var sym = new Symbol(name, SymbolKind.CharMap);
        sym.Define(0, site);
        sym.SetDefinitionFilePath(_diagnostics.CurrentFilePath);
        _charmapSymbols[name] = sym;
        Track(sym);
        return sym;
    }

    /// <summary>
    /// Look up a charmap symbol by name. Returns null if not found.
    /// </summary>
    public Symbol? LookupCharMap(string name)
    {
        _charmapSymbols.TryGetValue(name, out var sym);
        return sym;
    }

    /// <summary>
    /// Define a constant only if not already registered within an owner context.
    /// Used during pre-scan passes.
    /// </summary>
    public Symbol? DefineConstantIfAbsent(
        string name,
        long value,
        SyntaxNode? site,
        SymbolResolutionContext context
    )
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);
        var existing = FindOrAdopt(key, ownerKey);

        if (existing != null)
        {
            if (existing.State != SymbolState.Undefined)
                return null;
            existing.Value = value;
            existing.State = SymbolState.Defined;
            return existing;
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value, site);
        sym.OwnerId = context.OwnerId;
        Register(sym, key, ownerKey);
        return sym;
    }

    /// <summary>
    /// Define or redefine a constant within an owner context (used by FOR loop variables).
    /// Bypasses the duplicate-definition guard.
    /// </summary>
    public void DefineOrRedefine(string name, long value, SymbolResolutionContext context)
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);
        var existing = FindOrAdopt(key, ownerKey);

        if (existing != null)
        {
            existing.Value = value;
            existing.State = SymbolState.Defined;
            return;
        }

        if (_exportedSymbols.TryGetValue(key, out var exported))
        {
            exported.Value = value;
            exported.State = SymbolState.Defined;
            return;
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value);
        sym.OwnerId = context.OwnerId;
        Register(sym, key, ownerKey);
    }

    /// <summary>
    /// Create or return a placeholder symbol for a forward reference within an owner context.
    /// </summary>
    public Symbol DeclareForwardRef(
        string name,
        SymbolResolutionContext context,
        SymbolKind kind = SymbolKind.Label,
        SyntaxNode? referenceSite = null
    )
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);
        var sym = FindOrAdopt(key, ownerKey);

        if (sym == null)
        {
            sym = new Symbol(key, kind) { OwnerId = context.OwnerId };
            Register(sym, key, ownerKey);
        }

        if (referenceSite != null)
            sym.AddReference(referenceSite);
        return sym;
    }

    /// <summary>
    /// Promote a symbol from owner-local to exported namespace.
    /// Validates: undefined → diagnostic, macro → diagnostic, local label → diagnostic,
    /// duplicate export → diagnostic. Otherwise promote.
    /// </summary>
    public void PromoteExport(
        string rawName,
        SyntaxNode directiveSite,
        SymbolResolutionContext context
    )
    {
        var key = QualifyName(rawName);

        if (key.Contains('.'))
        {
            _diagnostics.Report(directiveSite.FullSpan, $"Cannot export local label '{key}'.");
            return;
        }

        var ownerKey = (context.OwnerId, key);
        if (_macroSymbols.ContainsKey(ownerKey))
        {
            _diagnostics.Report(
                directiveSite.FullSpan,
                $"Cannot export macro '{key}' — macros are not exportable."
            );
            return;
        }

        Symbol? sym;
        bool fromStringConstants;

        if (_ownerLocalSymbols.TryGetValue(ownerKey, out sym))
        {
            fromStringConstants = false;
        }
        else if (_stringConstantSymbols.TryGetValue(ownerKey, out sym))
        {
            fromStringConstants = true;
        }
        else if (
            !DictFor(key).TryGetValue(key, out sym)
            || (sym.OwnerId != null && sym.OwnerId != context.OwnerId)
        )
        {
            _diagnostics.Report(directiveSite.FullSpan, $"Cannot export undefined symbol '{key}'.");
            return;
        }
        else
        {
            fromStringConstants = false;
        }

        if (_exportedSymbols.TryGetValue(key, out var existingExport) && existingExport != sym)
        {
            _diagnostics.Report(
                directiveSite.FullSpan,
                $"Exported symbol '{key}' is already defined by another translation unit."
            );
            return;
        }

        if (fromStringConstants)
            _stringConstantSymbols.Remove(ownerKey);
        else
            _ownerLocalSymbols.Remove(ownerKey);
        EraseOwnerKey(ownerKey);

        sym.Visibility = SymbolVisibility.Exported;
        sym.OwnerId = null;
        _exportedSymbols[key] = sym;
    }

    /// <summary>
    /// Define an anonymous label within an owner context.
    /// </summary>
    public Symbol DefineAnonymousLabel(
        long pc,
        string? section,
        SyntaxNode? site,
        SymbolResolutionContext context
    )
    {
        var name = $"__anon_{_anonymousLabels.Count}";
        var ownerKey = (context.OwnerId, name);
        var sym = new Symbol(name, SymbolKind.Label);
        sym.Define(pc, site);
        sym.Section = section;
        sym.OwnerId = context.OwnerId;
        _ownerLocalSymbols[ownerKey] = sym;
        _symbols[name] = sym;
        RecordOwnerKey(ownerKey);
        _anonymousLabels.Add(sym);
        Track(sym);
        return sym;
    }

    public IEnumerable<Symbol> AllOwnerAwareSymbols =>
        _ownerLocalSymbols
            .Values.Concat(_stringConstantSymbols.Values)
            .Concat(_exportedSymbols.Values)
            .Concat(_macroSymbols.Values)
            .Concat(_charmapSymbols.Values);

    /// <summary>
    /// Get all symbols visible to a specific owner: owner-local + all exported.
    /// </summary>
    public IEnumerable<Symbol> GetVisibleSymbols(string ownerId) =>
        _ownerLocalSymbols
            .Where(kv => kv.Key.OwnerId == ownerId)
            .Select(kv => kv.Value)
            .Concat(
                _stringConstantSymbols.Where(kv => kv.Key.OwnerId == ownerId).Select(kv => kv.Value)
            )
            .Concat(_exportedSymbols.Values);

    // =========================================================================
    // OwnerNameComparer — allocation-free; previously called StringComparer
    // factory on every hash/equality operation
    // =========================================================================

    private sealed class OwnerNameComparer : IEqualityComparer<(string OwnerId, string Name)>
    {
        public static readonly OwnerNameComparer Instance = new();

        public bool Equals((string OwnerId, string Name) x, (string OwnerId, string Name) y) =>
            StringComparer.Ordinal.Equals(x.OwnerId, y.OwnerId)
            && (
                x.Name.StartsWith('#')
                    ? StringComparer.Ordinal.Equals(x.Name, y.Name)
                    : StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name)
            );

        public int GetHashCode((string OwnerId, string Name) obj) =>
            HashCode.Combine(
                obj.OwnerId is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.OwnerId),
                obj.Name is null ? 0
                    : obj.Name.StartsWith('#') ? StringComparer.Ordinal.GetHashCode(obj.Name)
                    : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name)
            );
    }
}
