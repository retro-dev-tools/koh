using System.Collections.Immutable;
using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Roslyn semantic-model plumbing: a real CSharpCompilation is available after every tree rewrite, as a
// resolution oracle for MethodLowerer's symbol-first lookups (intrinsic recognition, call/field/member
// resolution, unresolved-name diagnostic text — see MethodLowerer.cs). Lowering decisions still come
// entirely from Koh's own C-like typing (CsType); Roslyn only identifies which declaration a name/member
// refers to, never a type's width/signedness. Building the compilation is deferred until something
// actually consults CSharpSemantics (see CSharpSemantics below): per-Lower() cost for a program whose
// lowering never needs a symbol (rare — nearly everything routes through CSharpSemantics now) is then
// just constructing a handful of Lazy<T> wrappers, not real Roslyn binding work.
public sealed partial class CSharpFrontend
{
    /// <summary>A <see cref="MetadataReference"/> for every assembly the current runtime has loaded (the
    /// trusted-platform-assembly list), built once per process. Works identically for a TUnit test host
    /// and the in-proc MSBuild task — both run on the plain .NET 10 runtime; <c>Koh.Compiler</c> is never
    /// AOT-published. Empty (never null) if the runtime exposes no TPA list, so a caller degrades to
    /// <see cref="CSharpSemantics.Disabled"/>-like behavior (a null compilation) instead of throwing.</summary>
    private static readonly Lazy<ImmutableArray<MetadataReference>> TrustedPlatformReferences = new(
        () =>
        {
            if (
                AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa
                || tpa.Length == 0
            )
                return ImmutableArray<MetadataReference>.Empty;
            var builder = ImmutableArray.CreateBuilder<MetadataReference>();
            foreach (
                var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            )
            {
                try
                {
                    builder.Add(MetadataReference.CreateFromFile(path));
                }
                catch (IOException)
                {
                    // A TPA entry that no longer exists on disk; skip it rather than fail the whole set.
                }
                catch (BadImageFormatException)
                {
                    // Not a loadable managed assembly (e.g. a native TPA entry); skip it.
                }
            }
            return builder.ToImmutable();
        }
    );

    // `BlankNamespacing` erases the source's own `using` directives, but BCL names it still refers to
    // (Int128, MathF, BitConverter, ...) are written as bare simple names, exactly as if `using System;`
    // were in effect. `CSharpCompilationOptions.Usings` only applies to Script-kind trees, not a Regular
    // compilation like this one, so the `global using System;` that makes these resolve lives in
    // IntrinsicsStub's generated source instead (a `global using` in any tree applies to every tree in
    // the compilation). This only affects binding inside the semantic model, not the wrapped source text
    // BlankNamespacing/Report() work over, so it can't move a span.
    private static readonly CSharpCompilationOptions SemanticsCompilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        allowUnsafe: true,
        nullableContextOptions: NullableContextOptions.Disable
    );

    /// <summary>Build the (deferred) semantic-model oracle for the final wrapped tree(s). <paramref
    /// name="mainTree"/> must be <c>root.SyntaxTree</c> taken AFTER every rewrite (the softfloat
    /// re-parse, then <see cref="TransformIterators"/>) — building the compilation from an earlier tree
    /// would make every node identity in the compilation stale. <paramref name="instancesTree"/> is the
    /// second, constructed tree housing monomorphized generic instances (see
    /// <see cref="BuildInstancesTree"/>), or null for a program with no generic instances. The actual
    /// <c>CSharpCompilation.Create</c> call (and TPA reference load, on first use process-wide) only
    /// happens if a caller consults the result — see <see cref="CSharpSemantics"/>.</summary>
    private static CSharpSemantics BuildSemantics(SyntaxTree mainTree, SyntaxTree? instancesTree) =>
        new(
            mainTree,
            instancesTree,
            () =>
            {
                var references = TrustedPlatformReferences.Value;
                if (references.IsEmpty)
                    return null;
                var trees = instancesTree is null
                    ? new[] { mainTree, IntrinsicsStub.Tree }
                    : new[] { mainTree, instancesTree, IntrinsicsStub.Tree };
                return CSharpCompilation.Create(
                    "KohCSharpFrontend",
                    trees,
                    references,
                    SemanticsCompilationOptions
                );
            }
        );

    /// <summary>Test-only entry point: parses and wraps <paramref name="source"/> exactly as
    /// <see cref="LowerCore"/> does (through every tree rewrite, plus generic-instance synthesis and its
    /// instances tree), then builds semantics for the result, without lowering any method body. Lets tests
    /// assert against the real production tree/compilation shape instead of a re-implementation that could
    /// drift from it.</summary>
    internal static (CompilationUnitSyntax Root, CSharpSemantics Semantics) BuildSemanticsForTest(
        string source
    )
    {
        var diagnostics = new DiagnosticBag();
        var root = PrepareRoot(SourceText.From(source, "test.cs"), diagnostics);
        if (root is null)
            return (SyntaxFactory.CompilationUnit(), CSharpSemantics.Disabled);
        var genericTemplates = CollectGenericTemplates(root, diagnostics);
        var genericInstances = SynthesizeGenericInstances(root, genericTemplates);
        var instancesTree = BuildInstancesTree(genericInstances);
        return (root, BuildSemantics(root.SyntaxTree, instancesTree));
    }
}

