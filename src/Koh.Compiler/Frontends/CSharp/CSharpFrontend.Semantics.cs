using System.Collections.Immutable;
using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Roslyn semantic-model plumbing (Phase 1 of the semantic-model migration): a real CSharpCompilation is
// available after every tree rewrite, as a resolution oracle. Nothing consults it yet — lowering
// decisions still come entirely from Koh's own C-like typing (CsType / MethodLowerer's string-keyed
// tables). Later phases read symbols out of CSharpSemantics; this phase only wires it up. Building the
// compilation is deferred until something actually consults CSharpSemantics (see CSharpSemantics below):
// per-Lower() cost for the ~500 tests that never touch it is then just constructing a handful of Lazy<T>
// wrappers, not real Roslyn binding work.
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

    /// <summary>Build the (deferred) semantic-model oracle for the final wrapped tree. <paramref
    /// name="mainTree"/> must be <c>root.SyntaxTree</c> taken AFTER every rewrite (the softfloat
    /// re-parse, then <see cref="TransformIterators"/>) — building the compilation from an earlier tree
    /// would make every node identity in the compilation stale. The actual <c>CSharpCompilation.Create</c>
    /// call (and TPA reference load, on first use process-wide) only happens if a caller consults the
    /// result — see <see cref="CSharpSemantics"/>.</summary>
    private static CSharpSemantics BuildSemantics(SyntaxTree mainTree) =>
        new(
            mainTree,
            () =>
            {
                var references = TrustedPlatformReferences.Value;
                return references.IsEmpty
                    ? null
                    : CSharpCompilation.Create(
                        "KohCSharpFrontend",
                        [mainTree, IntrinsicsStub.Tree],
                        references,
                        SemanticsCompilationOptions
                    );
            }
        );

    /// <summary>Test-only entry point: parses and wraps <paramref name="source"/> exactly as
    /// <see cref="LowerCore"/> does (through every tree rewrite), then builds semantics for the result,
    /// without lowering any method body. Lets tests assert against the real production tree/compilation
    /// instead of a re-implementation that could drift from it.</summary>
    internal static (CompilationUnitSyntax Root, CSharpSemantics Semantics) BuildSemanticsForTest(
        string source
    )
    {
        var root = PrepareRoot(SourceText.From(source, "test.cs"), new DiagnosticBag());
        return root is null
            ? (SyntaxFactory.CompilationUnit(), CSharpSemantics.Disabled)
            : (root, BuildSemantics(root.SyntaxTree));
    }
}

/// <summary>
/// Roslyn as a resolution oracle over the final wrapped tree: a lazy <see cref="SemanticModel"/>, an
/// <see cref="InTree"/> guard, pre-resolved stub symbols for the intrinsic surface
/// (<see cref="IntrinsicsStub"/>), and symbol-keyed indexes that later phases populate — empty here,
/// since Phase 1 is plumbing only and nothing consults them yet.
///
/// Everything that requires real Roslyn binding (the underlying <see cref="CSharpCompilation"/>, the
/// <see cref="SemanticModel"/>, and the stub type symbols) is behind its own <see cref="Lazy{T}"/>, so
/// constructing an instance is cheap — the compiler doesn't do any of that work unless a caller actually
/// reads one of these members (nothing does yet in Phase 1; <see cref="MethodLowerer"/> only stores the
/// instance it's given).
/// </summary>
internal sealed class CSharpSemantics
{
    /// <summary>Used when a <see cref="CSharpSemantics"/> is needed with no source tree at all (e.g. a
    /// <see cref="CSharpFrontend.PrepareRoot"/> failure before any tree exists). Every lookup safely
    /// returns null/false rather than throwing, so a consuming site's string-based fallback always still
    /// runs — semantics can only ever be a strict improvement, never a regression.</summary>
    public static readonly CSharpSemantics Disabled = new();

    private readonly SyntaxTree? _mainTree;
    private readonly Lazy<CSharpCompilation?> _compilation;
    private readonly Lazy<SemanticModel?> _model;
    private readonly Lazy<INamedTypeSymbol?> _hardwareType;
    private readonly Lazy<INamedTypeSymbol?> _gbType;
    private readonly Lazy<INamedTypeSymbol?> _memType;
    private readonly Lazy<INamedTypeSymbol?> _interruptAttributeType;

