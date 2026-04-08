using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Symbols;

public sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.OrdinalIgnoreCase);
    // Raw identifiers (#keyword) are case-sensitive; stored separately to avoid OrdinalIgnoreCase collisions.
    private readonly Dictionary<string, Symbol> _rawSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<(string OwnerId, string Name), Symbol> _ownerLocalSymbols =
        new(OwnerNameComparer.Instance);
    private readonly Dictionary<(string OwnerId, string Name), Symbol> _stringConstantSymbols =
        new(OwnerNameComparer.Instance);
    private readonly Dictionary<string, Symbol> _exportedSymbols = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Separate macro namespace. Macros do not collide with constants/labels
    /// and are not found by expression evaluation or constant/label definition.
    /// Keyed by (OwnerId, QualifiedName) for owner-aware lookup.
    /// </summary>
    private readonly Dictionary<(string OwnerId, string Name), Symbol> _macroSymbols =
        new(OwnerNameComparer.Instance);
    private readonly DiagnosticBag _diagnostics;
    private Symbol? _currentGlobalAnchor;
    private SymbolResolutionContext _currentContext;

    // Anonymous label tracking
    private readonly List<Symbol> _anonymousLabels = new();
    private int _anonymousLabelIndex;

    public SymbolTable(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Set the current owner context for non-context-aware API calls.
    /// This allows the ExpressionEvaluator (which uses the old Lookup API)
    /// to resolve symbols in the correct owner scope.
    /// </summary>
    public void SetCurrentContext(SymbolResolutionContext context) => _currentContext = context;

    /// <summary>
    /// Look up a symbol by name. Local labels (.xxx) are qualified against
    /// the current global anchor.
    /// </summary>
    public Symbol? Lookup(string name)
    {
        var key = QualifyName(name);
        // If a current context is set, check owner-local and exported first
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

        // If a current context is set, use owner-local storage
        if (_currentContext.OwnerId != null)
        {
            var ownerKey = (_currentContext.OwnerId, key);
            if (!_ownerLocalSymbols.TryGetValue(ownerKey, out var ownerSym))
            {
                // Check legacy dict for existing symbol with matching or no owner
                var dict = DictFor(key);
                if (dict.TryGetValue(key, out var legacy) &&
                    (legacy.OwnerId == _currentContext.OwnerId ||
                     (legacy.OwnerId == null && legacy.Visibility != SymbolVisibility.Exported)))
                {
                    legacy.OwnerId = _currentContext.OwnerId;
                    _ownerLocalSymbols[ownerKey] = legacy;
                    ownerSym = legacy;
                }
                else
                {
                    ownerSym = new Symbol(key, kind);
                    ownerSym.OwnerId = _currentContext.OwnerId;
                    _ownerLocalSymbols[ownerKey] = ownerSym;
                    dict[key] = ownerSym;
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
        var removed = DictFor(key).Remove(key);
        // Also remove from owner-local storage (find any owner key with this name)
        var toRemove = new List<(string OwnerId, string Name)>();
        foreach (var ownerKey in _ownerLocalSymbols.Keys)
        {
            if (string.Equals(ownerKey.Name, key, StringComparison.OrdinalIgnoreCase))
                toRemove.Add(ownerKey);
        }
        foreach (var k in toRemove)
        {
            _ownerLocalSymbols.Remove(k);
            removed = true;
        }
        // Also remove from string constant storage
        var toRemoveStr = new List<(string OwnerId, string Name)>();
        foreach (var ownerKey in _stringConstantSymbols.Keys)
        {
            if (string.Equals(ownerKey.Name, key, StringComparison.OrdinalIgnoreCase))
                toRemoveStr.Add(ownerKey);
        }
        foreach (var k in toRemoveStr)
        {
            _stringConstantSymbols.Remove(k);
            removed = true;
        }
        // Also check exported storage
        if (_exportedSymbols.Remove(key))
            removed = true;
        // Also remove from macro namespace
        var toRemoveMacro = new List<(string OwnerId, string Name)>();
        foreach (var ownerKey in _macroSymbols.Keys)
        {
            if (string.Equals(ownerKey.Name, key, StringComparison.OrdinalIgnoreCase))
                toRemoveMacro.Add(ownerKey);
        }
        foreach (var k in toRemoveMacro)
        {
            _macroSymbols.Remove(k);
            removed = true;
        }
        return removed;
    }

    /// <summary>
    /// Returns all symbols that are still undefined after Pass 1.
    /// </summary>
    public IEnumerable<Symbol> GetUndefinedSymbols() =>
        _symbols.Values.Concat(_rawSymbols.Values)
            .Concat(_ownerLocalSymbols.Values)
            .Distinct()
            .Where(s => s.State == SymbolState.Undefined);

    /// <summary>
    /// All defined symbols (for export/linker).
    /// </summary>
    public IEnumerable<Symbol> AllSymbols =>
        _symbols.Values.Concat(_rawSymbols.Values)
            .Concat(_ownerLocalSymbols.Values)
            .Concat(_stringConstantSymbols.Values)
            .Concat(_exportedSymbols.Values)
            .Concat(_macroSymbols.Values)
            .Distinct();

    /// <summary>Count of defined symbols, for pre-scan convergence detection.</summary>
    public int DefinedCount =>
        _symbols.Values.Concat(_rawSymbols.Values)
            .Concat(_ownerLocalSymbols.Values)
            .Distinct()
            .Count(s => s.State == SymbolState.Defined);

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
        // If a current context is set, check owner-local and exported first
        if (_currentContext.OwnerId != null)
        {
            if (_ownerLocalSymbols.TryGetValue((_currentContext.OwnerId, qualifiedName), out var local))
                return local;
            if (_exportedSymbols.TryGetValue(qualifiedName, out var exported))
                return exported;
            // Check macro namespace (for LSP features — macros are not in expression namespace)
            if (_macroSymbols.TryGetValue((_currentContext.OwnerId, qualifiedName), out var macro))
                return macro;
        }
        var dict = DictFor(qualifiedName);
        dict.TryGetValue(qualifiedName, out var sym);
        return sym;
    }

    // =========================================================================
    // Owner-aware APIs (context-driven)
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
        // Fall back to legacy global dict for backward compatibility
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
        // Check macro namespace (for LSP features — macros are not in expression namespace)
        if (_macroSymbols.TryGetValue((context.OwnerId, qualifiedName), out var macro))
            return macro;
        // Fall back to legacy global dict for backward compatibility
        var dict = DictFor(qualifiedName);
        dict.TryGetValue(qualifiedName, out var sym);
        return sym;
    }

    /// <summary>
    /// Look up a symbol in the exported namespace only.
    /// </summary>
    public Symbol? LookupExportedOnly(string qualifiedName)
    {
        if (_exportedSymbols.TryGetValue(qualifiedName, out var exported))
            return exported;
        return null;
    }

    /// <summary>
    /// Find an existing symbol in owner-local or adopt from legacy dict.
    /// Returns null if no symbol exists anywhere.
    /// </summary>
    private Symbol? FindOrAdopt(string key, (string OwnerId, string Name) ownerKey)
    {
        if (_ownerLocalSymbols.TryGetValue(ownerKey, out var existing))
            return existing;
        // Check legacy dict for forward refs that haven't been adopted yet.
        // Only adopt if the symbol has no OwnerId (created by old non-context API)
        // AND is not an exported symbol (which has OwnerId=null by design).
        // Also adopt if it has the same OwnerId. Never adopt another owner's symbol.
        var dict = DictFor(key);
        if (dict.TryGetValue(key, out var legacy))
        {
            if (legacy.OwnerId == ownerKey.OwnerId)
                return legacy;
            if (legacy.OwnerId == null && legacy.Visibility != SymbolVisibility.Exported)
            {
                // Adopt into owner-local storage
                legacy.OwnerId = ownerKey.OwnerId;
                _ownerLocalSymbols[ownerKey] = legacy;
                return legacy;
            }
        }
        return null;
    }

    /// <summary>
    /// Register a new symbol in both owner-local and legacy storage.
    /// </summary>
    private void Register(Symbol sym, string key, (string OwnerId, string Name) ownerKey)
    {
        _ownerLocalSymbols[ownerKey] = sym;
        // Also write to legacy dict so non-context-aware code (ExpressionEvaluator)
        // can find the symbol. The non-context Lookup checks owner-local first via
        // _currentContext, so this is a fallback.
        DictFor(key)[key] = sym;
    }

    /// <summary>
    /// Define a label at the given PC value within an owner context.
    /// Global labels become the new anchor for subsequent local labels.
    /// </summary>
    public Symbol DefineLabel(string name, long pc, string? section,
        SyntaxNode? site, SymbolResolutionContext context)
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);
        var existing = FindOrAdopt(key, ownerKey);

        if (existing != null)
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
            existing.OwnerId = context.OwnerId;
            Register(existing, key, ownerKey);
        }

        // Global labels advance the anchor
        if (!name.StartsWith('.'))
            _currentGlobalAnchor = existing;

        return existing;
    }

    /// <summary>
    /// Define an EQU constant within an owner context.
    /// If the symbol is already defined with the same value, this is a silent no-op.
    /// If already defined with a different value, reports a duplicate-definition diagnostic.
    /// </summary>
    public Symbol DefineConstant(string name, long value,
        SyntaxNode? site, SymbolResolutionContext context)
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
                        site != null ? site.FullSpan : default,
                        $"Symbol '{key}' is already defined");
                return existing;
            }

            existing.Define(value, site);
            return existing;
        }

        var sym = new Symbol(key, SymbolKind.Constant);
        sym.Define(value, site);
        sym.OwnerId = context.OwnerId;
        Register(sym, key, ownerKey);
        return sym;
    }

    /// <summary>
    /// Define an EQUS string constant within an owner context.
    /// </summary>
    public Symbol DefineStringConstant(string name, string value,
        SyntaxNode? site, SymbolResolutionContext context)
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);

        // StringConstants are stored in a separate dictionary to avoid interfering
        // with numeric symbol resolution (ExpressionEvaluator uses Lookup which checks _ownerLocalSymbols).
        if (_stringConstantSymbols.TryGetValue(ownerKey, out var existing))
        {
            // Allow redefine for EQUS (string constants are inherently mutable via REDEF)
            return existing;
        }

        // Check for name collision with non-StringConstant symbols in owner-local
        if (_ownerLocalSymbols.TryGetValue(ownerKey, out var conflict) &&
            conflict.State == SymbolState.Defined)
        {
            _diagnostics.Report(
                site != null ? site.FullSpan : default,
                $"Symbol '{key}' is already defined");
            return conflict;
        }

        var sym = new Symbol(key, SymbolKind.StringConstant);
        sym.Define(0, site);
        sym.OwnerId = context.OwnerId;
        _stringConstantSymbols[ownerKey] = sym;
        return sym;
    }

    /// <summary>
    /// Define a macro symbol within an owner context.
    /// Macros live in a separate namespace from constants/labels and do not
    /// collide with them. Duplicate checks apply only within the macro namespace.
    /// </summary>
    public Symbol DefineMacro(string name, SyntaxNode? site,
        SymbolResolutionContext context)
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);

        if (_macroSymbols.TryGetValue(ownerKey, out var existing))
        {
            if (existing.State == SymbolState.Defined)
            {
                _diagnostics.Report(
                    site != null ? site.FullSpan : default,
                    $"Macro '{key}' is already defined");
                return existing;
            }

            existing.Define(0, site);
            return existing;
        }

        var sym = new Symbol(key, SymbolKind.Macro);
        sym.Define(0, site);
        sym.OwnerId = context.OwnerId;
        _macroSymbols[ownerKey] = sym;
        return sym;
    }

    /// <summary>
    /// Define a constant only if not already registered within an owner context.
    /// Used during pre-scan passes.
    /// </summary>
    public Symbol? DefineConstantIfAbsent(string name, long value, SyntaxNode? site,
        SymbolResolutionContext context)
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);
        var existing = FindOrAdopt(key, ownerKey);

        if (existing != null)
        {
            if (existing.State == SymbolState.Undefined)
            {
                existing.Value = value;
                existing.State = SymbolState.Defined;
                return existing;
            }
            return null;
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
        }
        else
        {
            // Check exported symbols — if already promoted, redefine it there
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
    }

    /// <summary>
    /// Create or return a placeholder symbol for a forward reference within an owner context.
    /// </summary>
    public Symbol DeclareForwardRef(string name, SymbolResolutionContext context,
        SymbolKind kind = SymbolKind.Label, SyntaxNode? referenceSite = null)
    {
        var key = QualifyName(name);
        var ownerKey = (context.OwnerId, key);
        var sym = FindOrAdopt(key, ownerKey);

        if (sym == null)
        {
            sym = new Symbol(key, kind);
            sym.OwnerId = context.OwnerId;
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
    public void PromoteExport(string rawName, SyntaxNode directiveSite,
        SymbolResolutionContext context)
    {
        var key = QualifyName(rawName);

        // Check for local label
        if (key.Contains('.'))
        {
            _diagnostics.Report(directiveSite.FullSpan,
                $"Cannot export local label '{key}'.");
            return;
        }

        // Check macro namespace first — macros are not exportable
        var ownerKey = (context.OwnerId, key);
        if (_macroSymbols.ContainsKey(ownerKey))
        {
            _diagnostics.Report(directiveSite.FullSpan,
                $"Cannot export macro '{key}' — macros are not exportable.");
            return;
        }

        // Look up in owner-local storage (including string constants)
        Symbol? sym;
        bool fromStringConstants = false;
        if (_ownerLocalSymbols.TryGetValue(ownerKey, out sym))
        {
            // Found in owner-local
        }
        else if (_stringConstantSymbols.TryGetValue(ownerKey, out sym))
        {
            fromStringConstants = true;
        }
        else if (!DictFor(key).TryGetValue(key, out sym) ||
                 (sym.OwnerId != null && sym.OwnerId != context.OwnerId))
        {
            _diagnostics.Report(directiveSite.FullSpan,
                $"Cannot export undefined symbol '{key}'.");
            return;
        }

        // Check for duplicate in exported namespace
        if (_exportedSymbols.TryGetValue(key, out var existingExport) && existingExport != sym)
        {
            _diagnostics.Report(directiveSite.FullSpan,
                $"Exported symbol '{key}' is already defined by another translation unit.");
            return;
        }

        // Promote: remove from owner-local (or string constants), set visibility, set OwnerId = null, add to exported
        if (fromStringConstants)
            _stringConstantSymbols.Remove(ownerKey);
        else
            _ownerLocalSymbols.Remove(ownerKey);
        sym.Visibility = SymbolVisibility.Exported;
        sym.OwnerId = null;
        _exportedSymbols[key] = sym;
    }

    /// <summary>
    /// Define an anonymous label within an owner context.
    /// </summary>
    public Symbol DefineAnonymousLabel(long pc, string? section, SyntaxNode? site,
        SymbolResolutionContext context)
    {
        var name = $"__anon_{_anonymousLabels.Count}";
        var sym = new Symbol(name, SymbolKind.Label);
        sym.Define(pc, site);
        sym.Section = section;
        sym.OwnerId = context.OwnerId;
        _ownerLocalSymbols[(context.OwnerId, name)] = sym;
        _symbols[name] = sym;
        _anonymousLabels.Add(sym);
        return sym;
    }

    /// <summary>
    /// All symbols including owner-local and exported (for export/linker).
    /// </summary>
    public IEnumerable<Symbol> AllOwnerAwareSymbols =>
        _ownerLocalSymbols.Values
            .Concat(_stringConstantSymbols.Values)
            .Concat(_exportedSymbols.Values)
            .Concat(_macroSymbols.Values);

    /// <summary>
    /// Get all symbols visible to a specific owner: owner-local + all exported.
    /// </summary>
    public IEnumerable<Symbol> GetVisibleSymbols(string ownerId) =>
        _ownerLocalSymbols
            .Where(kv => kv.Key.OwnerId == ownerId)
            .Select(kv => kv.Value)
            .Concat(_stringConstantSymbols
                .Where(kv => kv.Key.OwnerId == ownerId)
                .Select(kv => kv.Value))
            .Concat(_exportedSymbols.Values);

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

    /// <summary>
    /// Equality comparer for (OwnerId, Name) tuples where OwnerId is case-sensitive
    /// and Name is case-insensitive (matching RGBDS symbol naming rules).
    /// </summary>
    private sealed class OwnerNameComparer : IEqualityComparer<(string OwnerId, string Name)>
    {
        public static readonly OwnerNameComparer Instance = new();

        public bool Equals((string OwnerId, string Name) x, (string OwnerId, string Name) y) =>
            StringComparer.Ordinal.Equals(x.OwnerId, y.OwnerId) &&
            NameComparer(x.Name).Equals(x.Name, y.Name);

        public int GetHashCode((string OwnerId, string Name) obj) =>
            HashCode.Combine(
                obj.OwnerId != null ? StringComparer.Ordinal.GetHashCode(obj.OwnerId) : 0,
                obj.Name != null ? NameComparer(obj.Name).GetHashCode(obj.Name) : 0);

        // Raw identifiers (#xxx) are case-sensitive; all others are case-insensitive
        private static StringComparer NameComparer(string name) =>
            name.StartsWith('#') ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    }
}
