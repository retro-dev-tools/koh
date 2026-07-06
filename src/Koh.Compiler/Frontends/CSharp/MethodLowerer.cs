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
    private readonly IrBuilder _b = new();
    private readonly Dictionary<string, (IrValue Slot, CsType Type)> _locals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CsType Type, long Value)> _consts = new(StringComparer.Ordinal);
    private readonly Stack<(IrBasicBlock Break, IrBasicBlock Continue)> _loops = new();

    public MethodLowerer(
        CsMethod method,
        BlockSyntax? body,
        ArrowExpressionClauseSyntax? arrow,
        IReadOnlyDictionary<string, CsMethod> methods,
        IReadOnlyDictionary<string, CsEnum> enums)
    {
        _method = method;
        _body = body;
        _arrow = arrow;
        _methods = methods;
        _enums = enums;
    }

    public void Lower()
    {
        var entry = _method.Fn.AppendBlock("entry");
        _b.PositionAtEnd(entry);

        // Give every parameter a mutable slot seeded with its incoming value.
        for (int i = 0; i < _method.Fn.Parameters.Count; i++)
        {
            var p = _method.Fn.Parameters[i];
            var slot = _b.Alloca(p.Type);
            _b.Store(p, slot);
            _locals[p.Name!] = (slot, _method.Params[i]);
        }

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
                throw new CSharpNotSupportedException($"unsupported statement '{stmt.Kind()}'.");
        }
    }

    private void LowerLocalDeclaration(VariableDeclarationSyntax decl, bool isConst)
    {
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
                if (_consts.TryGetValue(id.Identifier.Text, out var constant))
                    return (IrBuilder.ConstInt(constant.Type.Ir, constant.Value), constant.Type);
                if (_locals.TryGetValue(id.Identifier.Text, out var local))
                    return (_b.Load(local.Slot), local.Type);
                throw new CSharpNotSupportedException($"unknown identifier '{id.Identifier.Text}'.");
            }

            case MemberAccessExpressionSyntax member:
                return LowerMemberAccess(member);

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
                throw new CSharpNotSupportedException($"unsupported expression '{expr.Kind()}'.");
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
        if (operand is not IdentifierNameSyntax id || !_locals.TryGetValue(id.Identifier.Text, out var local))
            throw new CSharpNotSupportedException("++/-- requires a local variable.");

        var old = _b.Load(local.Slot);
        var updated = _b.Binary(increment ? IrBinaryOp.Add : IrBinaryOp.Sub, old, IrBuilder.ConstInt(local.Type.Ir, 1));
        _b.Store(updated, local.Slot);
        return (prefix ? updated : old, local.Type);
    }

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
        if (assign.Left is not IdentifierNameSyntax id || !_locals.TryGetValue(id.Identifier.Text, out var local))
            throw new CSharpNotSupportedException("assignment target must be a local variable.");

        IrValue result;
        if (assign.Kind() == SyntaxKind.SimpleAssignmentExpression)
        {
            result = Coerce(LowerExpression(assign.Right, local.Type), local.Type);
        }
        else
        {
            var current = _b.Load(local.Slot);
            var rhs = Coerce(LowerExpression(assign.Right, local.Type), local.Type);
            result = _b.Binary(CompoundOp(assign.Kind(), local.Type.Signed), current, rhs);
        }

        _b.Store(result, local.Slot);
        return (result, local.Type);
    }

    private (IrValue, CsType) LowerMemberAccess(MemberAccessExpressionSyntax member)
    {
        // Enum member reference, e.g. Color.Red.
        if (member.Expression is IdentifierNameSyntax typeId && _enums.TryGetValue(typeId.Identifier.Text, out var e))
        {
            var name = member.Name.Identifier.Text;
            if (!e.Members.TryGetValue(name, out long value))
                throw new CSharpNotSupportedException($"enum '{typeId.Identifier.Text}' has no member '{name}'.");
            return (IrBuilder.ConstInt(e.Underlying.Ir, value), e.Underlying);
        }
        throw new CSharpNotSupportedException($"unsupported member access '{member}'.");
    }

    private (IrValue, CsType) LowerCall(InvocationExpressionSyntax call)
    {
        if (call.Expression is not IdentifierNameSyntax id || !_methods.TryGetValue(id.Identifier.Text, out var callee))
            throw new CSharpNotSupportedException($"unsupported call target '{call.Expression}'.");

        var args = new List<IrValue>();
        var argList = call.ArgumentList.Arguments;
        for (int i = 0; i < argList.Count; i++)
        {
            var paramType = callee.Params[i];
            args.Add(Coerce(LowerExpression(argList[i].Expression, paramType), paramType));
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
