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
    private readonly IrBuilder _b = new();
    private readonly Dictionary<string, (IrValue Slot, CsType Type)> _locals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IrValue Address, CsType Element)> _refs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IrValue ArrayPtr, CsType Element, int Length)> _arrays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IrValue BasePtr, CsStruct Info)> _structLocals = new(StringComparer.Ordinal);
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
        IReadOnlyList<(IrGlobal Global, long Value, CsType Type)> staticInits)
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
    }

    public void Lower()
    {
        var entry = _method.Fn.AppendBlock("entry");
        _b.PositionAtEnd(entry);

        // Parameters: a normal one gets a mutable slot seeded with its value; a ref/out parameter
        // arrives as an address, so its "place" is that address itself (reads/writes deref it).
        for (int i = 0; i < _method.Fn.Parameters.Count; i++)
        {
            var p = _method.Fn.Parameters[i];
            if (_method.RefParams[i])
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
        var element = CSharpFrontend.ResolveType(arrayType.ElementType, _enums);
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

    /// <summary>Compute the pointer to a struct local's field <c>s.field</c>.</summary>
    private (IrValue Pointer, CsType Type)? StructFieldPointer(MemberAccessExpressionSyntax member)
    {
        if (member.Expression is not IdentifierNameSyntax id || !_structLocals.TryGetValue(id.Identifier.Text, out var s))
            return null;

        var fieldName = member.Name.Identifier.Text;
        foreach (var field in s.Info.Fields)
            if (field.Name == fieldName)
            {
                // The offset is aligned to the field size, so index = offset / size is exact.
                int index = field.Offset / ((field.Type.Ir.Bits + 7) / 8);
                return (_b.Gep(s.BasePtr, IrBuilder.ConstInt(IrType.I16, index), field.Type.Ir), field.Type);
            }
        throw new CSharpNotSupportedException($"struct has no field '{fieldName}'.");
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

        var type = expected ?? CsType.U16;
        long value = Convert.ToInt64(lit.Token.Value);
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
            SyntaxKind.UnaryMinusExpression =>
                (_b.Sub(IrBuilder.ConstInt(type.Ir, 0), value), type),
            SyntaxKind.LogicalNotExpression =>
                (_b.Binary(IrBinaryOp.Xor, value, IrBuilder.ConstInt(IrType.I8, 1)), CsType.Bool),
            SyntaxKind.UnaryPlusExpression => (value, type),
            _ => throw new CSharpNotSupportedException($"unsupported unary operator '{unary.OperatorToken.Text}'."),
        };
    }

    private (IrValue, CsType) LowerIncDec(ExpressionSyntax operand, bool increment, bool prefix)
    {
        if (operand is not IdentifierNameSyntax id || WritePlace(id.Identifier.Text) is not { } place)
            throw new CSharpNotSupportedException("++/-- requires a variable.");

        var old = _b.Load(place.Pointer);
        var updated = _b.Binary(increment ? IrBinaryOp.Add : IrBinaryOp.Sub, old, IrBuilder.ConstInt(place.Type.Ir, 1));
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

    private (IrValue, CsType) LowerConditional(ConditionalExpressionSyntax cond, CsType? expected)
    {
        var type = expected ?? CsType.U16;
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
            var (left, lt) = LowerExpression(binary.Left, expected);
            var (right, rt) = LowerExpression(binary.Right, lt);
            return (_b.Compare(CompareOp(kind, lt.Signed), left, Coerce((right, rt), lt)), CsType.Bool);
        }

        var (l, ltype) = LowerExpression(binary.Left, expected);
        var (r, rtype) = LowerExpression(binary.Right, ltype);
        var rc = Coerce((r, rtype), ltype);
        return (_b.Binary(ArithOp(kind, ltype.Signed), l, rc), ltype);
    }

    private (IrValue, CsType) LowerAssignment(AssignmentExpressionSyntax assign)
    {
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

        IrValue result;
        if (assign.Kind() == SyntaxKind.SimpleAssignmentExpression)
        {
            result = Coerce(LowerExpression(assign.Right, type), type);
        }
        else
        {
            var current = _b.Load(pointer);
            var rhs = Coerce(LowerExpression(assign.Right, type), type);
            result = _b.Binary(CompoundOp(assign.Kind(), type.Signed), current, rhs);
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
            if (member.Name.Identifier.Text == "Length" && _arrays.TryGetValue(subject.Identifier.Text, out var arr))
            {
                var type = expected ?? (arr.Length <= 0xFF ? CsType.U8 : CsType.U16);
                return (IrBuilder.ConstInt(type.Ir, arr.Length), type);
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
        for (int i = 0; i < argList.Count; i++)
        {
            if (callee.RefParams[i])
                args.Add(LvalueAddress(argList[i].Expression)); // ref/out: pass the address
            else
                args.Add(Coerce(LowerExpression(argList[i].Expression, callee.Params[i]), callee.Params[i]));
        }

        var result = _b.Call(callee.Fn, args);
        return (result, callee.Return ?? CsType.U8);
    }

    // ---- Types & operators -------------------------------------------------

    /// <summary>Widen/narrow a value to <paramref name="target"/> using its source signedness.</summary>
    private IrValue Coerce((IrValue Value, CsType Type) source, CsType target)
    {
        if (source.Type.Ir.Bits == target.Ir.Bits)
            return source.Value;
        if (target.Ir.Bits < source.Type.Ir.Bits)
            return _b.Conv(IrConvOp.Trunc, source.Value, target.Ir);
        return _b.Conv(source.Type.Signed ? IrConvOp.SExt : IrConvOp.ZExt, source.Value, target.Ir);
    }

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

    private static IrBinaryOp CompoundOp(SyntaxKind kind, bool signed) => kind switch
    {
        SyntaxKind.AddAssignmentExpression => IrBinaryOp.Add,
        SyntaxKind.SubtractAssignmentExpression => IrBinaryOp.Sub,
        SyntaxKind.MultiplyAssignmentExpression => IrBinaryOp.Mul,
        SyntaxKind.DivideAssignmentExpression => signed ? IrBinaryOp.SDiv : IrBinaryOp.UDiv,
        SyntaxKind.ModuloAssignmentExpression => signed ? IrBinaryOp.SRem : IrBinaryOp.URem,
        SyntaxKind.AndAssignmentExpression => IrBinaryOp.And,
        SyntaxKind.OrAssignmentExpression => IrBinaryOp.Or,
        SyntaxKind.ExclusiveOrAssignmentExpression => IrBinaryOp.Xor,
        SyntaxKind.LeftShiftAssignmentExpression => IrBinaryOp.Shl,
        SyntaxKind.RightShiftAssignmentExpression => signed ? IrBinaryOp.AShr : IrBinaryOp.LShr,
        _ => throw new CSharpNotSupportedException($"unsupported compound assignment '{kind}'."),
    };
}