/// <summary>
/// Roslyn as a resolution oracle over the final wrapped tree: a lazy <see cref="SemanticModel"/>, an
/// <see cref="InTree"/> guard, pre-resolved stub symbols for the intrinsic surface
/// (<see cref="IntrinsicsStub"/>), and symbol-keyed indexes (<see cref="Methods"/>, <see cref="Globals"/>,
/// <see cref="ModuleConsts"/>, <see cref="Enums"/>, <see cref="Structs"/>, <see cref="Classes"/>, and —
/// since Stage-2 P4 — <see cref="GenericInstances"/>) that <see cref="MethodLowerer"/> consults
/// symbol-first for name/member/call resolution, falling back to its own string-keyed tables only when no
/// symbol resolves: no compilation built, a genuine resolution failure, or a generic call whose type
/// arguments were inferred rather than written out (no <c>&lt;...&gt;</c> syntax to mangle a suffix from —
/// still unsupported, unchanged by P4). A generic instance's own body is no longer detached syntax, see
/// <see cref="CSharpFrontend.BuildInstancesTree"/>; the syntax-based mangled-name lookup in
/// <see cref="MethodLowerer.LowerCall"/> remains as a fallback until Stage-2 P5 deletes it.
///
/// Everything that requires real Roslyn binding (the underlying <see cref="CSharpCompilation"/>, the
/// <see cref="SemanticModel"/>, and the stub type symbols) is behind its own <see cref="Lazy{T}"/>, so
/// constructing an instance is cheap — the compiler doesn't do any of that work unless a caller actually
/// reads one of these members. A <c>Lower()</c> whose program needs no resolution at all (rare) still
/// pays nothing beyond constructing the <see cref="Lazy{T}"/> wrappers.
/// </summary>
// Stage-2 P3 adds SymOrCandidate alongside Sym: a callee/field lookup that would otherwise fall
// through to the string-keyed fallback purely because Roslyn's OWN rules (overload resolution,
// accessibility) reject Koh-legal code now gets one more chance via CandidateSymbols before giving up.
// Stage-2 P4 adds GenericInstances/TryGetGenericInstance: a generic call's own resolved symbol (via
// SymOrCandidate) now routes straight to its monomorphized instance, keyed by the template symbol
// (OriginalDefinition) and the call's own mangled type-argument suffix — the syntax-based MangleGeneric
// switch in MethodLowerer.LowerCall stays only as the fallback for an inferred-type-argument call.
internal sealed class CSharpSemantics
{
    /// <summary>Used when a <see cref="CSharpSemantics"/> is needed with no source tree at all (e.g. a
    /// <see cref="CSharpFrontend.PrepareRoot"/> failure before any tree exists). Every lookup safely
    /// returns null/false rather than throwing, so a consuming site's string-based fallback always still
    /// runs — semantics can only ever be a strict improvement, never a regression.</summary>
    public static readonly CSharpSemantics Disabled = new();

