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

    public CSharpNotSupportedException(string message, Location? location = null) : base(message) =>
        Location = location;
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

    private const string WrapperPrefix = "static class __KohProgram {\n";

    /// <summary>Name of the synthesized heap-pointer global, and the top of the heap region it starts
    /// at. Allocation (<c>Mem.Alloc</c>, <c>new</c>) bumps the pointer downward from here; the region
    /// grows toward the function frames and recursion stack below it.</summary>
    internal const string HeapPointerName = "__heap";
    internal const int HeapTop = 0xDE00;

    /// <summary>Whether the program calls into the arena allocator (<c>Mem.Alloc</c> / <c>Mem.Reset</c>).</summary>
    private static bool UsesHeap(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(inv =>
            inv.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Mem" } });

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

    private static void LowerCore(SourceText source, IrModule module, DiagnosticBag diagnostics)
    {
        // Wrap in an implicit static class so plain `static T F(...)` methods and enum/struct
        // declarations coexist without hitting C#'s "top-level statements first" rule. (Source
        // lines shift by one; accounted for when line maps are emitted.)
        var wrapped = WrapperPrefix + source.ToString() + "\n}";
        var tree = CSharpSyntaxTree.ParseText(wrapped, path: source.FilePath);
        var root = tree.GetCompilationUnitRoot();

        bool hasParseError = false;
        foreach (var diag in tree.GetDiagnostics())
            if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            {
                Report(diagnostics, $"C# parse error: {diag.GetMessage()}", diag.Location);
                hasParseError = true;
            }
        if (hasParseError)
            return;

        // Pass 0.0: rewrite `yield return` iterators into cooperative-coroutine state machines (a state
        // class with MoveNext/Current plus a factory), so the rest of the pipeline sees ordinary classes.
        root = TransformIterators(root);

        var methods = new Dictionary<string, CsMethod>(StringComparer.Ordinal);
        var bodies = new List<(CsMethod Method, BlockSyntax? Body, ArrowExpressionClauseSyntax? Arrow)>();

        // Pass 0: enums (named constants), so their types and members resolve everywhere.
        var enums = CollectEnums(root, diagnostics);

        // Pass 0.4: struct layouts (value types with scalar fields).
        var structs = CollectStructs(root, enums, diagnostics);

        // Pass 0.45: class layouts (reference types, heap-allocated) and their instance methods.
        var classes = CollectClasses(root, enums, diagnostics);

        // Pass 0.5: static fields -> globals (WRAM) / ROM data / folded consts / data arrays.
        var (globals, moduleConsts, staticInits, moduleArrays) = CollectStatics(root, enums, module, diagnostics);

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
        var genericMethods = new Dictionary<(string Name, int Arity), MethodDeclarationSyntax>();
        foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.TypeParameterList is { Parameters.Count: > 0 }
                && m.Parent is ClassDeclarationSyntax { Identifier.Text: "__KohProgram" }))
        {
            var key = (m.Identifier.Text, m.TypeParameterList!.Parameters.Count);
            if (!genericMethods.TryAdd(key, m))
                Report(diagnostics,
                    $"generic method '{m.Identifier.Text}' with {key.Item2} type parameter(s) is declared "
                    + "more than once; overloaded generic methods are not supported.", m.Identifier.GetLocation());
        }
        var genericInstances = SynthesizeGenericInstances(root, genericMethods);

        // Pass 1: signatures, so calls resolve regardless of source order. Accept both class methods
        // and top-level `static T F(...) {...}` functions (which Roslyn parses as local functions).
        var methodDecls = CollectMethods(root)
            .Where(d => d is not MethodDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 })
            .Concat(genericInstances);
        foreach (var decl in methodDecls)
        {
            var (name, returnSyntax, parameterList, body, arrow) = Describe(decl);
            var returnType = ResolveReturnType(returnSyntax, enums);
            var paramTypes = new List<CsType>();
            var refFlags = new List<bool>();
            var paramStructs = new List<CsStruct?>();
            var parameters = new List<IrParameter>();
            foreach (var p in parameterList.Parameters)
            {
                bool isRef = p.Modifiers.Any(m => m.ValueText is "ref" or "out" or "in");
                // A struct parameter is passed by address (ref/in/out), so the callee shares the
                // caller's storage — value semantics would need a copy the backend can't make yet.
                if (p.Type is IdentifierNameSyntax structName && structs.TryGetValue(structName.Identifier.Text, out var ps))
                {
                    if (!isRef)
                        throw new CSharpNotSupportedException(
                            $"struct parameter '{p.Identifier.Text}' must be passed by ref/in/out.", p.GetLocation());
                    paramTypes.Add(CsType.U8);
                    paramStructs.Add(ps);
                    refFlags.Add(true);
                    parameters.Add(new IrParameter(p.Identifier.Text, IrType.Pointer(IrType.Array(IrType.I8, ps.Size))));
                    continue;
                }

                var t = ResolveType(p.Type!, enums);
                paramTypes.Add(t);
                paramStructs.Add(null);
                refFlags.Add(isRef);
                parameters.Add(new IrParameter(p.Identifier.Text, isRef ? IrType.Pointer(t.Ir) : t.Ir));
            }

            var fn = new IrFunction(name, returnType?.Ir ?? IrType.Void, parameters)
            {
                InterruptVector = InterruptVectorOf(decl, diagnostics),
            };
            var method = new CsMethod(fn, returnType, paramTypes, refFlags, paramStructs);
            // Duplicate names would silently overwrite the earlier binding (and emit two IR functions
            // with the same name); keep the first definition and report the rest.
            if (!methods.TryAdd(name, method))
            {
                Report(diagnostics, $"duplicate function '{name}' (only the first definition is used).",
                    IdentifierLocation(decl));
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
                var returnType = ResolveReturnType(mdecl.ReturnType, enums);
                var paramTypes = new List<CsType> { CsType.U16 };
                var refFlags = new List<bool> { false };
                var paramStructs = new List<CsStruct?> { null };
                var parameters = new List<IrParameter> { new("this", IrType.Pointer(IrType.I8)) };
                foreach (var p in mdecl.ParameterList.Parameters)
                {
                    var t = ResolveType(p.Type!, enums);
                    paramTypes.Add(t);
                    refFlags.Add(false);
                    paramStructs.Add(null);
                    parameters.Add(new IrParameter(p.Identifier.Text, t.Ir));
                }
                var qualified = $"{cls.Name}.{mname}";
                var fn = new IrFunction(qualified, returnType?.Ir ?? IrType.Void, parameters);
                var method = new CsMethod(fn, returnType, paramTypes, refFlags, paramStructs, cls);
                if (!methods.TryAdd(qualified, method))
                {
                    Report(diagnostics, $"duplicate method '{qualified}'.", mdecl.Identifier.GetLocation());
                    continue;
                }
                module.Functions.Add(fn);
                bodies.Add((method, mdecl.Body, mdecl.ExpressionBody));
            }

        // The entry function (main, else first) runs the static-field initializers in its prologue.
        var entry = bodies.FirstOrDefault(b =>
                string.Equals(b.Method.Fn.Name, "main", StringComparison.OrdinalIgnoreCase)).Method
            ?? (bodies.Count > 0 ? bodies[0].Method : null);

        // Pass 2: bodies. Report per-method so one bad method doesn't sink the whole compile.
        foreach (var (method, body, arrow) in bodies)
        {
            var inits = ReferenceEquals(method, entry) ? staticInits : [];
            try
            {
                new MethodLowerer(method, body, arrow, methods, enums, structs, globals, moduleConsts,
                    hardware, module.Name, inits, moduleArrays, classes).Lower();
            }
            catch (CSharpNotSupportedException ex)
            {
                Report(diagnostics, ex, fallback: FindDeclaration(root, method.Fn.Name)?.GetLocation());
            }
        }
    }

    private static Location IdentifierLocation(SyntaxNode decl) => decl switch
    {
        MethodDeclarationSyntax m => m.Identifier.GetLocation(),
        LocalFunctionStatementSyntax f => f.Identifier.GetLocation(),
        _ => decl.GetLocation(),
    };

    private static SyntaxNode? FindDeclaration(SyntaxNode root, string name) =>
        CollectMethods(root).FirstOrDefault(d => d is MethodDeclarationSyntax m
            ? m.Identifier.Text == name
            : ((LocalFunctionStatementSyntax)d).Identifier.Text == name);

    private static void Report(DiagnosticBag diagnostics, CSharpNotSupportedException ex, Location? fallback = null) =>
        Report(diagnostics, ex.Message, ex.Location ?? fallback);

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
                    Report(diagnostics,
                        $"unknown interrupt kind '{kind ?? arg?.ToString() ?? "?"}' "
                        + "(expected VBlank, Stat/LcdStat/Lcd, Timer, Serial, or Joypad).",
                        attr.GetLocation());
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
            if (node is MethodDeclarationSyntax { Parent: ClassDeclarationSyntax { Identifier.Text: "__KohProgram" } })
                yield return node;
            else if (node is LocalFunctionStatementSyntax fn && fn.Parent is GlobalStatementSyntax)
                yield return node; // top-level function
        }
    }

    private static (string Name, TypeSyntax Return, ParameterListSyntax Parameters, BlockSyntax? Body, ArrowExpressionClauseSyntax? Arrow)
        Describe(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => (m.Identifier.Text, m.ReturnType, m.ParameterList, m.Body, m.ExpressionBody),
        LocalFunctionStatementSyntax f => (f.Identifier.Text, f.ReturnType, f.ParameterList, f.Body, f.ExpressionBody),
        _ => throw new CSharpNotSupportedException($"unsupported declaration '{node.Kind()}'."),
    };

    internal static CsType ResolveType(TypeSyntax type, IReadOnlyDictionary<string, CsEnum> enums)
    {
        if (type is PredefinedTypeSyntax predefined && CsType.FromKeyword(predefined.Keyword.Kind()) is { } t)
            return t;
        // Int128/UInt128 have no keyword; they arrive as type names.
        if (type is IdentifierNameSyntax { Identifier.Text: "Int128" })
            return CsType.I128;
        if (type is IdentifierNameSyntax { Identifier.Text: "UInt128" })
            return CsType.U128;
        if (type is IdentifierNameSyntax id && enums.TryGetValue(id.Identifier.Text, out var e))
            return e.Underlying;
        if (type is PointerTypeSyntax pointer)
            return new CsType(IrType.Pointer(ResolveType(pointer.ElementType, enums).Ir), Signed: false);
        throw new CSharpNotSupportedException(
            $"unsupported type '{type}' (Koh C# supports byte/sbyte/ushort/short/bool, enums, and pointers).",
            type.GetLocation());
    }

    private static CsType? ResolveReturnType(TypeSyntax type, IReadOnlyDictionary<string, CsEnum> enums)
    {
        if (type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
            return null;
        return ResolveType(type, enums);
    }

    private static Dictionary<string, CsEnum> CollectEnums(CompilationUnitSyntax root, DiagnosticBag diagnostics)
    {
        var enums = new Dictionary<string, CsEnum>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            var underlying = decl.BaseList is { Types.Count: > 0 } bases
                ? ResolveType(bases.Types[0].Type, enums)
                : CsType.U8; // Koh C# defaults enums to byte (int has no place on an 8-bit CPU)

            var members = new Dictionary<string, long>(StringComparer.Ordinal);
            long next = 0;
            foreach (var member in decl.Members)
            {
                long value = member.EqualsValue is { } eq
                    ? ConstEval(eq.Value, name => members.TryGetValue(name, out var v) ? v : null)
                    : next;
                members[member.Identifier.Text] = value;
                next = value + 1;
            }
            var name = decl.Identifier.Text;
            if (!enums.TryAdd(name, new CsEnum(underlying, members)))
                Report(diagnostics, $"duplicate enum '{name}' (only the first definition is used).",
                    decl.Identifier.GetLocation());
        }
        return enums;
    }

    private static (Dictionary<string, (IrGlobal Global, CsType Type)> Globals,
                    Dictionary<string, (CsType Type, long Value)> Consts,
                    List<(IrGlobal Global, long Value, CsType Type)> Inits,
                    Dictionary<string, (IrGlobal Global, CsType Element, int Length)> Arrays)
        CollectStatics(CompilationUnitSyntax root, IReadOnlyDictionary<string, CsEnum> enums, IrModule module,
                       DiagnosticBag diagnostics)
    {
        var globals = new Dictionary<string, (IrGlobal, CsType)>(StringComparer.Ordinal);
        var consts = new Dictionary<string, (CsType, long)>(StringComparer.Ordinal);
        var inits = new List<(IrGlobal, long, CsType)>();
        var arrays = new Dictionary<string, (IrGlobal, CsType, int)>(StringComparer.Ordinal);

        long? ConstLookup(string n) => consts.TryGetValue(n, out var c) ? c.Item2 : null;

        // Statics (consts, scalar globals, data arrays) share one field namespace; a duplicate name
        // would collide across these dictionaries and emit two globals with the same name. Keep the
        // first definition and report the rest.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        bool Redeclared(string n, Location loc)
        {
            if (declared.Add(n))
                return false;
            Report(diagnostics, $"duplicate static field '{n}' (only the first definition is used).", loc);
            return true;
        }

        // Only class-level fields are statics; fields inside a struct are its members.
        // Only fields at the program (wrapper) level are statics; a user class's fields are its
        // per-instance members, laid out per instance rather than as global storage.
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                     .Where(f => f.Parent is ClassDeclarationSyntax { Identifier.Text: "__KohProgram" }))
        {
            bool isConst = field.Modifiers.Any(m => m.ValueText == "const");
            bool isReadonly = field.Modifiers.Any(m => m.ValueText == "readonly");

            // A static array field is a data table: `static readonly T[] x = { ... }` lives in ROM;
            // `static T[] x = new T[n]` is a zero-initialized WRAM buffer.
            if (field.Declaration.Type is ArrayTypeSyntax arrayType)
            {
                var element = ResolveType(arrayType.ElementType, enums);
                foreach (var v in field.Declaration.Variables)
                {
                    if (Redeclared(v.Identifier.Text, v.Identifier.GetLocation()))
                        continue;
                    CollectStaticArray(v, element, isReadonly, ConstLookup, module, arrays);
                }
                continue;
            }

            var type = ResolveType(field.Declaration.Type, enums);
            int size = type.Ir.SizeInBytes;

            foreach (var v in field.Declaration.Variables)
            {
                var name = v.Identifier.Text;
                if (Redeclared(name, v.Identifier.GetLocation()))
                    continue;
                if (isConst)
                {
                    if (v.Initializer is null)
                        throw new CSharpNotSupportedException($"const '{name}' needs an initializer.");
                    consts[name] = (type, ConstEval(v.Initializer.Value, ConstLookup));
                }
                else if (isReadonly && v.Initializer is { } roInit)
                {
                    var g = new IrGlobal(name, type.Ir, AddressSpace.Rom,
                        initializer: ToLittleEndian(ConstEval(roInit.Value, ConstLookup), size));
                    module.Globals.Add(g);
                    globals[name] = (g, type);
                }
                else
                {
                    var g = new IrGlobal(name, type.Ir, AddressSpace.Wram);
                    module.Globals.Add(g);
                    globals[name] = (g, type);
                    if (v.Initializer is { } init)
                        inits.Add((g, ConstEval(init.Value, ConstLookup), type));
                }
            }
        }
        return (globals, consts, inits, arrays);
    }

    /// <summary>Lower one static array field into a ROM (readonly, initialized) or WRAM (mutable,
    /// <c>new T[n]</c>) global.</summary>
    private static void CollectStaticArray(
        VariableDeclaratorSyntax v, CsType element, bool isReadonly, Func<string, long?> constLookup,
        IrModule module, Dictionary<string, (IrGlobal, CsType, int)> arrays)
    {
        var name = v.Identifier.Text;
        int elemSize = element.Ir.SizeInBytes;

        // A string literal is ROM character data: `static readonly byte[] Msg = "SCORE";`.
        if (v.Initializer?.Value is LiteralExpressionSyntax { Token.Value: string text })
        {
            if (!isReadonly)
                throw new CSharpNotSupportedException(
                    $"string-initialized static array '{name}' must be 'static readonly' (it lives in ROM).");
            var strBytes = new List<byte>(text.Length * elemSize);
            foreach (var ch in text)
                strBytes.AddRange(ToLittleEndian(ch, elemSize));
            var strRom = new IrGlobal(name, IrType.Array(element.Ir, text.Length), AddressSpace.Rom, initializer: strBytes.ToArray());
            module.Globals.Add(strRom);
            arrays[name] = (strRom, element, text.Length);
            return;
        }

        List<ExpressionSyntax>? elements = v.Initializer?.Value switch
        {
            InitializerExpressionSyntax bare => bare.Expressions.ToList(),                 // = { ... }
            ArrayCreationExpressionSyntax { Initializer: { } init } => init.Expressions.ToList(), // = new T[] { ... }
            _ => null,
        };

        if (elements is not null)
        {
            if (!isReadonly)
                throw new CSharpNotSupportedException(
                    $"initialized static array '{name}' must be 'static readonly' (it lives in ROM); "
                    + "use 'static T[] x = new T[n]' for a mutable buffer.");
            var bytes = new List<byte>(elements.Count * elemSize);
            foreach (var e in elements)
                bytes.AddRange(ToLittleEndian(ConstEval(e, constLookup), elemSize));
            var rom = new IrGlobal(name, IrType.Array(element.Ir, elements.Count), AddressSpace.Rom, initializer: bytes.ToArray());
            module.Globals.Add(rom);
            arrays[name] = (rom, element, elements.Count);
            return;
        }

        // No element list: `new T[n]` (or a bare size) -> a zero buffer. Mutable in WRAM; a readonly
        // one is placed in ROM (constant zeros).
        int length = v.Initializer?.Value switch
        {
            ArrayCreationExpressionSyntax create when create.Type.RankSpecifiers[0].Sizes[0] is { } size
                => (int)ConstEval(size, constLookup),
            _ => throw new CSharpNotSupportedException($"static array '{name}' needs an initializer or a size."),
        };
        if (length < 0)
            throw new CSharpNotSupportedException($"static array '{name}' has a negative length ({length}).");
        var g = new IrGlobal(name, IrType.Array(element.Ir, length), isReadonly ? AddressSpace.Rom : AddressSpace.Wram);
        module.Globals.Add(g);
        arrays[name] = (g, element, length);
    }

    private static Dictionary<string, CsStruct> CollectStructs(
        CompilationUnitSyntax root, IReadOnlyDictionary<string, CsEnum> enums, DiagnosticBag diagnostics)
    {
        var decls = new Dictionary<string, StructDeclarationSyntax>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<StructDeclarationSyntax>())
            if (!decls.TryAdd(decl.Identifier.Text, decl))
                Report(diagnostics, $"duplicate struct '{decl.Identifier.Text}' (only the first definition is used).",
                    decl.Identifier.GetLocation());

        var structs = new Dictionary<string, CsStruct>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);

        // Lay a struct out, resolving struct-typed fields on demand (nested structs) with cycle
        // detection. Scalar fields align to their own size; a nested struct field is packed (SM83 is
        // byte-addressable, so misalignment costs nothing).
        CsStruct Layout(string name)
        {
            if (structs.TryGetValue(name, out var done))
                return done;
            if (!inProgress.Add(name))
                throw new CSharpNotSupportedException($"struct '{name}' contains itself.");

            var fields = new List<CsField>();
            int offset = 0, align = 1;
            foreach (var member in decls[name].Members.OfType<FieldDeclarationSyntax>())
            {
                var typeSyntax = member.Declaration.Type;
                bool isStruct = typeSyntax is IdentifierNameSyntax sn && decls.ContainsKey(sn.Identifier.Text);
                CsStruct? nested = isStruct ? Layout(((IdentifierNameSyntax)typeSyntax).Identifier.Text) : null;
                var type = isStruct ? CsType.U8 : ResolveType(typeSyntax, enums);
                int size = nested?.Size ?? type.Ir.SizeInBytes;
                int fieldAlign = nested is not null ? 1 : size;
                foreach (var v in member.Declaration.Variables)
                {
                    offset = RoundUp(offset, fieldAlign);
                    fields.Add(new CsField(v.Identifier.Text, type, offset, nested));
                    offset += size;
                    align = Math.Max(align, fieldAlign);
                }
            }
            var result = new CsStruct(fields, RoundUp(offset, align));
            structs[name] = result;
            inProgress.Remove(name);
            return result;
        }

        foreach (var name in decls.Keys)
            Layout(name);
        return structs;
    }

    /// <summary>Collect user classes (reference types) nested in the program wrapper: lay out their
    /// non-static scalar fields like a struct and record their instance methods.</summary>
    private static Dictionary<string, CsClass> CollectClasses(
        CompilationUnitSyntax root, IReadOnlyDictionary<string, CsEnum> enums, DiagnosticBag diagnostics)
    {
        var classes = new Dictionary<string, CsClass>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (decl.Identifier.Text == "__KohProgram")
                continue; // the synthesized program wrapper, not a user class

            var fields = new List<CsField>();
            int offset = 0, align = 1;
            foreach (var member in decl.Members.OfType<FieldDeclarationSyntax>())
            {
                if (member.Modifiers.Any(m => m.ValueText == "static"))
                    continue; // a static field is program-global, not per-instance
                var type = ResolveType(member.Declaration.Type, enums);
                int fsize = type.Ir.SizeInBytes;
                foreach (var v in member.Declaration.Variables)
                {
                    offset = RoundUp(offset, fsize);
                    fields.Add(new CsField(v.Identifier.Text, type, offset));
                    offset += fsize;
                    align = Math.Max(align, fsize);
                }
            }
            var methods = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
            foreach (var m in decl.Members.OfType<MethodDeclarationSyntax>())
                methods.TryAdd(m.Identifier.Text, m);

            var cls = new CsClass(decl.Identifier.Text, new CsStruct(fields, RoundUp(offset, align)), methods);
            if (!classes.TryAdd(decl.Identifier.Text, cls))
                Report(diagnostics, $"duplicate class '{decl.Identifier.Text}' (only the first definition is used).",
                    decl.Identifier.GetLocation());
        }
        return classes;
    }

    private static int RoundUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static byte[] ToLittleEndian(long value, int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++)
            bytes[i] = (byte)(value >> (8 * i));
        return bytes;
    }

    /// <summary>Fold a constant expression to a long, resolving bare names via <paramref name="lookup"/>.</summary>
    internal static long ConstEval(ExpressionSyntax expr, Func<string, long?> lookup) => expr switch
    {
        ParenthesizedExpressionSyntax p => ConstEval(p.Expression, lookup),
        LiteralExpressionSyntax lit => Convert.ToInt64(lit.Token.Value),
        IdentifierNameSyntax id => lookup(id.Identifier.Text)
            ?? throw new CSharpNotSupportedException($"'{id.Identifier.Text}' is not a constant."),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } u => -ConstEval(u.Operand, lookup),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryPlusExpression } u => ConstEval(u.Operand, lookup),
        BinaryExpressionSyntax bin => FoldBinary(bin, lookup),
        _ => throw new CSharpNotSupportedException($"'{expr}' is not a constant expression."),
    };

    private static long FoldBinary(BinaryExpressionSyntax bin, Func<string, long?> lookup)
    {
        long l = ConstEval(bin.Left, lookup), r = ConstEval(bin.Right, lookup);
        return bin.Kind() switch
        {
            SyntaxKind.AddExpression => l + r,
            SyntaxKind.SubtractExpression => l - r,
            SyntaxKind.MultiplyExpression => l * r,
            SyntaxKind.DivideExpression => l / r,
            SyntaxKind.ModuloExpression => l % r,
            SyntaxKind.BitwiseAndExpression => l & r,
            SyntaxKind.BitwiseOrExpression => l | r,
            SyntaxKind.ExclusiveOrExpression => l ^ r,
            SyntaxKind.LeftShiftExpression => l << (int)r,
            SyntaxKind.RightShiftExpression => l >> (int)r,
            _ => throw new CSharpNotSupportedException($"'{bin}' is not a constant expression."),
        };
    }
}
