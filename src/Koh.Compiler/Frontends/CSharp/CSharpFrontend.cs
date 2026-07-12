using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// Thrown internally when a C# construct falls outside the supported "Koh C#" subset. The
/// frontend catches these at the module/method boundary and reports them into the diagnostic
/// bag; it does not escape <see cref="CSharpFrontend.Lower"/>.
/// </summary>
public sealed class CSharpNotSupportedException : Exception
{
    /// <summary>The offending syntax location, if known (in the wrapped source).</summary>
    public Location? Location { get; }

    public CSharpNotSupportedException(string message, Location? location = null)
        : base(message) => Location = location;
}

/// <summary>
/// The C# frontend. Roslyn parses the source; this walks the syntax tree, rejecting constructs
/// outside the supported systems subset ("Koh C#") and lowering the rest to Koh IR. It does not
/// use the semantic model: types are tracked from declarations with C-like rules (see
/// <see cref="CsType"/>). Static methods become IR functions; locals and parameters become
/// <c>alloca</c>s (so control flow needs no phi construction here — mutable state lives in memory,
/// and the backend statically allocates it).
/// </summary>
public sealed partial class CSharpFrontend : IFrontend
{
    public string Name => "csharp";

    public IReadOnlyList<string> Extensions => [".cs"];

    /// <summary>The synthesized class that wraps top-level source; its direct members are the program's
    /// top-level functions and static fields (methods inside a user class are instance methods).</summary>
    internal const string WrapperClassName = "__KohProgram";
    private const string WrapperPrefix = "static class " + WrapperClassName + " {\n";

    /// <summary>Whether a node is a program-level declaration: a direct member of the synthesized
    /// wrapper (the legacy bare-top-level-function style), or a static member of a user <c>static
    /// class</c> written at the top level (the modern style). The latter's members are qualified by the
    /// class name (<see cref="ProgramMemberName"/>), so <c>Board.Slide</c> and <c>Lcd.Off</c> are
    /// distinct even when short method names repeat.</summary>
    private static bool IsWrapperMember(SyntaxNode node) =>
        node.Parent is ClassDeclarationSyntax { Identifier.Text: WrapperClassName }
        || (node.Parent is ClassDeclarationSyntax p && IsProgramStaticClass(p));

    /// <summary>A user <c>static class</c> declared at the top level (nested directly in the wrapper):
    /// its static methods and fields are program-level declarations, namespaced by the class.</summary>
    private static bool IsProgramStaticClass(SyntaxNode node) =>
        node is ClassDeclarationSyntax { Identifier.Text: not WrapperClassName } cd
        && cd.Modifiers.Any(m => m.ValueText == "static")
        && cd.Parent is ClassDeclarationSyntax { Identifier.Text: WrapperClassName };

    /// <summary>Annotation kind carrying the declaring class of a monomorphized generic instance, which
    /// is a syntax node detached from the tree (so it has no <c>Parent</c> to read the class from).</summary>
    internal const string DeclaringClassAnnotation = "Koh.DeclaringClass";

    /// <summary>The enclosing top-level user <c>static class</c> of a program-scope declaration, or null
    /// for a direct wrapper member (a legacy top-level function/field).</summary>
    private static string? ProgramMemberClass(SyntaxNode decl) =>
        decl.GetAnnotations(DeclaringClassAnnotation).FirstOrDefault()?.Data
        ?? (
            decl.Parent is ClassDeclarationSyntax { Identifier.Text: var cn }
            && cn != WrapperClassName
                ? cn
                : null
        );

    /// <summary>The program-scope name of a top-level method/function/field: bare for the legacy
    /// wrapper, qualified <c>Class.Member</c> for a member of a user top-level <c>static class</c>.</summary>
    private static string ProgramMemberName(SyntaxNode decl, string simpleName) =>
        ProgramMemberClass(decl) is { } cls ? $"{cls}.{simpleName}" : simpleName;