    private readonly SyntaxTree? _mainTree;
    private readonly SyntaxTree? _instancesTree;
    private readonly Lazy<CSharpCompilation?> _compilation;
    private readonly Lazy<SemanticModel?> _mainModel;
    private readonly Lazy<SemanticModel?> _instancesModel;
    private readonly Lazy<INamedTypeSymbol?> _hardwareType;
    private readonly Lazy<INamedTypeSymbol?> _gbType;
    private readonly Lazy<INamedTypeSymbol?> _memType;
    private readonly Lazy<INamedTypeSymbol?> _interruptAttributeType;

    /// <summary>The underlying compilation, or null if none could be built (the runtime exposes no TPA
    /// list, or this is <see cref="Disabled"/>). Exposed for diagnostics (Roslyn's own errors, e.g.
    /// CS0266/CS0214 on Koh-legal-but-C#-illegal code, are never a gate — see the design's diagnostics
    /// policy — but <see cref="DiagnosticsAt"/> reads whitelisted messages from here, and tests use it to
    /// confirm symbols still resolve despite such errors being present). Building it is what triggers the
    /// actual Roslyn work — first access is the expensive one.</summary>
    public Compilation? Compilation => _compilation.Value;

    public INamedTypeSymbol? HardwareType => _hardwareType.Value;
    public INamedTypeSymbol? GbType => _gbType.Value;
    public INamedTypeSymbol? MemType => _memType.Value;
    public INamedTypeSymbol? InterruptAttributeType => _interruptAttributeType.Value;

    // ---- Deferred declaration registration -----------------------------------------------------------
    //
    // The declaration passes (CSharpFrontend.cs Pass 1/1.5, CSharpFrontend.Declarations.cs) call
    // Register* alongside every string-keyed table insert. Register* is O(1) list-append — it never
    // touches the semantic model. Each symbol-keyed index below only walks its registration list (and so
    // only forces the compilation/model, via DeclaredSym) the first time a caller actually reads the
    // corresponding property; a Lower() call whose result nobody inspects this way costs nothing beyond
    // the appends. A monomorphized generic instance's declaration node lives in the instances tree (see
    // CSharpFrontend.BuildInstancesTree), not the main tree, but DeclaredSym resolves either tree (see
    // InTree/ModelFor below) — so it registers into Methods exactly like any other declaration.

    private readonly List<(SyntaxNode Decl, CsMethod Value)> _methodRegs = [];
    private readonly List<(SyntaxNode Decl, (IrGlobal Global, CsType Type) Value)> _globalRegs = [];
    private readonly List<(SyntaxNode Decl, (CsType Type, long Value) Value)> _constRegs = [];
    private readonly List<(SyntaxNode Decl, CsEnum Value)> _enumRegs = [];
    private readonly List<(SyntaxNode Decl, CsStruct Value)> _structRegs = [];
    private readonly List<(SyntaxNode Decl, CsClass Value)> _classRegs = [];

    // One entry per monomorphized generic instance, recorded against its TEMPLATE's declaration node
    // (a main-tree node, so DeclaredSym always resolves it — unlike the instance's own decl, which lives
    // in the instances tree but resolves there just as well; the template is used as the key because a
    // generic call site's resolved symbol's OriginalDefinition IS the template, never any one instance).
    private readonly List<(
        SyntaxNode TemplateDecl,
        string Suffix,
        CsMethod Value
    )> _genericInstanceRegs = [];

    // Not `readonly`: a readonly field can only be assigned directly within a constructor body or a
    // field initializer (which in turn can't reference other instance members at all, even inside a
    // deferred lambda — CS0236). InitIndexes() assigns these identically from both constructors, so
    // Disabled and a real instance build the same way; each Lazy only walks its registration list (and so
    // only forces the compilation, via DeclaredSym) the first time a caller reads the property.
    private Lazy<IReadOnlyDictionary<IMethodSymbol, CsMethod>> _methodIndex = null!;
    private Lazy<IReadOnlyDictionary<IFieldSymbol, (IrGlobal Global, CsType Type)>> _globalIndex =
        null!;
    private Lazy<IReadOnlyDictionary<IFieldSymbol, (CsType Type, long Value)>> _moduleConstIndex =
        null!;
    private Lazy<IReadOnlyDictionary<INamedTypeSymbol, CsEnum>> _enumIndex = null!;
    private Lazy<IReadOnlyDictionary<INamedTypeSymbol, CsStruct>> _structIndex = null!;
    private Lazy<IReadOnlyDictionary<INamedTypeSymbol, CsClass>> _classIndex = null!;
    private Lazy<
        IReadOnlyDictionary<IMethodSymbol, IReadOnlyDictionary<string, CsMethod>>
    > _genericInstanceIndex = null!;

