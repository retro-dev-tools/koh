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
public sealed class CSharpFrontend : IFrontend
{
    public string Name => "csharp";

    public IReadOnlyList<string> Extensions => [".cs"];

    private const string WrapperPrefix = "static class __KohProgram {\n";

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
        var methods = new Dictionary<string, CsMethod>(StringComparer.Ordinal);
        var bodies = new List<(CsMethod Method, BlockSyntax? Body, ArrowExpressionClauseSyntax? Arrow)>();

        // Pass 0: enums (named constants), so their types and members resolve everywhere.
        var enums = CollectEnums(root);

        // Pass 0.4: struct layouts (value types with scalar fields).
        var structs = CollectStructs(root, enums);

        // Pass 0.5: static fields -> globals (WRAM) / ROM data / folded consts.
        var (globals, moduleConsts, staticInits) = CollectStatics(root, enums, module);

        var hardware = new HardwareRegisters(module);

        // Pass 1: signatures, so calls resolve regardless of source order. Accept both class methods
        // and top-level `static T F(...) {...}` functions (which Roslyn parses as local functions).
        foreach (var decl in CollectMethods(root))
        {
            var (name, returnSyntax, parameterList, body, arrow) = Describe(decl);
            var returnType = ResolveReturnType(returnSyntax, enums);
            var paramTypes = new List<CsType>();
            var refFlags = new List<bool>();
            var parameters = new List<IrParameter>();
            foreach (var p in parameterList.Parameters)
            {
                var t = ResolveType(p.Type!, enums);
                bool isRef = p.Modifiers.Any(m => m.ValueText is "ref" or "out" or "in");
                paramTypes.Add(t);
                refFlags.Add(isRef);
                parameters.Add(new IrParameter(p.Identifier.Text, isRef ? IrType.Pointer(t.Ir) : t.Ir));
            }

            var fn = new IrFunction(name, returnType?.Ir ?? IrType.Void, parameters)
            {
                InterruptVector = InterruptVectorOf(decl),
            };
            module.Functions.Add(fn);
            var method = new CsMethod(fn, returnType, paramTypes, refFlags);
            methods[name] = method;
            bodies.Add((method, body, arrow));
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
                    hardware, module.Name, inits).Lower();
            }
            catch (CSharpNotSupportedException ex)
            {
                Report(diagnostics, ex, fallback: FindDeclaration(root, method.Fn.Name)?.GetLocation());
            }
        }
    }

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

    private static int? InterruptVectorOf(SyntaxNode decl)
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
                return HardwareRegisters.InterruptVector(kind);
            }
        return null;
    }

    private static IEnumerable<SyntaxNode> CollectMethods(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is MethodDeclarationSyntax)
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

    private static Dictionary<string, CsEnum> CollectEnums(CompilationUnitSyntax root)
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
            enums[decl.Identifier.Text] = new CsEnum(underlying, members);
        }
        return enums;
    }

    private static (Dictionary<string, (IrGlobal Global, CsType Type)> Globals,
                    Dictionary<string, (CsType Type, long Value)> Consts,
                    List<(IrGlobal Global, long Value, CsType Type)> Inits)
        CollectStatics(CompilationUnitSyntax root, IReadOnlyDictionary<string, CsEnum> enums, IrModule module)
    {
        var globals = new Dictionary<string, (IrGlobal, CsType)>(StringComparer.Ordinal);
        var consts = new Dictionary<string, (CsType, long)>(StringComparer.Ordinal);
        var inits = new List<(IrGlobal, long, CsType)>();

        long? ConstLookup(string n) => consts.TryGetValue(n, out var c) ? c.Item2 : null;

        // Only class-level fields are statics; fields inside a struct are its members.
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                     .Where(f => f.Parent is ClassDeclarationSyntax))
        {
            bool isConst = field.Modifiers.Any(m => m.ValueText == "const");
            bool isReadonly = field.Modifiers.Any(m => m.ValueText == "readonly");
            var type = ResolveType(field.Declaration.Type, enums);
            int size = (type.Ir.Bits + 7) / 8;

            foreach (var v in field.Declaration.Variables)
            {
                var name = v.Identifier.Text;
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
        return (globals, consts, inits);
    }

    private static Dictionary<string, CsStruct> CollectStructs(
        CompilationUnitSyntax root, IReadOnlyDictionary<string, CsEnum> enums)
    {
        var structs = new Dictionary<string, CsStruct>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<StructDeclarationSyntax>())
        {
            var fields = new List<CsField>();
            int offset = 0, align = 1;
            foreach (var member in decl.Members.OfType<FieldDeclarationSyntax>())
            {
                var type = ResolveType(member.Declaration.Type, enums);
                int size = (type.Ir.Bits + 7) / 8;
                foreach (var v in member.Declaration.Variables)
                {
                    offset = RoundUp(offset, size); // align each field to its own size
                    fields.Add(new CsField(v.Identifier.Text, type, offset));
                    offset += size;
                    align = Math.Max(align, size);
                }
            }
            structs[decl.Identifier.Text] = new CsStruct(fields, RoundUp(offset, align));
        }
        return structs;
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

/// <summary>An enum: its underlying Koh C# type and member values.</summary>
internal sealed record CsEnum(CsType Underlying, IReadOnlyDictionary<string, long> Members);

/// <summary>A value-type struct: scalar fields with byte offsets, and its total size.</summary>
internal sealed record CsStruct(IReadOnlyList<CsField> Fields, int Size);

/// <summary>One struct field: name, type, and byte offset within the struct.</summary>
internal sealed record CsField(string Name, CsType Type, int Offset);

/// <summary>A resolved method: its IR function plus Koh C# signature types (for signedness/coercion).</summary>
internal sealed record CsMethod(IrFunction Fn, CsType? Return, IReadOnlyList<CsType> Params, IReadOnlyList<bool> RefParams);
