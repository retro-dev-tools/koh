using Koh.Compiler.Ir;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// Lowers one method body to an IR function. Parameters and locals become <c>alloca</c>s read via
/// <c>load</c> and written via <c>store</c>, so control flow (if/while) only needs br/condbr — no
/// phi construction. Expression types are tracked with C-like rules (8-bit arithmetic stays 8-bit)
/// and drive signed vs. unsigned operation selection.
/// </summary>
internal sealed class MethodLowerer
{
    private readonly CsMethod _method;
    private readonly BlockSyntax? _body;
    private readonly ArrowExpressionClauseSyntax? _arrow;
    private readonly IReadOnlyDictionary<string, CsMethod> _methods;
    private readonly IReadOnlyDictionary<string, CsEnum> _enums;
    private readonly IReadOnlyDictionary<string, CsStruct> _structs;
    private readonly IReadOnlyDictionary<string, (IrGlobal Global, CsType Type)> _globals;
    private readonly IReadOnlyDictionary<string, (CsType Type, long Value)> _moduleConsts;
    private readonly HardwareRegisters _hardware;
    private readonly string _file;
    private readonly IReadOnlyList<(IrGlobal Global, long Value, CsType Type)> _staticInits;
    private readonly IReadOnlyDictionary<string, (IrGlobal Global, CsType Element, int Length)> _moduleArrays;
    private readonly IrBuilder _b = new();
    private readonly Dictionary<string, (IrValue Slot, CsType Type)> _locals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IrValue Address, CsType Element)> _refs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IrValue ArrayPtr, CsType Element, int Length)> _arrays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IrValue BasePtr, CsStruct Info)> _structLocals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IrValue ArrayPtr, CsStruct Info, int Length)> _structArrays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CsType Type, long Value)> _consts = new(StringComparer.Ordinal);
    private readonly Stack<(IrBasicBlock Break, IrBasicBlock Continue)> _loops = new();

    public MethodLowerer(
        CsMethod method,
        BlockSyntax? body,
        ArrowExpressionClauseSyntax? arrow,
        IReadOnlyDictionary<string, CsMethod> methods,
        IReadOnlyDictionary<string, CsEnum> enums,
        IReadOnlyDictionary<string, CsStruct> structs,
        IReadOnlyDictionary<string, (IrGlobal Global, CsType Type)> globals,
        IReadOnlyDictionary<string, (CsType Type, long Value)> moduleConsts,
        HardwareRegisters hardware,
        string file,
        IReadOnlyList<(IrGlobal Global, long Value, CsType Type)> staticInits,
        IReadOnlyDictionary<string, (IrGlobal Global, CsType Element, int Length)> moduleArrays)
    {
        _file = file;
        _method = method;
        _body = body;
        _arrow = arrow;
        _methods = methods;
        _enums = enums;
        _structs = structs;
        _globals = globals;
        _moduleConsts = moduleConsts;
        _hardware = hardware;
        _staticInits = staticInits;
        _moduleArrays = moduleArrays;
    }

    public void Lower()
    {
        var entry = _method.Fn.AppendBlock("entry");
        _b.PositionAtEnd(entry);

        // Static data arrays are visible in every method: index/Length treat them like local
        // arrays, but the base is the global's address (ROM tables or WRAM buffers) not an alloca.
        foreach (var (name, a) in _moduleArrays)
            _arrays[name] = (IrBuilder.GlobalRef(a.Global), a.Element, a.Length);

        // Parameters: a normal one gets a mutable slot seeded with its value; a ref/out parameter
        // arrives as an address, so its "place" is that address itself (reads/writes deref it).
        for (int i = 0; i < _method.Fn.Parameters.Count; i++)
        {
            var p = _method.Fn.Parameters[i];
            if (_method.ParamStructs[i] is { } structParam)
            {
                _structLocals[p.Name!] = (p, structParam); // the param value is the struct's address
            }
            else if (_method.RefParams[i])
            {
                _refs[p.Name!] = (p, _method.Params[i]);
            }
            else
            {
                var slot = _b.Alloca(p.Type);
                _b.Store(p, slot);
                _locals[p.Name!] = (slot, _method.Params[i]);
            }
        }

        // Entry function only: apply static-field initializers.
        foreach (var (global, value, type) in _staticInits)
            _b.Store(IrBuilder.ConstInt(type.Ir, value), IrBuilder.GlobalRef(global));

        if (_body is { } body)
            foreach (var stmt in body.Statements)
                LowerStatement(stmt);
        else if (_arrow is { } arrow)
            EmitReturn(arrow.Expression);

        // Ensure the final block is terminated (fell off the end).
        if (_b.CurrentBlock.Terminator is null)
        {
            if (_method.Return is { } rt)
                _b.Ret(IrBuilder.ConstInt(rt.Ir, 0));
            else
                _b.Ret();
        }
    }

    // ---- Statements --------------------------------------------------------

    private void LowerStatement(StatementSyntax stmt)
    {
        // Stamp instructions with this statement's source line. The source was wrapped in a
        // one-line `static class {` prefix, so the wrapped 0-based line equals the user's 1-based line.
        _b.CurrentSource = new IrSourceLocation(
            _file, (uint)stmt.GetLocation().GetLineSpan().StartLinePosition.Line);

        switch (stmt)
        {
            case BlockSyntax block:
                foreach (var s in block.Statements)
                    LowerStatement(s);
                break;

            case LocalDeclarationStatementSyntax local:
                LowerLocalDeclaration(local.Declaration, local.Modifiers.Any(m => m.ValueText == "const"));
                break;

            case ExpressionStatementSyntax expr:
                LowerExpression(expr.Expression, expected: null);
                break;

            case IfStatementSyntax ifStmt:
                LowerIf(ifStmt);
                break;

            case WhileStatementSyntax whileStmt:
                LowerWhile(whileStmt);
                break;

            case ForStatementSyntax forStmt:
                LowerFor(forStmt);
                break;

            case DoStatementSyntax doStmt:
                LowerDo(doStmt);
                break;

            case SwitchStatementSyntax switchStmt:
                LowerSwitch(switchStmt);
                break;

            case BreakStatementSyntax:
                if (_loops.Count == 0)
                    throw new CSharpNotSupportedException("'break' outside a loop.");
                _b.Br(_loops.Peek().Break);
                break;

            case ContinueStatementSyntax:
                if (_loops.Count == 0)
                    throw new CSharpNotSupportedException("'continue' outside a loop.");
                _b.Br(_loops.Peek().Continue);
                break;

            case ReturnStatementSyntax ret:
                if (ret.Expression is { } value)
                    EmitReturn(value);
                else
                    _b.Ret();
                break;

            default:
                throw new CSharpNotSupportedException($"unsupported statement '{stmt.Kind()}'.", stmt.GetLocation());
        }
    }

    private void LowerLocalDeclaration(VariableDeclarationSyntax decl, bool isConst)
    {
        if (decl.Type is ArrayTypeSyntax arrayType)
        {
            foreach (var v in decl.Variables)
                LowerArrayLocal(v.Identifier.Text, arrayType, v.Initializer);
            return;
        }

        // A struct-typed local: reserve its bytes; fields default to zero (WRAM/emulator-zeroed).
        if (decl.Type is IdentifierNameSyntax typeName && _structs.TryGetValue(typeName.Identifier.Text, out var structInfo))
        {
            foreach (var v in decl.Variables)
                _structLocals[v.Identifier.Text] = (_b.Alloca(IrType.Array(IrType.I8, structInfo.Size)), structInfo);
            return;
        }

        var type = CSharpFrontend.ResolveType(decl.Type, _enums);
        foreach (var v in decl.Variables)
        {
            if (isConst)
            {
                if (v.Initializer is null)
                    throw new CSharpNotSupportedException($"const '{v.Identifier.Text}' needs an initializer.");
                _consts[v.Identifier.Text] = (type, CSharpFrontend.ConstEval(v.Initializer.Value, ResolveConst));
                continue;
            }

            var slot = _b.Alloca(type.Ir);
            _locals[v.Identifier.Text] = (slot, type);
            if (v.Initializer is { } init)
                _b.Store(Coerce(LowerExpression(init.Value, type), type), slot);
        }
    }

    /// <summary>Resolve a bare name to a constant value (local const), for constant folding.</summary>
    private long? ResolveConst(string name) => _consts.TryGetValue(name, out var c) ? c.Value : null;

    private void LowerArrayLocal(string name, ArrayTypeSyntax arrayType, EqualsValueClauseSyntax? initializer)
    {
        // An array of structs (`Sprite[] s = new Sprite[n]`) reserves n * structSize bytes; elements
        // are accessed as `s[i].field`. It must be sized with `new T[n]` (there are no struct literals).
        if (arrayType.ElementType is IdentifierNameSyntax structName
            && _structs.TryGetValue(structName.Identifier.Text, out var structElem))
        {
            int count = initializer?.Value switch
            {
                ArrayCreationExpressionSyntax { Initializer: null } c
                    => (int)CSharpFrontend.ConstEval(c.Type.RankSpecifiers[0].Sizes[0], ResolveConst),
                _ => throw new CSharpNotSupportedException(
                    $"struct array '{name}' must be created with 'new {structName.Identifier.Text}[n]'."),
            };
            if (count < 0)
                throw new CSharpNotSupportedException($"array '{name}' has a negative length ({count}).");
            var basePtr = _b.Alloca(IrType.Array(IrType.I8, structElem.Size * count));
            _structArrays[name] = (basePtr, structElem, count);
            return;
        }

        var element = CSharpFrontend.ResolveType(arrayType.ElementType, _enums);

        // A string literal initializes a byte array with its characters' codes (`byte[] s = "HI"`).
        if (initializer?.Value is LiteralExpressionSyntax { Token.Value: string text })
        {
            var strPtr = _b.Alloca(IrType.Array(element.Ir, text.Length));
            _arrays[name] = (strPtr, element, text.Length);
            for (int i = 0; i < text.Length; i++)
                _b.Store(IrBuilder.ConstInt(element.Ir, (byte)text[i]),
                    _b.Gep(strPtr, IrBuilder.ConstInt(IrType.I16, i), element.Ir));
            return;
        }

        List<ExpressionSyntax>? elements = null;
        int length;

        switch (initializer?.Value)
        {
            case ArrayCreationExpressionSyntax create:
                if (create.Initializer is { } listInit)
                {
                    elements = listInit.Expressions.ToList();
                    length = elements.Count;
                }
                else
                {
                    var size = create.Type.RankSpecifiers[0].Sizes[0];
                    length = (int)CSharpFrontend.ConstEval(size, ResolveConst);
                }
                break;
            case InitializerExpressionSyntax bare: // byte[] a = { 1, 2, 3 };
                elements = bare.Expressions.ToList();
                length = elements.Count;
                break;
            default:
                throw new CSharpNotSupportedException($"array '{name}' needs a size or initializer.");
        }

        if (length < 0)
            throw new CSharpNotSupportedException($"array '{name}' has a negative length ({length}).");
        var arrayPtr = _b.Alloca(IrType.Array(element.Ir, length));
        _arrays[name] = (arrayPtr, element, length);

        if (elements is not null)
            for (int i = 0; i < elements.Count; i++)
            {
                var slot = _b.Gep(arrayPtr, IrBuilder.ConstInt(IrType.I16, i), element.Ir);
                _b.Store(Coerce(LowerExpression(elements[i], element), element), slot);
            }
    }

    /// <summary>Compute the pointer to <c>arr[index]</c>.</summary>
    private (IrValue Pointer, CsType Element) ArrayElementPointer(ElementAccessExpressionSyntax access)
    {
        if (access.Expression is not IdentifierNameSyntax id || !_arrays.TryGetValue(id.Identifier.Text, out var arr))
            throw new CSharpNotSupportedException("indexing requires an array variable.");
        var (index, _) = LowerExpression(access.ArgumentList.Arguments[0].Expression, CsType.U16);
        return (_b.Gep(arr.ArrayPtr, index, arr.Element.Ir), arr.Element);
    }

    /// <summary>An assignable member: a struct field or a hardware register.</summary>
    private (IrValue Pointer, CsType Type)? MemberPointer(MemberAccessExpressionSyntax member)
    {
        if (StructFieldPointer(member) is { } field)
            return field;
        if (member.Expression is IdentifierNameSyntax subject
            && subject.Identifier.Text == "Hardware"
            && _hardware.IsRegister(member.Name.Identifier.Text))
            return (IrBuilder.GlobalRef(_hardware.Register(member.Name.Identifier.Text)), CsType.U8);
        return null;
    }

    /// <summary>Compute the pointer to a struct field: <c>s.field</c> on a struct local, or
    /// <c>arr[i].field</c> on an element of a struct array.</summary>
    private (IrValue Pointer, CsType Type)? StructFieldPointer(MemberAccessExpressionSyntax member)
    {
        if (StructBaseOf(member.Expression) is not { } b)
            return null;

        var fieldName = member.Name.Identifier.Text;
        foreach (var field in b.Info.Fields)
            if (field.Name == fieldName)
            {
                if (field.Struct is not null)
                    return null; // a struct-typed field is an aggregate base, resolved by StructBaseOf
                // The offset is aligned to the field size, so index = offset / size is exact.
                int index = field.Offset / field.Type.Ir.SizeInBytes;
                return (_b.Gep(b.Base, IrBuilder.ConstInt(IrType.I16, index), field.Type.Ir), field.Type);
            }
        throw new CSharpNotSupportedException($"struct has no field '{fieldName}'.");
    }

    /// <summary>The (base pointer, struct layout) a member access reads a field of: a named struct
    /// local (<c>s.field</c>) or an element of a struct array (<c>arr[i].field</c>).</summary>
    private (IrValue Base, CsStruct Info)? StructBaseOf(ExpressionSyntax expr)
    {
        if (expr is IdentifierNameSyntax id && _structLocals.TryGetValue(id.Identifier.Text, out var s))
            return (s.BasePtr, s.Info);
        if (expr is ElementAccessExpressionSyntax access
            && access.Expression is IdentifierNameSyntax arrayId
            && _structArrays.TryGetValue(arrayId.Identifier.Text, out var arr))
        {
            var (index, _) = LowerExpression(access.ArgumentList.Arguments[0].Expression, CsType.U16);
            var elementPtr = _b.Gep(arr.ArrayPtr, index, IrType.Array(IrType.I8, arr.Info.Size));
            return (elementPtr, arr.Info);
        }
        // A struct-typed field of another struct, e.g. `e.pos` in `e.pos.x` — recurse into the parent
        // and step to the field's bytes.
        if (expr is MemberAccessExpressionSyntax nested && StructBaseOf(nested.Expression) is { } parent)
            foreach (var field in parent.Info.Fields)
                if (field.Name == nested.Name.Identifier.Text && field.Struct is { } sub)
                    return (_b.Gep(parent.Base, IrBuilder.ConstInt(IrType.I16, field.Offset), IrType.I8), sub);
        return null;
    }

    private void LowerIf(IfStatementSyntax ifStmt)
    {
        var cond = Coerce(LowerExpression(ifStmt.Condition, CsType.Bool), CsType.Bool);
        var thenBlock = _method.Fn.AppendBlock("if.then");
        var elseBlock = ifStmt.Else is not null ? _method.Fn.AppendBlock("if.else") : null;
        var endBlock = _method.Fn.AppendBlock("if.end");

        _b.CondBr(cond, thenBlock, elseBlock ?? endBlock);

        _b.PositionAtEnd(thenBlock);
        LowerStatement(ifStmt.Statement);
        if (_b.CurrentBlock.Terminator is null)
            _b.Br(endBlock);

        if (elseBlock is not null)
        {
            _b.PositionAtEnd(elseBlock);
            LowerStatement(ifStmt.Else!.Statement);
            if (_b.CurrentBlock.Terminator is null)
                _b.Br(endBlock);
        }

        _b.PositionAtEnd(endBlock);
    }

    private void LowerWhile(WhileStatementSyntax whileStmt)
    {
        var condBlock = _method.Fn.AppendBlock("while.cond");
        var bodyBlock = _method.Fn.AppendBlock("while.body");
        var endBlock = _method.Fn.AppendBlock("while.end");

        _b.Br(condBlock);
        _b.PositionAtEnd(condBlock);
        _b.CondBr(Coerce(LowerExpression(whileStmt.Condition, CsType.Bool), CsType.Bool), bodyBlock, endBlock);

        _b.PositionAtEnd(bodyBlock);
        _loops.Push((endBlock, condBlock));
        LowerStatement(whileStmt.Statement);
        _loops.Pop();
        if (_b.CurrentBlock.Terminator is null)
            _b.Br(condBlock);

        _b.PositionAtEnd(endBlock);
    }

    private void LowerDo(DoStatementSyntax doStmt)
    {
        var bodyBlock = _method.Fn.AppendBlock("do.body");
        var condBlock = _method.Fn.AppendBlock("do.cond");
        var endBlock = _method.Fn.AppendBlock("do.end");

        _b.Br(bodyBlock);
        _b.PositionAtEnd(bodyBlock);
        _loops.Push((endBlock, condBlock));
        LowerStatement(doStmt.Statement);
        _loops.Pop();
        if (_b.CurrentBlock.Terminator is null)
            _b.Br(condBlock);

        _b.PositionAtEnd(condBlock);
        _b.CondBr(Coerce(LowerExpression(doStmt.Condition, CsType.Bool), CsType.Bool), bodyBlock, endBlock);

        _b.PositionAtEnd(endBlock);
    }

    private void LowerSwitch(SwitchStatementSyntax sw)
    {
        var (value, valueType) = LowerExpression(sw.Expression, expected: null);
        var endBlock = _method.Fn.AppendBlock("switch.end");
        IrBasicBlock? defaultBlock = null;
        var cases = new List<(IrConstInt, IrBasicBlock)>();
        var sections = new List<(SwitchSectionSyntax Section, IrBasicBlock Block)>();

        foreach (var section in sw.Sections)
        {
            var block = _method.Fn.AppendBlock("switch.case");
            sections.Add((section, block));
            foreach (var label in section.Labels)
            {
                if (label is CaseSwitchLabelSyntax c)
                {
                    var (v, _) = LowerExpression(c.Value, valueType);
                    if (v is not IrConstInt constant)
                        throw new CSharpNotSupportedException("switch case label must be a constant.");
                    cases.Add((constant, block));
                }
                else if (label is DefaultSwitchLabelSyntax)
                {
                    defaultBlock = block;
                }
            }
        }

        _b.Switch(value, defaultBlock ?? endBlock, cases);

        var continueTarget = _loops.Count > 0 ? _loops.Peek().Continue : endBlock;
        foreach (var (section, block) in sections)
        {
            _b.PositionAtEnd(block);
            _loops.Push((endBlock, continueTarget)); // break -> switch end; continue -> enclosing loop
            foreach (var stmt in section.Statements)
                LowerStatement(stmt);
            _loops.Pop();
            if (_b.CurrentBlock.Terminator is null)
                _b.Br(endBlock);
        }

        _b.PositionAtEnd(endBlock);
    }

    private void LowerFor(ForStatementSyntax forStmt)
    {
        // Initializers.
        if (forStmt.Declaration is { } decl)
            LowerLocalDeclaration(decl, isConst: false);
        foreach (var init in forStmt.Initializers)
            LowerExpression(init, expected: null);

        var condBlock = _method.Fn.AppendBlock("for.cond");
        var bodyBlock = _method.Fn.AppendBlock("for.body");
        var incrBlock = _method.Fn.AppendBlock("for.incr");
        var endBlock = _method.Fn.AppendBlock("for.end");

        _b.Br(condBlock);
        _b.PositionAtEnd(condBlock);
        if (forStmt.Condition is { } cond)
            _b.CondBr(Coerce(LowerExpression(cond, CsType.Bool), CsType.Bool), bodyBlock, endBlock);
        else
            _b.Br(bodyBlock); // for(;;)

        _b.PositionAtEnd(bodyBlock);
        _loops.Push((endBlock, incrBlock)); // continue runs the incrementors
        LowerStatement(forStmt.Statement);
        _loops.Pop();
        if (_b.CurrentBlock.Terminator is null)
            _b.Br(incrBlock);

        _b.PositionAtEnd(incrBlock);
        foreach (var incr in forStmt.Incrementors)
            LowerExpression(incr, expected: null);
        _b.Br(condBlock);

        _b.PositionAtEnd(endBlock);
    }

    private void EmitReturn(ExpressionSyntax value)
    {
        if (_method.Return is not { } rt)
            throw new CSharpNotSupportedException("return with a value from a void method.");
        _b.Ret(Coerce(LowerExpression(value, rt), rt));
    }

    // ---- Expressions -------------------------------------------------------

    /// <summary>Lower an expression, returning its value and Koh C# type. <paramref name="expected"/>
    /// types otherwise-ambiguous literals.</summary>
    private (IrValue Value, CsType Type) LowerExpression(ExpressionSyntax expr, CsType? expected)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax paren:
                return LowerExpression(paren.Expression, expected);

            case LiteralExpressionSyntax lit:
                return LowerLiteral(lit, expected);

            case IdentifierNameSyntax id:
            {
                var name = id.Identifier.Text;
                if (_consts.TryGetValue(name, out var localConst))
                    return (IrBuilder.ConstInt(localConst.Type.Ir, localConst.Value), localConst.Type);
                if (_moduleConsts.TryGetValue(name, out var moduleConst))
                    return (IrBuilder.ConstInt(moduleConst.Type.Ir, moduleConst.Value), moduleConst.Type);
                if (WritePlace(name) is { } place)
                    return (_b.Load(place.Pointer), place.Type);
                throw new CSharpNotSupportedException($"unknown identifier '{name}'.");
            }

            case ElementAccessExpressionSyntax access:
            {
                var (pointer, element) = ArrayElementPointer(access);
                return (_b.Load(pointer), element);
            }

            case MemberAccessExpressionSyntax member:
                return LowerMemberAccess(member, expected);

            case CastExpressionSyntax cast:
            {
                var target = CSharpFrontend.ResolveType(cast.Type, _enums);
                return (Coerce(LowerExpression(cast.Expression, target), target), target);
            }

            case PrefixUnaryExpressionSyntax unary:
                return LowerUnary(unary, expected);

            case PostfixUnaryExpressionSyntax post:
                return LowerIncDec(post.Operand, post.Kind() == SyntaxKind.PostIncrementExpression, prefix: false);

            case ConditionalExpressionSyntax cond:
                return LowerConditional(cond, expected);

            case BinaryExpressionSyntax binary:
                return LowerBinary(binary, expected);

            case AssignmentExpressionSyntax assign:
                return LowerAssignment(assign);

            case InvocationExpressionSyntax call:
                return LowerCall(call);

            default:
                throw new CSharpNotSupportedException($"unsupported expression '{expr.Kind()}'.", expr.GetLocation());
        }
    }

    private (IrValue, CsType) LowerLiteral(LiteralExpressionSyntax lit, CsType? expected)
    {
        if (lit.Kind() == SyntaxKind.TrueLiteralExpression)
            return (IrBuilder.ConstInt(IrType.I8, 1), CsType.Bool);
        if (lit.Kind() == SyntaxKind.FalseLiteralExpression)
            return (IrBuilder.ConstInt(IrType.I8, 0), CsType.Bool);

        // A string literal is only valid as a byte-array initializer (handled in LowerArrayLocal);
        // elsewhere `Convert.ToInt64` would throw or silently parse it, so report it cleanly.
        if (lit.Token.Value is not (long or int or char or byte or short or ushort or uint or sbyte or ulong))
            throw new CSharpNotSupportedException(
                $"a {lit.Kind()} is not a value here (string literals are only allowed as byte[] initializers).",
                lit.GetLocation());

        long value = unchecked((long)Convert.ToUInt64(lit.Token.Value));
        // With no expected type, size the literal to its value so a wide constant isn't truncated
        // to a neighbouring narrow type (e.g. `1000 == x`), and a small one still stays 8-bit.
        var type = expected ?? value switch
        {
            >= 0 and <= 0xFF => CsType.U8,
            >= 0 and <= 0xFFFF => CsType.U16,
            >= 0 and <= 0xFFFFFFFF => CsType.U32,
            _ => CsType.U64,
        };
        return (IrBuilder.ConstInt(type.Ir, value), type);
    }

    private (IrValue, CsType) LowerUnary(PrefixUnaryExpressionSyntax unary, CsType? expected)
    {
        switch (unary.Kind())
        {
            case SyntaxKind.PreIncrementExpression:
                return LowerIncDec(unary.Operand, increment: true, prefix: true);
            case SyntaxKind.PreDecrementExpression:
                return LowerIncDec(unary.Operand, increment: false, prefix: true);
            case SyntaxKind.AddressOfExpression:
            {
                var address = LvalueAddress(unary.Operand);
                return (address, new CsType(address.Type, Signed: false));
            }
            case SyntaxKind.PointerIndirectionExpression: // *p
            {
                var place = DerefPlace(unary.Operand);
                return (_b.Load(place.Pointer), place.Type);
            }
        }

        var (value, type) = LowerExpression(unary.Operand, expected);
        return unary.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => LowerNegate(value, type, expected),
            SyntaxKind.BitwiseNotExpression => LowerComplement(value, type),
            SyntaxKind.LogicalNotExpression =>
                (_b.Binary(IrBinaryOp.Xor, value, IrBuilder.ConstInt(IrType.I8, 1)), CsType.Bool),
            SyntaxKind.UnaryPlusExpression => (value, type),
            _ => throw new CSharpNotSupportedException($"unsupported unary operator '{unary.OperatorToken.Text}'."),
        };
    }

    /// <summary>Lower bitwise complement <c>~x</c> as an xor with an all-ones mask of the operand's
    /// width, folding a constant operand. The result keeps the operand's type (width and signedness),
    /// matching how <see cref="InferType"/> sizes it; the backend's per-byte xor masks each byte, so
    /// a <c>-1</c> constant complements every width correctly.</summary>
    private (IrValue, CsType) LowerComplement(IrValue value, CsType type) =>
        value is IrConstInt k
            ? (IrBuilder.ConstInt(type.Ir, ~k.Value), type)
            : (_b.Binary(IrBinaryOp.Xor, value, IrBuilder.ConstInt(type.Ir, -1)), type);

    /// <summary>Lower unary minus. A negated value is signed — C# promotes <c>-x</c> to a signed type —
    /// and a negated literal folds to a signed constant so it sign-extends correctly and can adopt a
    /// wider operand's type in a mixed expression. Without this, <c>-5</c> (from the unsigned literal 5)
    /// would be an unsigned 251, so <c>x &lt; -5</c> would compare against 251 instead of -5.</summary>
    private (IrValue, CsType) LowerNegate(IrValue value, CsType type, CsType? expected)
    {
        if (value is IrConstInt k)
        {
            long neg = -k.Value;
            CsType t = expected is { Signed: true } e && Fits(neg, e) ? e
                : neg is >= -128 and <= 127 ? CsType.I8
                : neg is >= -32768 and <= 32767 ? CsType.I16
                : neg is >= int.MinValue and <= int.MaxValue ? CsType.I32
                : CsType.I64;
            return (IrBuilder.ConstInt(t.Ir, neg), t);
        }
        var signed = type.Signed ? type : new CsType(type.Ir, Signed: true);
        return (_b.Sub(IrBuilder.ConstInt(signed.Ir, 0), value), signed);
    }

    private (IrValue, CsType) LowerIncDec(ExpressionSyntax operand, bool increment, bool prefix)
    {
        if (operand is not IdentifierNameSyntax id || WritePlace(id.Identifier.Text) is not { } place)
            throw new CSharpNotSupportedException("++/-- requires a variable.");

        var old = _b.Load(place.Pointer);
        // A pointer steps by one element (scaled by the pointee size via gep); an integer by one.
        IrValue updated = place.Type.Ir.Kind == IrTypeKind.Pointer
            ? _b.Gep(old, IrBuilder.ConstInt(IrType.I16, increment ? 1 : -1), Pointee(place.Type))
            : _b.Binary(increment ? IrBinaryOp.Add : IrBinaryOp.Sub, old, IrBuilder.ConstInt(place.Type.Ir, 1));
        _b.Store(updated, place.Pointer);
        return (prefix ? updated : old, place.Type);
    }

    /// <summary>An assignable storage location — a local's alloca or a global's address — or null.</summary>
    private (IrValue Pointer, CsType Type)? WritePlace(string name)
    {
        if (_locals.TryGetValue(name, out var local))
            return (local.Slot, local.Type);
        if (_refs.TryGetValue(name, out var reference))
            return (reference.Address, reference.Element); // ref param: the address is the place
        if (_globals.TryGetValue(name, out var global))
            return (IrBuilder.GlobalRef(global.Global), global.Type);
        return null;
    }

    /// <summary>The place denoted by <c>*ptr</c>: the pointer's value is the address to load/store.</summary>
    private (IrValue Pointer, CsType Type) DerefPlace(ExpressionSyntax pointerExpr)
    {
        var (pointerValue, pointerType) = LowerExpression(pointerExpr, expected: null);
        if (pointerType.Ir.Element is not { } element)
            throw new CSharpNotSupportedException("'*' requires a pointer.");
        return (pointerValue, new CsType(element, Signed: false));
    }

    /// <summary>The address of an lvalue, for taking a reference (ref argument or &amp;).</summary>
    private IrValue LvalueAddress(ExpressionSyntax expr) => expr switch
    {
        // A struct value (local, array element, or nested field) is referenced by its base address.
        _ when StructBaseOf(expr) is { } s => s.Base,
        IdentifierNameSyntax id when WritePlace(id.Identifier.Text) is { } p => p.Pointer,
        ElementAccessExpressionSyntax ea => ArrayElementPointer(ea).Pointer,
        MemberAccessExpressionSyntax ma when MemberPointer(ma) is { } mp => mp.Pointer,
        _ => throw new CSharpNotSupportedException($"cannot take a reference to '{expr}'."),
    };

    /// <summary>Short-circuit <c>&amp;&amp;</c>/<c>||</c> via a result slot and a conditional branch.</summary>
    private (IrValue, CsType) LowerLogical(BinaryExpressionSyntax binary, bool isAnd)
    {
        var result = _b.Alloca(IrType.I8);
        var left = Coerce(LowerExpression(binary.Left, CsType.Bool), CsType.Bool);
        _b.Store(left, result);

        var evalRight = _method.Fn.AppendBlock("logic.rhs");
        var done = _method.Fn.AppendBlock("logic.end");
        if (isAnd)
            _b.CondBr(left, evalRight, done);   // && : only evaluate rhs when lhs is true
        else
            _b.CondBr(left, done, evalRight);   // || : only evaluate rhs when lhs is false

        _b.PositionAtEnd(evalRight);
        _b.Store(Coerce(LowerExpression(binary.Right, CsType.Bool), CsType.Bool), result);
        _b.Br(done);

        _b.PositionAtEnd(done);
        return (_b.Load(result), CsType.Bool);
    }

    /// <summary>Best-effort static type of an expression without emitting IR, for sizing a result
    /// slot (e.g. a ternary with no expected type). Returns null when the type isn't obvious, and the
    /// caller falls back to a default.</summary>
    private CsType? InferType(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax p:
                return InferType(p.Expression);
            case CastExpressionSyntax cast:
                try { return CSharpFrontend.ResolveType(cast.Type, _enums); }
                catch (CSharpNotSupportedException) { return null; }
            case LiteralExpressionSyntax lit when lit.Token.Value is long or int or char or byte or short or ushort or uint or sbyte:
                long v = Convert.ToInt64(lit.Token.Value);
                return v switch { >= 0 and <= 0xFF => CsType.U8, >= 0 and <= 0xFFFF => CsType.U16, _ => CsType.U32 };
            case IdentifierNameSyntax id:
                if (_locals.TryGetValue(id.Identifier.Text, out var local)) return local.Type;
                if (_refs.TryGetValue(id.Identifier.Text, out var reference)) return reference.Element;
                if (_consts.TryGetValue(id.Identifier.Text, out var c)) return c.Type;
                if (_moduleConsts.TryGetValue(id.Identifier.Text, out var mc)) return mc.Type;
                if (_globals.TryGetValue(id.Identifier.Text, out var g)) return g.Type;
                return null;
            case PrefixUnaryExpressionSyntax u:
                return u.Kind() switch
                {
                    SyntaxKind.LogicalNotExpression => CsType.Bool,
                    SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression
                        or SyntaxKind.BitwiseNotExpression => InferType(u.Operand),
                    _ => null,
                };
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax fn }
                when _methods.TryGetValue(fn.Identifier.Text, out var callee):
                return callee.Return;
            case ConditionalExpressionSyntax nested:
                return CommonInferred(nested.WhenTrue, nested.WhenFalse, nested);
            case BinaryExpressionSyntax bin when IsComparison(bin.Kind())
                    || bin.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression:
                return CsType.Bool;
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LeftShiftExpression
                    or (int)SyntaxKind.RightShiftExpression } sh:
                return InferType(sh.Left); // a shift result follows its left operand
            case BinaryExpressionSyntax bin:
                return (InferType(bin.Left), InferType(bin.Right)) is ({ } bl, { } br)
                    ? CommonType(bl, br, signMatters: false, bin)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>The inferred common type of two branch expressions, or the one that is inferable, or
    /// null when neither is. Used to size a ternary's result slot from its branches.</summary>
    private CsType? CommonInferred(ExpressionSyntax whenTrue, ExpressionSyntax whenFalse, ExpressionSyntax site) =>
        (InferType(whenTrue), InferType(whenFalse)) switch
        {
            ({ } t, { } f) => CommonType(t, f, signMatters: false, site),
            ({ } t, null) => t,
            (null, { } f) => f,
            _ => null,
        };

    private (IrValue, CsType) LowerConditional(ConditionalExpressionSyntax cond, CsType? expected)
    {
        // The result slot must be sized before the branches run, so when there is no expected type
        // infer one from the branches (else a wide branch, e.g. an int, would truncate to the default).
        var type = expected ?? CommonInferred(cond.WhenTrue, cond.WhenFalse, cond) ?? CsType.U16;
        var result = _b.Alloca(type.Ir);
        var c = Coerce(LowerExpression(cond.Condition, CsType.Bool), CsType.Bool);

        var thenBlock = _method.Fn.AppendBlock("cond.then");
        var elseBlock = _method.Fn.AppendBlock("cond.else");
        var done = _method.Fn.AppendBlock("cond.end");
        _b.CondBr(c, thenBlock, elseBlock);

        _b.PositionAtEnd(thenBlock);
        _b.Store(Coerce(LowerExpression(cond.WhenTrue, type), type), result);
        _b.Br(done);

        _b.PositionAtEnd(elseBlock);
        _b.Store(Coerce(LowerExpression(cond.WhenFalse, type), type), result);
        _b.Br(done);

        _b.PositionAtEnd(done);
        return (_b.Load(result), type);
    }

    private (IrValue, CsType) LowerBinary(BinaryExpressionSyntax binary, CsType? expected)
    {
        var kind = binary.Kind();

        if (kind == SyntaxKind.LogicalAndExpression)
            return LowerLogical(binary, isAnd: true);
        if (kind == SyntaxKind.LogicalOrExpression)
            return LowerLogical(binary, isAnd: false);

        if (IsComparison(kind))
        {
            // Lower each operand by its own type (not the outer `expected`, which is the Bool result
            // type and would truncate a literal operand); a common type reconciles the two.
            var leftOp = LowerExpression(binary.Left, expected: null);
            var rightOp = LowerExpression(binary.Right, expected: null);
            leftOp = AdoptConstant(leftOp, rightOp.Type);
            rightOp = AdoptConstant(rightOp, leftOp.Type);
            var (left, lt) = leftOp;
            var (right, rt) = rightOp;
            if (lt.Ir.Kind == IrTypeKind.Pointer || rt.Ir.Kind == IrTypeKind.Pointer)
            {
                // Compare two addresses as unsigned 16-bit integers (icmp stays integer-only).
                var li = Coerce((left, lt), CsType.U16);
                var ri = Coerce((right, rt), CsType.U16);
                return (_b.Compare(CompareOp(kind, signed: false), li, ri), CsType.Bool);
            }
            // Convert both to their common type, then the predicate's signedness follows it — so a
            // mixed comparison like `sbyte < byte` isn't silently governed by the left operand.
            // Ordering needs signedness; equality does not (it is a pure bit test).
            bool ordering = kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression);
            var cmpType = CommonType(lt, rt, signMatters: ordering, binary);
            return (_b.Compare(CompareOp(kind, cmpType.Signed),
                Coerce((left, lt), cmpType), Coerce((right, rt), cmpType)), CsType.Bool);
        }

        // The outer `expected` types int literals for width promotion, but a pointer expectation
        // would mistype an integer literal operand as a pointer — drop it in that case.
        var (l, ltype) = LowerExpression(binary.Left, expected?.Ir.Kind == IrTypeKind.Pointer ? null : expected);

        // Pointer arithmetic (p + i / p - i) lowers to a gep: the index is scaled by the pointee
        // size and widened to the 16-bit address. A plain add would instead try to coerce the index
        // *into* the pointer type, which is not a valid integer conversion and drops the high byte.
        if (ltype.Ir.Kind == IrTypeKind.Pointer
            && kind is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression)
            return (PointerOffset(l, ltype, binary.Right, subtract: kind == SyntaxKind.SubtractExpression), ltype);

        var (r, rtype) = LowerExpression(binary.Right, ltype);

        // Commuted form: i + p. The pointer is on the right; gep from it with the left as the index.
        if (kind == SyntaxKind.AddExpression && rtype.Ir.Kind == IrTypeKind.Pointer)
            return (_b.Gep(r, Coerce((l, ltype), CsType.U16), Pointee(rtype)), rtype);

        // A shift result follows its left operand; the count is an independent operand.
        if (kind is SyntaxKind.LeftShiftExpression or SyntaxKind.RightShiftExpression)
            return (_b.Binary(ArithOp(kind, ltype.Signed), l, Coerce((r, rtype), ltype)), ltype);

        // Otherwise both operands convert to their common type (C-like usual arithmetic
        // conversions), which also selects signed vs. unsigned div/rem and avoids narrowing the
        // wider operand to the left's width. Signedness only affects div/rem (and comparisons).
        (l, ltype) = AdoptConstant((l, ltype), rtype);
        (r, rtype) = AdoptConstant((r, rtype), ltype);
        bool signMatters = kind is SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression;
        var common = CommonType(ltype, rtype, signMatters, binary);
        return (_b.Binary(ArithOp(kind, common.Signed),
            Coerce((l, ltype), common), Coerce((r, rtype), common)), common);
    }

    /// <summary>If <paramref name="operand"/> is a constant whose value fits <paramref name="other"/>,
    /// retype it to that type. This mirrors C#'s constant conversions: a bare literal (which defaults
    /// to the smallest unsigned type holding it) adopts the signedness/width of the value it is used
    /// with, so `intVar &lt; 1000` stays a signed compare instead of becoming a mixed-sign one.</summary>
    private static (IrValue Value, CsType Type) AdoptConstant((IrValue Value, CsType Type) operand, CsType other)
    {
        if (operand.Value is IrConstInt c && other.Ir.Kind == IrTypeKind.Int && Fits(c.Value, other))
            return (IrBuilder.ConstInt(other.Ir, c.Value), other);
        return operand;
    }

    private static bool Fits(long value, CsType type)
    {
        int bits = type.Ir.SizeInBits;
        if (type.Signed)
            return value >= -(1L << (bits - 1)) && value <= (1L << (bits - 1)) - 1;
        return value >= 0 && (bits >= 64 || value <= (1L << bits) - 1);
    }

    /// <summary>
    /// The common type two operands convert to, following C-like usual arithmetic conversions over
    /// the Koh numeric types: the wider storage width wins. When the operands' signedness differs
    /// <em>and</em> it affects the result, the pair is promoted to a signed type wide enough to hold
    /// both ranges (so <c>sbyte</c> vs <c>byte</c> becomes a signed <c>short</c>, matching C#). When
    /// no wider signed type exists on the target (a 32-bit unsigned operand mixed with a signed one,
    /// which would need a 64-bit signed type): if the operator's signedness affects the result (divide,
    /// remainder, ordering) that is a diagnostic asking for an explicit cast; otherwise the common width
    /// is used unsigned, since the bits are identical.
    /// </summary>
    private static CsType CommonType(CsType a, CsType b, bool signMatters, ExpressionSyntax site)
    {
        int width = Math.Max(a.Ir.SizeInBits, b.Ir.SizeInBits);
        if (a.Signed == b.Signed)
            return new CsType(IrType.Int(width), a.Signed);

        // Mixed signedness: prefer a signed type wide enough to hold the unsigned operand's range,
        // because the result's signedness governs how a later widening sign- vs. zero-extends it —
        // so `(sbyte)a + (byte)b` must stay signed even though the add's bits don't depend on sign.
        var unsignedOp = a.Signed ? b : a;
        int need = Math.Max(width, unsignedOp.Ir.SizeInBits + 1);
        if (need <= 16)
            return new CsType(IrType.I16, Signed: true);
        if (need <= 32)
            return new CsType(IrType.I32, Signed: true); // e.g. ushort vs sbyte -> signed int
        if (need <= 64)
            return new CsType(IrType.I64, Signed: true); // e.g. uint vs int -> signed long

        // No usable wider signed type exists on the target (64-bit unsigned mixed with signed would
        // need a 128-bit signed type). If the operator's signedness affects the result it needs an
        // explicit cast; otherwise use the common width unsigned, since the bits are identical.
        if (signMatters)
            throw new CSharpNotSupportedException(
                $"mixed signed/unsigned operation on '{a.Ir}' and '{b.Ir}' needs a wider signed type "
                + "than this target provides; cast one operand explicitly.", site.GetLocation());
        return new CsType(IrType.Int(width), Signed: false);
    }

    /// <summary>Offset a pointer by an index expression via a gep (scaled by the pointee size).</summary>
    private IrValue PointerOffset(IrValue pointer, CsType pointerType, ExpressionSyntax indexExpr, bool subtract)
    {
        var index = Coerce(LowerExpression(indexExpr, CsType.U16), CsType.U16);
        if (subtract)
            index = _b.Binary(IrBinaryOp.Sub, IrBuilder.ConstInt(IrType.I16, 0), index);
        return _b.Gep(pointer, index, Pointee(pointerType));
    }

    private (IrValue, CsType) LowerAssignment(AssignmentExpressionSyntax assign)
    {
        // Whole-struct copy: `a = b` where a is a struct (local or array element) copies its bytes.
        if (assign.Kind() == SyntaxKind.SimpleAssignmentExpression
            && StructBaseOf(assign.Left) is { } dest)
        {
            if (StructBaseOf(assign.Right) is not { } src || !ReferenceEquals(src.Info, dest.Info))
                throw new CSharpNotSupportedException("a struct can only be assigned from another value of the same struct type.");
            for (int k = 0; k < dest.Info.Size; k++)
            {
                var from = _b.Gep(src.Base, IrBuilder.ConstInt(IrType.I16, k), IrType.I8);
                var to = _b.Gep(dest.Base, IrBuilder.ConstInt(IrType.I16, k), IrType.I8);
                _b.Store(_b.Load(from), to);
            }
            return (dest.Base, CsType.U8);
        }

        IrValue pointer;
        CsType type;
        if (assign.Left is IdentifierNameSyntax id && WritePlace(id.Identifier.Text) is { } place)
            (pointer, type) = place;
        else if (assign.Left is ElementAccessExpressionSyntax access)
            (pointer, type) = ArrayElementPointer(access);
        else if (assign.Left is MemberAccessExpressionSyntax fieldAccess && MemberPointer(fieldAccess) is { } field)
            (pointer, type) = field;
        else if (assign.Left is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PointerIndirectionExpression } deref)
            (pointer, type) = DerefPlace(deref.Operand);
        else
            throw new CSharpNotSupportedException("assignment target must be a variable, array element, struct field, or *pointer.");

        var kind = assign.Kind();
        IrValue result;
        if (kind == SyntaxKind.SimpleAssignmentExpression)
        {
            result = Coerce(LowerExpression(assign.Right, type), type);
        }
        else if (type.Ir.Kind == IrTypeKind.Pointer
                 && kind is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression)
        {
            // p += n / p -= n step by whole elements, so lower through a gep like `p + n`.
            var current = _b.Load(pointer);
            result = PointerOffset(current, type, assign.Right, subtract: kind == SyntaxKind.SubtractAssignmentExpression);
        }
        else if (kind is SyntaxKind.LeftShiftAssignmentExpression or SyntaxKind.RightShiftAssignmentExpression)
        {
            // Shift: the count is independent and the result keeps the target's width.
            var current = _b.Load(pointer);
            var amount = Coerce(LowerExpression(assign.Right, type), type);
            result = _b.Binary(CompoundOp(kind, type.Signed), current, amount);
        }
        else
        {
            // `x OP= y` computes `x OP y` in the operands' common type (usual arithmetic conversions),
            // then narrows back to x — so /= and %= match `x = x / y` rather than truncating y first.
            var current = _b.Load(pointer);
            var (rhsVal, rhsType) = LowerExpression(assign.Right, expected: null);
            bool signMatters = kind is SyntaxKind.DivideAssignmentExpression or SyntaxKind.ModuloAssignmentExpression;
            var common = CommonType(type, rhsType, signMatters, assign);
            var opResult = _b.Binary(CompoundOp(kind, common.Signed),
                Coerce((current, type), common), Coerce((rhsVal, rhsType), common));
            result = Coerce((opResult, common), type);
        }

        _b.Store(result, pointer);
        return (result, type);
    }

    private (IrValue, CsType) LowerMemberAccess(MemberAccessExpressionSyntax member, CsType? expected)
    {
        // Struct field read.
        if (StructFieldPointer(member) is { } field)
            return (_b.Load(field.Pointer), field.Type);

        if (member.Expression is IdentifierNameSyntax subject)
        {
            // Hardware register read, e.g. Hardware.LCDC.
            if (subject.Identifier.Text == "Hardware" && _hardware.IsRegister(member.Name.Identifier.Text))
                return (_b.Load(IrBuilder.GlobalRef(_hardware.Register(member.Name.Identifier.Text))), CsType.U8);

            // Enum member reference, e.g. Color.Red.
            if (_enums.TryGetValue(subject.Identifier.Text, out var e))
            {
                var name = member.Name.Identifier.Text;
                if (!e.Members.TryGetValue(name, out long value))
                    throw new CSharpNotSupportedException($"enum '{subject.Identifier.Text}' has no member '{name}'.");
                return (IrBuilder.ConstInt(e.Underlying.Ir, value), e.Underlying);
            }

            // Array length, e.g. arr.Length.
            if (member.Name.Identifier.Text == "Length")
            {
                int? length =
                    _arrays.TryGetValue(subject.Identifier.Text, out var arr) ? arr.Length :
                    _structArrays.TryGetValue(subject.Identifier.Text, out var sarr) ? sarr.Length : null;
                if (length is { } n)
                {
                    var type = expected ?? (n <= 0xFF ? CsType.U8 : CsType.U16);
                    return (IrBuilder.ConstInt(type.Ir, n), type);
                }
            }
        }
        throw new CSharpNotSupportedException($"unsupported member access '{member}'.");
    }

    private (IrValue, CsType) LowerCall(InvocationExpressionSyntax call)
    {
        // Hardware control intrinsics: Hardware.EnableInterrupts(), etc.
        if (call.Expression is MemberAccessExpressionSyntax hw
            && hw.Expression is IdentifierNameSyntax hwId && hwId.Identifier.Text == "Hardware")
        {
            var intrinsic = hw.Name.Identifier.Text switch
            {
                "EnableInterrupts" => "ei",
                "DisableInterrupts" => "di",
                "Halt" => "halt",
                "Nop" => "nop",
                _ => throw new CSharpNotSupportedException($"unknown Hardware method '{hw.Name.Identifier.Text}'."),
            };
            return (_b.Intrinsic(intrinsic), CsType.U8);
        }

        if (call.Expression is not IdentifierNameSyntax id || !_methods.TryGetValue(id.Identifier.Text, out var callee))
            throw new CSharpNotSupportedException($"unsupported call target '{call.Expression}'.");

        var args = new List<IrValue>();
        var argList = call.ArgumentList.Arguments;
        if (argList.Count != callee.Params.Count)
            throw new CSharpNotSupportedException(
                $"'{id.Identifier.Text}' takes {callee.Params.Count} argument(s), but {argList.Count} were given.",
                call.GetLocation());
        for (int i = 0; i < argList.Count; i++)
        {
            if (callee.ParamStructs[i] is not null)
            {
                // A struct is passed by its address; reinterpret to the parameter's exact pointer
                // type so the call is well-typed (a nested field's base is typed i8*, not [N x i8]*).
                var address = LvalueAddress(argList[i].Expression);
                args.Add(_b.Conv(IrConvOp.Bitcast, address, callee.Fn.Parameters[i].Type));
            }
            else if (callee.RefParams[i])
            {
                args.Add(LvalueAddress(argList[i].Expression)); // ref/out: pass the address
            }
            else
            {
                args.Add(Coerce(LowerExpression(argList[i].Expression, callee.Params[i]), callee.Params[i]));
            }
        }

        var result = _b.Call(callee.Fn, args);
        return (result, callee.Return ?? CsType.U8);
    }

    // ---- Types & operators -------------------------------------------------

    /// <summary>Widen/narrow a value to <paramref name="target"/> using its source signedness.
    /// Pointers and integers of the address width share storage, so casts between them (e.g.
    /// <c>(byte*)someUshort</c>, <c>(byte)ptr</c>) go through a <c>bitcast</c> reinterpret — a
    /// resize as an integer first when the widths differ — which keeps the IR well-typed rather
    /// than emitting a <c>zext</c>/<c>trunc</c> onto a pointer type.</summary>
    private IrValue Coerce((IrValue Value, CsType Type) source, CsType target)
    {
        var s = source.Type.Ir;
        var t = target.Ir;
        if (s.StructurallyEquals(t))
            return source.Value;

        if (s.Kind == IrTypeKind.Pointer || t.Kind == IrTypeKind.Pointer)
        {
            if (s.SizeInBytes == t.SizeInBytes)
                return _b.Conv(IrConvOp.Bitcast, source.Value, t); // pure reinterpret

            // Different widths: resize as an integer of the source's storage, then reinterpret.
            var value = source.Value;
            var asInt = s.Kind == IrTypeKind.Pointer ? IrType.Int(s.SizeInBits) : s;
            if (s.Kind == IrTypeKind.Pointer)
                value = _b.Conv(IrConvOp.Bitcast, value, asInt);
            int targetIntBits = t.SizeInBits;
            if (targetIntBits != asInt.Bits)
                value = _b.Conv(
                    targetIntBits < asInt.Bits ? IrConvOp.Trunc
                        : source.Type.Signed ? IrConvOp.SExt : IrConvOp.ZExt,
                    value, IrType.Int(targetIntBits));
            return t.Kind == IrTypeKind.Pointer ? _b.Conv(IrConvOp.Bitcast, value, t) : value;
        }

        if (t.Bits < s.Bits)
            return _b.Conv(IrConvOp.Trunc, source.Value, t);
        return _b.Conv(source.Type.Signed ? IrConvOp.SExt : IrConvOp.ZExt, source.Value, t);
    }

    /// <summary>The pointee type of a pointer, or a diagnostic if it has none (e.g. a bare address).</summary>
    private static IrType Pointee(CsType pointer) =>
        pointer.Ir.Element ?? throw new CSharpNotSupportedException("pointer arithmetic requires a typed pointee.");

    private static bool IsComparison(SyntaxKind kind) => kind is
        SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
        SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or
        SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;

    private static IrCompareOp CompareOp(SyntaxKind kind, bool signed) => kind switch
    {
        SyntaxKind.EqualsExpression => IrCompareOp.Eq,
        SyntaxKind.NotEqualsExpression => IrCompareOp.Ne,
        SyntaxKind.LessThanExpression => signed ? IrCompareOp.Slt : IrCompareOp.Ult,
        SyntaxKind.LessThanOrEqualExpression => signed ? IrCompareOp.Sle : IrCompareOp.Ule,
        SyntaxKind.GreaterThanExpression => signed ? IrCompareOp.Sgt : IrCompareOp.Ugt,
        SyntaxKind.GreaterThanOrEqualExpression => signed ? IrCompareOp.Sge : IrCompareOp.Uge,
        _ => throw new CSharpNotSupportedException($"unsupported comparison '{kind}'."),
    };

    private static IrBinaryOp ArithOp(SyntaxKind kind, bool signed) => kind switch
    {
        SyntaxKind.AddExpression => IrBinaryOp.Add,
        SyntaxKind.SubtractExpression => IrBinaryOp.Sub,
        SyntaxKind.MultiplyExpression => IrBinaryOp.Mul,
        SyntaxKind.DivideExpression => signed ? IrBinaryOp.SDiv : IrBinaryOp.UDiv,
        SyntaxKind.ModuloExpression => signed ? IrBinaryOp.SRem : IrBinaryOp.URem,
        SyntaxKind.BitwiseAndExpression => IrBinaryOp.And,
        SyntaxKind.BitwiseOrExpression => IrBinaryOp.Or,
        SyntaxKind.ExclusiveOrExpression => IrBinaryOp.Xor,
        SyntaxKind.LeftShiftExpression => IrBinaryOp.Shl,
        SyntaxKind.RightShiftExpression => signed ? IrBinaryOp.AShr : IrBinaryOp.LShr,
        _ => throw new CSharpNotSupportedException($"unsupported operator '{kind}'."),
    };

    /// <summary>A compound assignment (<c>+=</c> etc.) uses the same operator table as its plain
    /// form; map the assignment kind to the base binary kind and reuse <see cref="ArithOp"/>.</summary>
    private static IrBinaryOp CompoundOp(SyntaxKind kind, bool signed) => ArithOp(kind switch
    {
        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
        SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
        SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
        SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
        SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
        SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
        SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
        SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
        SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
        _ => throw new CSharpNotSupportedException($"unsupported compound assignment '{kind}'."),
    }, signed);
}