    /// <summary>Record a method declaration (top-level function or instance method) under its
    /// declaration node; materialized into <see cref="Methods"/> keyed by <see cref="IMethodSymbol"/> on
    /// first read. A no-op on <see cref="Disabled"/> (there is no tree to resolve against, so keeping the
    /// entry would only leak memory in that shared singleton).</summary>
    public void RegisterMethod(SyntaxNode decl, CsMethod method)
    {
        if (_mainTree is not null)
            _methodRegs.Add((decl, method));
    }

    /// <summary>Record one monomorphized generic instance (Stage-2 P4): <paramref name="templateDecl"/> is
    /// the generic template's own declaration node (a main-tree node — the template is never itself
    /// lowered, but it is where the routing lookup's key, <see cref="TryGetGenericInstance"/>'s <c>template</c>
    /// argument, comes from), <paramref name="mangledSuffix"/> is the concrete type arguments' mangled
    /// suffix (<see cref="CSharpFrontend.MangleSuffix"/>) that produced this specific instance, and
    /// <paramref name="method"/> is the instance's own <see cref="CsMethod"/> — the same one <see
    /// cref="RegisterMethod"/> records under the instance's own (non-generic) symbol. Materialized into
    /// <see cref="GenericInstances"/> on first read. A no-op on <see cref="Disabled"/>, like every other
    /// Register* method.</summary>
    public void RegisterGenericInstance(
        SyntaxNode templateDecl,
        string mangledSuffix,
        CsMethod method
    )
    {
        if (_mainTree is not null)
            _genericInstanceRegs.Add((templateDecl, mangledSuffix, method));
    }

    /// <summary>Record a static field/global declaration (its <see cref="VariableDeclaratorSyntax"/>);
    /// materialized into <see cref="Globals"/> keyed by <see cref="IFieldSymbol"/> on first read.</summary>
    public void RegisterGlobal(SyntaxNode decl, IrGlobal global, CsType type)
    {
        if (_mainTree is not null)
            _globalRegs.Add((decl, (global, type)));
    }

    /// <summary>Record a module-level <c>const</c> declaration (its <see cref="VariableDeclaratorSyntax"/>);
    /// materialized into <see cref="ModuleConsts"/> keyed by <see cref="IFieldSymbol"/> on first read.</summary>
    public void RegisterConst(SyntaxNode decl, CsType type, long value)
    {
        if (_mainTree is not null)
            _constRegs.Add((decl, (type, value)));
    }

    /// <summary>Record an enum declaration; materialized into <see cref="Enums"/> keyed by
    /// <see cref="INamedTypeSymbol"/> on first read.</summary>
    public void RegisterEnum(SyntaxNode decl, CsEnum value)
    {
        if (_mainTree is not null)
            _enumRegs.Add((decl, value));
    }

    /// <summary>Record a struct declaration; materialized into <see cref="Structs"/> keyed by
    /// <see cref="INamedTypeSymbol"/> on first read.</summary>
    public void RegisterStruct(SyntaxNode decl, CsStruct value)
    {
        if (_mainTree is not null)
            _structRegs.Add((decl, value));
    }

    /// <summary>Record a class declaration; materialized into <see cref="Classes"/> keyed by
    /// <see cref="INamedTypeSymbol"/> on first read.</summary>
    public void RegisterClass(SyntaxNode decl, CsClass value)
    {
        if (_mainTree is not null)
            _classRegs.Add((decl, value));
    }

    /// <summary>Method declarations (top-level functions, instance methods, and — since Stage-2 P2 — each
    /// monomorphized generic instance) keyed by their Roslyn symbol. Empty until read; reading forces the
    /// compilation (see the class remarks). Consulted by <see cref="MethodLowerer.LowerCall"/> and
    /// <see cref="MethodLowerer.LowerInstanceCall"/> via <c>OriginalDefinition</c>, so a
    /// constructed/inherited call signature still maps to the registered template — a generic
    /// <em>template</em> itself is never registered (it is never lowered). A constructed generic call's
    /// own symbol (whose <c>OriginalDefinition</c> is the template, not any instance) therefore never hits
    /// this index directly; since Stage-2 P4, <see cref="MethodLowerer.LowerCall"/> instead routes such a
    /// call through
    /// <see cref="GenericInstances"/>/<see cref="TryGetGenericInstance"/>, keyed by that same
    /// <c>OriginalDefinition</c> plus the call's own mangled type-argument suffix — the instance found
    /// that way is the very same <see cref="CsMethod"/> also indexed here under its own (non-generic)
    /// symbol.</summary>
    public IReadOnlyDictionary<IMethodSymbol, CsMethod> Methods => _methodIndex.Value;