    /// <summary>Blank out (replace with spaces, preserving line count) the syntax the frontend has no
    /// semantic model for: <c>using</c> directives, and the header of a file-scoped <c>namespace X;</c>.
    /// This lets the same source be organized like ordinary C# — usings, and framework code declared in
    /// a namespace — yet still wrap cleanly into the program class, with its members lifted to the top
    /// level. Namespaces are inert here (types resolve by simple name), so dropping them is safe.</summary>
    private static string BlankNamespacing(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        // DescendantNodes (not just the leading .Usings) so that when several source files are compiled
        // as one unit, their per-file usings/namespaces — which land after the first file's types — are
        // blanked too.
        var spans = new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        foreach (var node in root.DescendantNodes())
        {
            if (node is UsingDirectiveSyntax u)
                spans.Add(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(u.SpanStart, u.Span.End));
            else if (node is FileScopedNamespaceDeclarationSyntax fns)
                // Blank only the `namespace X;` header, not its members.
                spans.Add(
                    Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                        fns.NamespaceKeyword.SpanStart,
                        fns.SemicolonToken.Span.End
                    )
                );
            else if (node is NamespaceDeclarationSyntax bns)
            {
                // A block `namespace X { ... }`: blank the `namespace X {` header and its matching `}`,
                // lifting the members to the top level (they stay in place textually).
                spans.Add(
                    Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                        bns.NamespaceKeyword.SpanStart,
                        bns.OpenBraceToken.Span.End
                    )
                );
                spans.Add(bns.CloseBraceToken.Span);
            }
        }
        if (spans.Count == 0)
            return source;
        var chars = source.ToCharArray();
        foreach (var span in spans)
            for (int i = span.Start; i < span.End && i < chars.Length; i++)
                if (chars[i] != '\n' && chars[i] != '\r')
                    chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>Name of the synthesized heap-pointer global, and the top of the heap region it starts
    /// at. Allocation (<c>Mem.Alloc</c>, <c>new</c>) bumps the pointer downward from here; the region
    /// grows toward the function frames and recursion stack below it.</summary>
    internal const string HeapPointerName = "__heap";
    internal const int HeapTop = 0xDE00;

    /// <summary>Whether the program uses <c>float</c>/<c>double</c> (a type keyword or a float/double
    /// literal), so the softfloat runtime must be appended. Checked on the parsed tree so a mention in a
    /// comment or string can't false-trigger.</summary>
    private static bool UsesFloat(CompilationUnitSyntax root)
    {
        foreach (var tok in root.DescendantTokens())
        {
            if (tok.IsKind(SyntaxKind.FloatKeyword) || tok.IsKind(SyntaxKind.DoubleKeyword))
                return true;
            if (tok.IsKind(SyntaxKind.NumericLiteralToken) && tok.Value is float or double)
                return true;
        }
        return false;
    }

    /// <summary>Whether the program calls into the arena allocator (<c>Mem.Alloc</c> / <c>Mem.Reset</c>).</summary>
    private static bool UsesHeap(CompilationUnitSyntax root) =>
        root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(inv =>
                inv.Expression
                    is MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.Text: "Mem" }
                    }
            );

    public IrModule Lower(SourceText source, DiagnosticBag diagnostics)
    {
        var module = new IrModule(source.FilePath.Length > 0 ? source.FilePath : "csharp");
        try
        {
            LowerCore(source, module, diagnostics);
        }
        catch (CSharpNotSupportedException ex)
        {
            Report(diagnostics, ex);
        }
        return module;
    }

    /// <summary>Parse and wrap the source, then apply every whole-tree rewrite the rest of the pipeline
    /// depends on: synthesize the program-wrapper class, inject the softfloat runtime if the program
    /// uses float, and rewrite <c>yield return</c> iterators into state machines. Returns the final root
    /// — the tree <see cref="BuildSemantics"/> must build the compilation from, and the one every later
    /// pass lowers — or null if an error was already reported (a reserved <c>MathF</c> class name, or a
    /// parse error), in which case the caller stops.</summary>
    private static CompilationUnitSyntax? PrepareRoot(SourceText source, DiagnosticBag diagnostics)
    {
        // Wrap in an implicit static class so plain `static T F(...)` methods and enum/struct
        // declarations coexist without hitting C#'s "top-level statements first" rule. (Source
        // lines shift by one; accounted for when line maps are emitted.)
        // Wrap in the program class. A modern source's own `static class`es become nested static
        // classes (their members are program-level, qualified by class); a legacy source's bare `static
        // T F(...)` methods become direct members. Usings are blanked first so they don't land inside
        // the wrapper. Both keep their line count, so line maps stay aligned (one added prefix line).
        var wrapped = WrapperPrefix + BlankNamespacing(source.ToString()) + "\n}";
        var tree = CSharpSyntaxTree.ParseText(wrapped, path: source.FilePath);
        var root = tree.GetCompilationUnitRoot();

        // Float support: a program that uses `float`/`double` needs the softfloat runtime (its operators
        // lower to `__f32_add` etc.). Append that subset-C# source once — only when float is actually used
        // (so non-float ROMs carry none of it) and not already declared (a test may include it directly;
        // check for a real `__f32_add` method, not a text mention in a comment/string).
        bool runtimeAlreadyDeclared = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "__f32_add");
        if (UsesFloat(root) && !runtimeAlreadyDeclared)
        {
            // `MathF` is the appended library's class; a user class of that name would collide with it once
            // the runtime is injected. Reject it cleanly (like Mem/Hardware/Gb/BitConverter) instead of
            // emitting confusing duplicate-method diagnostics.
            var userMathF = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == "MathF");
            if (userMathF is not null)
            {
                Report(
                    diagnostics,
                    "'MathF' is reserved for the built-in Math library and cannot name a class.",
                    userMathF.Identifier.GetLocation()
                );
                return null;
            }
            wrapped =
                WrapperPrefix
                + BlankNamespacing(source.ToString() + "\n" + SoftFloatRuntime.Source)
                + "\n}";
            tree = CSharpSyntaxTree.ParseText(wrapped, path: source.FilePath);
            root = tree.GetCompilationUnitRoot();
        }

        bool hasParseError = false;
        foreach (var diag in tree.GetDiagnostics())
            if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            {
                Report(diagnostics, $"C# parse error: {diag.GetMessage()}", diag.Location);
                hasParseError = true;
            }
        if (hasParseError)
            return null;

        // Pass 0.0: rewrite `yield return` iterators into cooperative-coroutine state machines (a state
        // class with MoveNext/Current plus a factory), so the rest of the pipeline sees ordinary classes.
        return TransformIterators(root);
    }

    private static void LowerCore(SourceText source, IrModule module, DiagnosticBag diagnostics)
    {
        if (PrepareRoot(source, diagnostics) is not { } root)
            return;

        // A real CSharpCompilation over the final tree, as a resolution oracle for later phases of the
        // semantic-model migration (nothing consults it yet — see CSharpFrontend.Semantics.cs). Built
        // from root.SyntaxTree (post-rewrite), so node identity here matches everything lowered below.
        var semantics = BuildSemantics(root.SyntaxTree);

        var methods = new Dictionary<string, CsMethod>(StringComparer.Ordinal);
        var bodies =
            new List<(CsMethod Method, BlockSyntax? Body, ArrowExpressionClauseSyntax? Arrow)>();

        // Pass 0: enums (named constants), so their types and members resolve everywhere.
        var enums = CollectEnums(root, diagnostics);

        // Pass 0.4: struct layouts (value types with scalar fields).
        var structs = CollectStructs(root, enums, diagnostics);

        // Pass 0.45: class layouts (reference types, heap-allocated) and their instance methods.
        var classes = CollectClasses(root, enums, diagnostics);

        // Pass 0.5: static fields -> globals (WRAM) / ROM data / folded consts / data arrays.
        var (globals, moduleConsts, staticInits, moduleArrays) = CollectStatics(
            root,
            enums,
            module,
            diagnostics
        );

        // Pass 0.6: if the program allocates (Mem.Alloc/Reset or `new` of a class), give it a heap-
        // pointer global seeded to the top of the heap region; allocation bumps it down.
        if (UsesHeap(root) || root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any())
        {
            var heap = new IrGlobal(HeapPointerName, IrType.I16, AddressSpace.Wram);
            module.Globals.Add(heap);
            globals[HeapPointerName] = (heap, CsType.U16);
            staticInits.Add((heap, HeapTop, CsType.U16));
        }

        var hardware = new HardwareRegisters(module);

        // Pass 0.7: monomorphize generic methods — synthesize a specialized copy per concrete
        // instantiation. Generic templates themselves are not lowered; the specializations are.
        // Key templates by (name, type-parameter count): an invocation carries a name and a type-argument
        // count, and that pair selects the template — so `Wrap<T>` and `Wrap<T,U>` stay distinct. Two
        // templates sharing both (a value-arity overload like `Max<T>(T,T)` vs `Max<T>(T,T,T)`) would
        // mangle to the same specialized name, so that is reported rather than silently mis-specialized.
        var genericMethods =
            new Dictionary<(string? Class, string Name, int Arity), MethodDeclarationSyntax>();
        foreach (
            var m in root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.TypeParameterList is { Parameters.Count: > 0 } && IsWrapperMember(m))
        )
        {
            // Key by declaring class too, so `A.Id<T>` and `B.Id<T>` are distinct rather than colliding.
            int arity = m.TypeParameterList!.Parameters.Count;
            var key = (ProgramMemberClass(m), m.Identifier.Text, arity);
            if (!genericMethods.TryAdd(key, m))
                Report(
                    diagnostics,
                    $"generic method '{m.Identifier.Text}' with {arity} type parameter(s) is declared "
                        + "more than once; overloaded generic methods are not supported.",
                    m.Identifier.GetLocation()
                );

            // Monomorphization substitutes type-parameter names by identifier text alone, so a parameter
            // or local named like a type parameter would be rewritten to the concrete type (e.g. a local
            // `T` becomes `byte`). Reject that shadowing rather than mis-specialize.
            var typeParams = m
                .TypeParameterList.Parameters.Select(tp => tp.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);
            var shadow = m
                .ParameterList.Parameters.Select(p => p.Identifier.Text)
                .Concat(
                    m.DescendantNodes()
                        .OfType<VariableDeclaratorSyntax>()
                        .Select(v => v.Identifier.Text)
                )
                .FirstOrDefault(typeParams.Contains);
            if (shadow is not null)
                Report(
                    diagnostics,
                    $"in generic method '{m.Identifier.Text}', '{shadow}' shadows a type parameter of the "
                        + "same name; rename the value.",
                    m.Identifier.GetLocation()
                );
        }
        var genericInstances = SynthesizeGenericInstances(root, genericMethods);

        var classNames = (IReadOnlySet<string>)classes.Keys.ToHashSet(StringComparer.Ordinal);

        // Classify one parameter into its Koh C# type, ref flag, struct/class binding, and IrParameter.
        // A struct is passed by address (ref/in/out required); a class is a heap pointer passed by value;
        // a scalar is by value, or by address when ref/out/in.
        (CsType Type, bool Ref, CsStruct? Struct, CsClass? Class, IrParameter Param) BindParameter(
            ParameterSyntax p
        )
        {
            bool isRef = p.Modifiers.Any(m => m.ValueText is "ref" or "out" or "in");
            string pname = p.Identifier.Text;
            if (
                p.Type is IdentifierNameSyntax sn
                && structs.TryGetValue(sn.Identifier.Text, out var ps)
            )
            {
                if (!isRef)
                    throw new CSharpNotSupportedException(
                        $"struct parameter '{pname}' must be passed by ref/in/out.",
                        p.GetLocation()
                    );
                return (
                    CsType.U8,
                    true,
                    ps,
                    null,
                    new IrParameter(pname, IrType.Pointer(IrType.Array(IrType.I8, ps.Size)))
                );
            }
            if (
                p.Type is IdentifierNameSyntax cn
                && classes.TryGetValue(cn.Identifier.Text, out var pc)
            )
                return (
                    new CsType(IrType.Pointer(IrType.I8), false),
                    false,
                    null,
                    pc,
                    new IrParameter(pname, IrType.Pointer(IrType.I8))
                );
            var t = ResolveType(p.Type!, enums);
            return (
                t,
                isRef,
                null,
                null,
                new IrParameter(pname, isRef ? IrType.Pointer(t.Ir) : t.Ir)
            );
        }

        // Pass 1: signatures, so calls resolve regardless of source order. Accept both class methods
        // and top-level `static T F(...) {...}` functions (which Roslyn parses as local functions).
        var methodDecls = CollectMethods(root)
            .Where(d =>
                d is not MethodDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 }
            )
            .Concat(genericInstances);
        foreach (var decl in methodDecls)
        {
            var (simpleName, returnSyntax, parameterList, body, arrow) = Describe(decl);
            var name = ProgramMemberName(decl, simpleName);
            var returnType = ResolveReturnTypeAllowingClass(returnSyntax, enums, classNames);
            var paramTypes = new List<CsType>();
            var refFlags = new List<bool>();
            var paramStructs = new List<CsStruct?>();
            var paramClasses = new List<CsClass?>();
            var parameters = new List<IrParameter>();
            foreach (var p in parameterList.Parameters)
            {
                var (pt, pref, pstruct, pclass, par) = BindParameter(p);
                paramTypes.Add(pt);
                refFlags.Add(pref);
                paramStructs.Add(pstruct);
                paramClasses.Add(pclass);
                parameters.Add(par);
            }

            var fn = new IrFunction(name, returnType?.Ir ?? IrType.Void, parameters)
            {
                InterruptVector = InterruptVectorOf(decl, diagnostics),
            };
            var method = new CsMethod(
                fn,
                returnType,
                paramTypes,
                refFlags,
                paramStructs,
                ParamClasses: paramClasses,
                DeclaringClass: ProgramMemberClass(decl)
            );
            // Duplicate names would silently overwrite the earlier binding (and emit two IR functions
            // with the same name); keep the first definition and report the rest.
            if (!methods.TryAdd(name, method))
            {
                Report(
                    diagnostics,
                    $"duplicate function '{name}' (only the first definition is used).",
                    IdentifierLocation(decl)
                );
                continue;
            }
            module.Functions.Add(fn);
            bodies.Add((method, body, arrow));
        }

        // Pass 1.5: instance methods. Each becomes a function `Class.Method` with an implicit `this`
        // pointer prepended to its parameters.
        foreach (var cls in classes.Values)
        foreach (var (mname, mdecl) in cls.Methods)
        {
            var returnType = ResolveReturnTypeAllowingClass(mdecl.ReturnType, enums, classNames);
            var paramTypes = new List<CsType> { CsType.U16 };
            var refFlags = new List<bool> { false };
            var paramStructs = new List<CsStruct?> { null };
            var paramClasses = new List<CsClass?> { null };
            var parameters = new List<IrParameter> { new("this", IrType.Pointer(IrType.I8)) };
            foreach (var p in mdecl.ParameterList.Parameters)
            {
                // The instance-call path passes arguments by value, so a ref/out/in parameter on an
                // instance method would be a silent by-value miscompile — report it instead.
                if (p.Modifiers.Any(m => m.ValueText is "ref" or "out" or "in"))
                    Report(
                        diagnostics,
                        $"instance method '{cls.Name}.{mname}' parameter '{p.Identifier.Text}' "
                            + "cannot be ref/out/in (unsupported).",
                        p.GetLocation()
                    );
                var (pt, _, pstruct, pclass, par) = BindParameter(p);
                paramTypes.Add(pt);
                refFlags.Add(false);
                paramStructs.Add(pstruct);
                paramClasses.Add(pclass);
                parameters.Add(par);
            }
            var qualified = $"{cls.Name}.{mname}";
            var fn = new IrFunction(qualified, returnType?.Ir ?? IrType.Void, parameters);
            var method = new CsMethod(
                fn,
                returnType,
                paramTypes,
                refFlags,
                paramStructs,
                cls,
                paramClasses
            );
            if (!methods.TryAdd(qualified, method))
            {
                Report(
                    diagnostics,
                    $"duplicate method '{qualified}'.",
                    mdecl.Identifier.GetLocation()
                );
                continue;
            }
            module.Functions.Add(fn);
            bodies.Add((method, mdecl.Body, mdecl.ExpressionBody));
        }

        // The entry function (main, else the first non-handler) runs the static-field initializers in
        // its prologue. An interrupt handler must never be the entry: its body runs on every interrupt,
        // which would re-seed the heap pointer and re-run initializers, corrupting live state.
        var mains = bodies
            .Where(b =>
                // An instance method (a reference type's method, taking an implicit `this`) is never the
                // program entry even when named Main — the backend would boot into it with no receiver.
                b.Method.ThisClass
                    is null
                && string.Equals(
                    SimpleName(b.Method.Fn.Name),
                    "main",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();
        // More than one Main is ambiguous — the backend and this prologue would each pick one by order,
        // and which becomes the ROM entry would be silent. Report it rather than boot into a guess.
        if (mains.Count > 1)
            Report(
                diagnostics,
                "multiple 'Main' entry points ("
                    + string.Join(", ", mains.Select(b => b.Method.Fn.Name))
                    + "); a program must have exactly one.",
                FindDeclaration(root, mains[1].Method.Fn.Name)?.GetLocation()
            );
        var entry =
            mains.FirstOrDefault().Method
            ?? bodies.FirstOrDefault(b => b.Method.Fn.InterruptVector is null).Method;
        // Mark the entry authoritatively so the backend boots into it by flag, not by name-matching.
        if (entry is not null)
            entry.Fn.IsEntry = true;

        // Pass 2: bodies. Report per-method so one bad method doesn't sink the whole compile.
        foreach (var (method, body, arrow) in bodies)
        {
            var inits = ReferenceEquals(method, entry) ? staticInits : [];
            try
            {
                new MethodLowerer(
                    method,
                    body,
                    arrow,
                    methods,
                    enums,
                    structs,
                    globals,
                    moduleConsts,
                    hardware,
                    module.Name,
                    inits,
                    moduleArrays,
                    classes,
                    semantics
                ).Lower();
            }
            catch (CSharpNotSupportedException ex)
            {
                Report(
                    diagnostics,
                    ex,
                    fallback: FindDeclaration(root, method.Fn.Name)?.GetLocation()
                );
            }
        }

        // The softfloat runtime is appended whole, but a program uses only a few of its operations. Drop
        // the runtime functions the program can't reach, so a float ROM carries only the ops it uses (and
        // a handful of uncalled softfloat functions can't crowd the static frame allocation). Reuse the
        // optimizer's reachability walk, scoped to the `__`-prefixed runtime so user dead code is left to
        // later passes. Runs even unoptimized, so float works with or without the optimizer.
        Ir.Optimization.IrOptimizer.RemoveUnreachableFunctions(module, IsAppendedRuntimeFunction);
    }

    /// <summary>Whether a function belongs to the appended float runtime (the <c>__</c>-prefixed softfloat
    /// helpers and the <c>MathF</c> library), so an unused one is pruned rather than costing ROM. Only
    /// unreachable functions are dropped; <c>MathF</c> is a reserved class name, so a user function is never
    /// affected.</summary>
    private static bool IsAppendedRuntimeFunction(IrFunction f) =>
        f.Name.StartsWith("__", StringComparison.Ordinal)
        || f.Name.StartsWith("MathF.", StringComparison.Ordinal);

    private static Location IdentifierLocation(SyntaxNode decl) =>
        decl switch
        {
            MethodDeclarationSyntax m => m.Identifier.GetLocation(),
            LocalFunctionStatementSyntax f => f.Identifier.GetLocation(),
            _ => decl.GetLocation(),
        };

    /// <summary>Split a program-scope name into its owning class (null if unqualified) and its simple
    /// name — the inverse of the <c>Class.name</c> qualification applied at collection.</summary>
    internal static (string? Owner, string Simple) SplitQualified(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? (null, name) : (name[..dot], name[(dot + 1)..]);
    }

    /// <summary>The unqualified method name (the part after the last dot, if any).</summary>
    private static string SimpleName(string name) => SplitQualified(name).Simple;

    private static SyntaxNode? FindDeclaration(SyntaxNode root, string name) =>
        // Match the full program name (e.g. "Tilemap.Set"), so a diagnostic's fallback location doesn't
        // bind to a same-simple-named method in another static class (e.g. "Board.Set").
        CollectMethods(root)
            .FirstOrDefault(d =>
                ProgramMemberName(
                    d,
                    d is MethodDeclarationSyntax m
                        ? m.Identifier.Text
                        : ((LocalFunctionStatementSyntax)d).Identifier.Text
                ) == name
            );

    private static void Report(
        DiagnosticBag diagnostics,
        CSharpNotSupportedException ex,
        Location? fallback = null
    ) => Report(diagnostics, ex.Message, ex.Location ?? fallback);

    private static void Report(DiagnosticBag diagnostics, string message, Location? location)
    {
        if (location is { IsInSource: true } loc)
        {
            var span = loc.SourceSpan;
            int start = Math.Max(0, span.Start - WrapperPrefix.Length); // map past the class wrapper
            diagnostics.Report(new Koh.Core.Syntax.TextSpan(start, span.Length), message);
        }
        else
        {
            diagnostics.Report(new Koh.Core.Syntax.TextSpan(0, 0), message);
        }
    }

    private static int? InterruptVectorOf(SyntaxNode decl, DiagnosticBag diagnostics)
    {
        SyntaxList<AttributeListSyntax> lists = decl switch
        {
            MethodDeclarationSyntax m => m.AttributeLists,
            LocalFunctionStatementSyntax f => f.AttributeLists,
            _ => default,
        };

        foreach (var list in lists)
        foreach (var attr in list.Attributes)
        {
            var attrName = attr.Name.ToString();
            if (attrName is not ("Interrupt" or "InterruptAttribute"))
                continue;
            var arg = attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            var kind = arg switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                LiteralExpressionSyntax lit => lit.Token.ValueText,
                _ => null,
            };
            var vector = HardwareRegisters.InterruptVector(kind);
            // A present-but-unrecognized kind (typo, wrong enum) would otherwise map to null and be
            // silently treated as an ordinary function — the handler would never be wired to a vector.
            if (vector is null)
                Report(
                    diagnostics,
                    $"unknown interrupt kind '{kind ?? arg?.ToString() ?? "?"}' "
                        + "(expected VBlank, Stat/LcdStat/Lcd, Timer, Serial, or Joypad).",
                    attr.GetLocation()
                );
            return vector;
        }
        return null;
    }

    private static IEnumerable<SyntaxNode> CollectMethods(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            // Only methods directly in the program wrapper are top-level functions; methods inside a
            // user class are instance methods, collected separately with an implicit `this`.
            if (node is MethodDeclarationSyntax m && IsWrapperMember(m))
                yield return m;
            else if (node is LocalFunctionStatementSyntax fn && fn.Parent is GlobalStatementSyntax)
                yield return node; // top-level function
        }
    }

    private static (
        string Name,
        TypeSyntax Return,
        ParameterListSyntax Parameters,
        BlockSyntax? Body,
        ArrowExpressionClauseSyntax? Arrow
    ) Describe(SyntaxNode node) =>
        node switch
        {
            MethodDeclarationSyntax m => (
                m.Identifier.Text,
                m.ReturnType,
                m.ParameterList,
                m.Body,
                m.ExpressionBody
            ),
            LocalFunctionStatementSyntax f => (
                f.Identifier.Text,
                f.ReturnType,
                f.ParameterList,
                f.Body,
                f.ExpressionBody
            ),
            _ => throw new CSharpNotSupportedException($"unsupported declaration '{node.Kind()}'."),
        };
}