    /// <summary>The underlying compilation, or null if none could be built (the runtime exposes no TPA
    /// list, or this is <see cref="Disabled"/>). Exposed for diagnostics (Roslyn's own errors, e.g.
    /// CS0266/CS0214 on Koh-legal-but-C#-illegal code, are never a gate — see the design's diagnostics
    /// policy — but a later phase reads whitelisted messages from here, and tests use it to confirm
    /// symbols still resolve despite such errors being present). Building it is what triggers the actual
    /// Roslyn work — first access is the expensive one.</summary>
    public Compilation? Compilation => _compilation.Value;

    public INamedTypeSymbol? HardwareType => _hardwareType.Value;
    public INamedTypeSymbol? GbType => _gbType.Value;
    public INamedTypeSymbol? MemType => _memType.Value;
    public INamedTypeSymbol? InterruptAttributeType => _interruptAttributeType.Value;

    /// <summary>Method declarations keyed by their Roslyn symbol. Populated in a later phase's
    /// declaration passes; empty here.</summary>
    public Dictionary<IMethodSymbol, CsMethod> Methods { get; } =
        new(SymbolEqualityComparer.Default);

    /// <summary>Static fields (globals) keyed by their Roslyn symbol. Populated in a later phase; empty
    /// here.</summary>
    public Dictionary<IFieldSymbol, (IrGlobal Global, CsType Type)> Globals { get; } =
        new(SymbolEqualityComparer.Default);

    /// <summary>Enum types keyed by their Roslyn symbol. Populated in a later phase; empty here.</summary>
    public Dictionary<INamedTypeSymbol, CsEnum> Enums { get; } =
        new(SymbolEqualityComparer.Default);

    /// <summary>Struct types keyed by their Roslyn symbol. Populated in a later phase; empty here.</summary>
    public Dictionary<INamedTypeSymbol, CsStruct> Structs { get; } =
        new(SymbolEqualityComparer.Default);

    /// <summary>Class types keyed by their Roslyn symbol. Populated in a later phase; empty here.</summary>
    public Dictionary<INamedTypeSymbol, CsClass> Classes { get; } =
        new(SymbolEqualityComparer.Default);

    private CSharpSemantics()
    {
        _mainTree = null;
        _compilation = new Lazy<CSharpCompilation?>(() => null);
        _model = new Lazy<SemanticModel?>(() => null);
        _hardwareType = new Lazy<INamedTypeSymbol?>(() => null);
        _gbType = new Lazy<INamedTypeSymbol?>(() => null);
        _memType = new Lazy<INamedTypeSymbol?>(() => null);
        _interruptAttributeType = new Lazy<INamedTypeSymbol?>(() => null);
    }

    /// <param name="mainTree">The final wrapped tree (see <see cref="CSharpFrontend.BuildSemantics"/>).</param>
    /// <param name="compilationFactory">Builds the compilation on first use; never invoked if nothing
    /// consults this instance.</param>
    public CSharpSemantics(SyntaxTree mainTree, Func<CSharpCompilation?> compilationFactory)
    {
        _mainTree = mainTree;
        _compilation = new Lazy<CSharpCompilation?>(compilationFactory);
        _model = new Lazy<SemanticModel?>(() => _compilation.Value?.GetSemanticModel(mainTree));
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
    }

    /// <summary>Whether <paramref name="node"/> belongs to the tree the semantic model was built from.
    /// A monomorphized generic instance's body (<see cref="CSharpFrontend.TypeParamRewriter"/>) is a
    /// syntax node detached from that tree, so this is false for it — and every symbol lookup below
    /// checks this first, since <c>GetSymbolInfo</c>/<c>GetDeclaredSymbol</c> throw given a node from a
    /// foreign tree.</summary>
    public bool InTree(SyntaxNode node) => _mainTree is not null && node.SyntaxTree == _mainTree;

    /// <summary>The symbol a syntax node (identifier, member access, invocation, ...) refers to, or null
    /// if the node is detached, no compilation could be built, resolution failed, or these are
    /// <see cref="Disabled"/>. Never throws — a caller keeps its string-based fallback regardless of the
    /// result.</summary>
    public ISymbol? Sym(SyntaxNode node)
    {
        if (!InTree(node) || _model.Value is not { } model)
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
        if (!InTree(node) || _model.Value is not { } model)
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
}