    /// <summary>Static fields (globals) keyed by their Roslyn symbol.</summary>
    public IReadOnlyDictionary<IFieldSymbol, (IrGlobal Global, CsType Type)> Globals =>
        _globalIndex.Value;

    /// <summary>Module-level <c>const</c> fields keyed by their Roslyn symbol (folded value; there is no
    /// backing <see cref="IrGlobal"/> for a const).</summary>
    public IReadOnlyDictionary<IFieldSymbol, (CsType Type, long Value)> ModuleConsts =>
        _moduleConstIndex.Value;

    /// <summary>Enum types keyed by their Roslyn symbol.</summary>
    public IReadOnlyDictionary<INamedTypeSymbol, CsEnum> Enums => _enumIndex.Value;

    /// <summary>Struct types keyed by their Roslyn symbol.</summary>
    public IReadOnlyDictionary<INamedTypeSymbol, CsStruct> Structs => _structIndex.Value;

    /// <summary>Class types keyed by their Roslyn symbol.</summary>
    public IReadOnlyDictionary<INamedTypeSymbol, CsClass> Classes => _classIndex.Value;

    /// <summary>Monomorphized generic instances (Stage-2 P4), keyed by their template's Roslyn symbol
    /// (an unbound generic method definition — the same symbol a call's own <c>OriginalDefinition</c>
    /// produces) and then by the call's own mangled type-argument suffix (<see
    /// cref="CSharpFrontend.MangleSuffix"/>), so two instances of the same template at different type
    /// arguments (or two same-named templates at different arities/owners) stay distinct. Prefer <see
    /// cref="TryGetGenericInstance"/> over reading this directly.</summary>
    public IReadOnlyDictionary<
        IMethodSymbol,
        IReadOnlyDictionary<string, CsMethod>
    > GenericInstances => _genericInstanceIndex.Value;

    /// <summary>Look up the monomorphized instance of generic method <paramref name="template"/> (its
    /// <c>OriginalDefinition</c> symbol) specialized at <paramref name="suffix"/> (the call site's own
    /// mangled type-argument suffix). Used by <see cref="MethodLowerer.LowerCall"/> to route a generic
    /// call symbol-first: on a miss (an inferred type argument with no explicit <c>&lt;...&gt;</c> call
    /// syntax to mangle, or a template this frontend never registered any instance for) the caller falls
    /// back to the pre-migration syntax-based mangled-name lookup, exactly as before this index
    /// existed.</summary>
    public bool TryGetGenericInstance(IMethodSymbol template, string suffix, out CsMethod method)
    {
        if (
            GenericInstances.TryGetValue(template, out var bySuffix)
            && bySuffix.TryGetValue(suffix, out var found)
        )
        {
            method = found;
            return true;
        }
        method = null!;
        return false;
    }

    /// <summary>Build a symbol-keyed dictionary from a registration list, resolving each declaration
    /// node's symbol on demand. A node whose <see cref="DeclaredSym"/> is null (a foreign node, no
    /// compilation available, or resolution failure) or resolves to an unexpected symbol kind is silently
    /// skipped: callers never need to filter these out themselves.</summary>
    private Dictionary<TSymbol, TValue> Materialize<TSymbol, TValue>(
        List<(SyntaxNode Decl, TValue Value)> registrations
    )
        where TSymbol : class, ISymbol
    {
        var dict = new Dictionary<TSymbol, TValue>(SymbolEqualityComparer.Default);
        foreach (var (decl, value) in registrations)
            if (DeclaredSym(decl) is TSymbol sym)
                dict[sym] = value;
        return dict;
    }

