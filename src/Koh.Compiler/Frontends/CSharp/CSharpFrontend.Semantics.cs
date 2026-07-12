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
/// <see cref="ModuleConsts"/>, <see cref="Enums"/>, <see cref="Structs"/>, <see cref="Classes"/>) that
/// <see cref="MethodLowerer"/> consults symbol-first for name/member/call resolution, falling back to its
/// own string-keyed tables only when no symbol resolves (no compilation built, resolution failure, or —
/// for now — routing a generic call to its monomorphized instance; a generic instance's own body is no
/// longer detached syntax, see <see cref="CSharpFrontend.BuildInstancesTree"/>).
///
/// Everything that requires real Roslyn binding (the underlying <see cref="CSharpCompilation"/>, the
/// <see cref="SemanticModel"/>, and the stub type symbols) is behind its own <see cref="Lazy{T}"/>, so
/// constructing an instance is cheap — the compiler doesn't do any of that work unless a caller actually
/// reads one of these members. A <c>Lower()</c> whose program needs no resolution at all (rare) still
/// pays nothing beyond constructing the <see cref="Lazy{T}"/> wrappers.
/// </summary>
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

    /// <summary>Record a method declaration (top-level function or instance method) under its
    /// declaration node; materialized into <see cref="Methods"/> keyed by <see cref="IMethodSymbol"/> on
    /// first read. A no-op on <see cref="Disabled"/> (there is no tree to resolve against, so keeping the
    /// entry would only leak memory in that shared singleton).</summary>
    public void RegisterMethod(SyntaxNode decl, CsMethod method)
    {
        if (_mainTree is not null)
            _methodRegs.Add((decl, method));
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
    /// <em>template</em> itself is never registered (it is never lowered), so a constructed generic call's
    /// symbol (whose <c>OriginalDefinition</c> is the template, not any instance) still falls through to
    /// the syntax-based mangled-name lookup, even though the instance it should route to is itself now
    /// indexed here under its own (non-generic) symbol.</summary>
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