    /// <summary>Like <see cref="Materialize{TSymbol,TValue}"/>, but two-level: groups registrations by
    /// their template declaration's resolved symbol first, then by mangled suffix within that group. A
    /// registration whose template's <see cref="DeclaredSym"/> is null is silently skipped, same as
    /// <see cref="Materialize{TSymbol,TValue}"/>.</summary>
    private Dictionary<
        IMethodSymbol,
        IReadOnlyDictionary<string, CsMethod>
    > MaterializeGenericInstances()
    {
        var bySymbol = new Dictionary<IMethodSymbol, Dictionary<string, CsMethod>>(
            SymbolEqualityComparer.Default
        );
        foreach (var (templateDecl, suffix, value) in _genericInstanceRegs)
        {
            if (DeclaredSym(templateDecl) is not IMethodSymbol sym)
                continue;
            if (!bySymbol.TryGetValue(sym, out var bySuffix))
                bySymbol[sym] = bySuffix = new Dictionary<string, CsMethod>(StringComparer.Ordinal);
            bySuffix[suffix] = value;
        }
        var result = new Dictionary<IMethodSymbol, IReadOnlyDictionary<string, CsMethod>>(
            SymbolEqualityComparer.Default
        );
        foreach (var (sym, bySuffix) in bySymbol)
            result[sym] = bySuffix;
        return result;
    }

    /// <summary>Wire up the (shared, list-driven) materialization for every symbol-keyed index. Called
    /// from both constructors so <see cref="Disabled"/> and a real instance build the same way — for
    /// <see cref="Disabled"/> the registration lists simply stay empty forever (Register* is a no-op when
    /// <see cref="_mainTree"/> is null), so materializing them never touches the (also-null) model.</summary>
    private void InitIndexes()
    {
        _methodIndex = new Lazy<IReadOnlyDictionary<IMethodSymbol, CsMethod>>(() =>
            Materialize<IMethodSymbol, CsMethod>(_methodRegs)
        );
        _globalIndex = new Lazy<IReadOnlyDictionary<IFieldSymbol, (IrGlobal Global, CsType Type)>>(
            () =>
                Materialize<IFieldSymbol, (IrGlobal, CsType)>(_globalRegs)
        );
        _moduleConstIndex = new Lazy<IReadOnlyDictionary<IFieldSymbol, (CsType Type, long Value)>>(
            () =>
                Materialize<IFieldSymbol, (CsType, long)>(_constRegs)
        );
        _enumIndex = new Lazy<IReadOnlyDictionary<INamedTypeSymbol, CsEnum>>(() =>
            Materialize<INamedTypeSymbol, CsEnum>(_enumRegs)
        );
        _structIndex = new Lazy<IReadOnlyDictionary<INamedTypeSymbol, CsStruct>>(() =>
            Materialize<INamedTypeSymbol, CsStruct>(_structRegs)
        );
        _classIndex = new Lazy<IReadOnlyDictionary<INamedTypeSymbol, CsClass>>(() =>
            Materialize<INamedTypeSymbol, CsClass>(_classRegs)
        );
        _genericInstanceIndex = new Lazy<
            IReadOnlyDictionary<IMethodSymbol, IReadOnlyDictionary<string, CsMethod>>
        >(MaterializeGenericInstances);
    }

    private CSharpSemantics()
    {
        _mainTree = null;
        _instancesTree = null;
        _compilation = new Lazy<CSharpCompilation?>(() => null);
        _mainModel = new Lazy<SemanticModel?>(() => null);
        _instancesModel = new Lazy<SemanticModel?>(() => null);
        _hardwareType = new Lazy<INamedTypeSymbol?>(() => null);
        _gbType = new Lazy<INamedTypeSymbol?>(() => null);
        _memType = new Lazy<INamedTypeSymbol?>(() => null);
        _interruptAttributeType = new Lazy<INamedTypeSymbol?>(() => null);
        InitIndexes();
    }

    /// <param name="mainTree">The final wrapped tree (see <see cref="CSharpFrontend.BuildSemantics"/>).</param>
    /// <param name="instancesTree">The second, constructed tree housing monomorphized generic instances
    /// (see <see cref="CSharpFrontend.BuildInstancesTree"/>), or null for a program with no generic
    /// instances.</param>
    /// <param name="compilationFactory">Builds the compilation on first use; never invoked if nothing
    /// consults this instance.</param>
    public CSharpSemantics(
        SyntaxTree mainTree,
        SyntaxTree? instancesTree,
        Func<CSharpCompilation?> compilationFactory
    )
    {
        _mainTree = mainTree;
        _instancesTree = instancesTree;
        _compilation = new Lazy<CSharpCompilation?>(compilationFactory);
        _mainModel = new Lazy<SemanticModel?>(() => _compilation.Value?.GetSemanticModel(mainTree));
        _instancesModel = new Lazy<SemanticModel?>(() =>
            instancesTree is null ? null : _compilation.Value?.GetSemanticModel(instancesTree)
        );
        _hardwareType = new Lazy<INamedTypeSymbol?>(() =>
            _compilation.Value?.GetTypeByMetadataName("Hardware")
        );
        _gbType = new Lazy<INamedTypeSymbol?>(() =>
            _compilation.Value?.GetTypeByMetadataName("Gb")
        );
        _memType = new Lazy<INamedTypeSymbol?>(() =>
            _compilation.Value?.GetTypeByMetadataName("Mem")
        );
        _interruptAttributeType = new Lazy<INamedTypeSymbol?>(() =>
            _compilation.Value?.GetTypeByMetadataName("InterruptAttribute")
        );
        InitIndexes();
    }

    /// <summary>Whether <paramref name="node"/> belongs to a tree the semantic model was built from — the
    /// main tree, or the instances tree (see <see cref="CSharpFrontend.BuildInstancesTree"/>) once
    /// generic instances live there instead of as detached syntax. Every symbol lookup below checks this
    /// first, since <c>GetSymbolInfo</c>/<c>GetDeclaredSymbol</c> throw given a node from a foreign tree
    /// (e.g. one from an entirely separate parse, standing in for pre-migration detached syntax in
    /// tests).</summary>
    public bool InTree(SyntaxNode node) =>
        _mainTree is not null
        && (
            node.SyntaxTree == _mainTree
            || (_instancesTree is not null && node.SyntaxTree == _instancesTree)
        );

    /// <summary>The lazy <see cref="SemanticModel"/> for whichever of the two trees <paramref name="node"/>
    /// belongs to, or null if it belongs to neither.</summary>
    private SemanticModel? ModelFor(SyntaxNode node) =>
        node.SyntaxTree == _mainTree ? _mainModel.Value
        : node.SyntaxTree == _instancesTree ? _instancesModel.Value
        : null;

    /// <summary>The symbol a syntax node (identifier, member access, invocation, ...) refers to, or null
    /// if the node is detached, no compilation could be built, resolution failed, or these are
    /// <see cref="Disabled"/>. Never throws — a caller keeps its string-based fallback regardless of the
    /// result.</summary>
    public ISymbol? Sym(SyntaxNode node)
    {
        if (!InTree(node) || ModelFor(node) is not { } model)
            return null;
        try
        {
            return model.GetSymbolInfo(node).Symbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Like <see cref="Sym"/>, but when Roslyn's own binder rejects the single best answer
    /// (<c>SymbolInfo.Symbol</c> is null) this inspects <c>CandidateSymbols</c> and accepts one anyway,
    /// for the two reasons Koh-legal-but-C#-illegal code routinely triggers (Stage-2 design decision 5 —
    /// see <c>whats-the-state-of-sparkling-gem.md</c>):
    /// <list type="bullet">
    /// <item><see cref="CandidateReason.OverloadResolutionFailure"/> — Koh's own usual-arithmetic
    /// conversions accept mixed-width/signedness operands freely (<c>Helper(a + b)</c> with <c>byte a,
    /// b</c> types the sum as C#'s <c>int</c>; there is no user-visible <c>int</c>-to-<c>byte</c>
    /// implicit conversion, so Roslyn's own overload resolution rejects the only real candidate even
    /// though Koh's own arg-by-arg lowering accepts it happily);</item>
    /// <item><see cref="CandidateReason.Inaccessible"/> — Koh ignores C# accessibility modifiers
    /// entirely (a <c>private</c> field/method is reachable from any class), so a cross-class reference
    /// to a private member resolves with exactly this reason.</item>
    /// </list>
    /// Every other <see cref="CandidateReason"/> is left alone (this returns null, same as if there were
    /// no candidates at all) rather than guessed at:
    /// <see cref="CandidateReason.Ambiguous"/> means more than one member is genuinely equally
    /// applicable even by Koh's own looser rules — accepting either would be silently picking a winner
    /// Roslyn (correctly) refused to; <see cref="CandidateReason.MemberGroup"/> covers a method group
    /// used somewhere that needs a single value without ever being invoked (e.g. a delegate conversion),
    /// which cannot arise at any call site here since every consumer of this method resolves either an
    /// actual invocation or a field/member access, never a bare method-group expression; the remaining
    /// reasons (<c>WrongArity</c>, <c>StaticInstanceMismatch</c>, <c>NotAValue</c>, ...) all indicate a
    /// shape Koh's own lowering doesn't accept either, so nothing is lost by declining them.
    ///
    /// When exactly one candidate carries an accepted reason, it is accepted outright. More than one can
    /// still occur under an accepted reason (e.g. two overloads both rejected by the same C#-only rule);
    /// for a method lookup this narrows to the single candidate that is already a known Koh declaration
    /// (its <c>OriginalDefinition</c> is a key in <see cref="Methods"/>) — if more than one candidate is
    /// itself registered there, the choice is genuinely ambiguous and this returns null rather than guess
    /// (the caller's string fallback, or eventually an honest diagnostic, decides instead). Field lookups
    /// never produce more than one candidate in practice (C# has no field overloading), so there is no
    /// analogous narrowing for them — multiple candidates for a non-method symbol always return null.
    ///
    /// Never throws; identical foreign-tree/no-compilation behavior to <see cref="Sym"/>. <see cref="Sym"/>
    /// itself is unchanged and still candidate-blind — declaration passes and non-callee uses want the
    /// single unambiguous answer or nothing, never a guess.</summary>
    public ISymbol? SymOrCandidate(SyntaxNode node)
    {
        if (!InTree(node) || ModelFor(node) is not { } model)
            return null;
        SymbolInfo info;
        try
        {
            info = model.GetSymbolInfo(node);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (info.Symbol is { } resolved)
            return resolved;
        if (
            info.CandidateReason
            is not (CandidateReason.OverloadResolutionFailure or CandidateReason.Inaccessible)
        )
            return null;
        if (info.CandidateSymbols.Length == 1)
            return info.CandidateSymbols[0];
        IMethodSymbol? uniqueRegistered = null;
        foreach (var candidate in info.CandidateSymbols)
            if (candidate is IMethodSymbol method && Methods.ContainsKey(method.OriginalDefinition))
            {
                if (uniqueRegistered is not null)
                    return null; // more than one plausible candidate is itself a known declaration
                uniqueRegistered = method;
            }
        return uniqueRegistered;
    }

    /// <summary>The symbol a declaration node (a method/field/enum/... declaration) introduces, or null
    /// under the same conditions as <see cref="Sym"/>.</summary>
    public ISymbol? DeclaredSym(SyntaxNode node)
    {
        if (!InTree(node) || ModelFor(node) is not { } model)
            return null;
        try
        {
            return model.GetDeclaredSymbol(node);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Roslyn diagnostics overlapping <paramref name="node"/>'s own span — used only on
    /// <see cref="MethodLowerer"/>'s error path, to reword one of Koh's own
    /// generic "unresolved name" messages with Roslyn's clearer text when a whitelisted diagnostic covers
    /// the same span (see the design's Roslyn diagnostics policy: Roslyn's own diagnostics never gate
    /// compilation — Koh-legal code is routinely C#-illegal, e.g. CS0266 on <c>byte c = a + b;</c> — a
    /// whitelisted one only improves a message Koh's own lowering already decided to report). Scoped to
    /// the node's span rather than <c>Compilation.GetDiagnostics()</c> (which binds every method in the
    /// program), so a successful compile — which never calls this — pays nothing, and even the error path
    /// stays cheap. Empty for a node from neither tracked tree, or when no compilation could be
    /// built.</summary>
    public ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> DiagnosticsAt(SyntaxNode node)
    {
        if (!InTree(node) || ModelFor(node) is not { } model)
            return ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>.Empty;
        try
        {
            return model.GetDiagnostics(node.Span);
        }
        catch (ArgumentException)
        {
            return ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>.Empty;
        }
    }
}
