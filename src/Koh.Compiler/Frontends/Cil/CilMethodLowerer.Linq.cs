using Koh.Compiler.Ir;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// LINQ call-site interception: <c>arr.Where(..).Select(..).Sum()</c> and similar (see CLAUDE.md's
/// "array LINQ reductions" subset entry). <c>System.Linq.Enumerable</c>'s own IL bodies are
/// categorically unlowerable on SM83 (isinst runtime dispatch, spans, checked generic math over
/// static-abstract interfaces — see the design spike's finding (c)), so a <c>call</c> to one of its
/// methods is never resolved/lowered as an ordinary call: it is pattern-matched here and the whole
/// chain is rewritten to a loop, porting <c>CSharpFrontend.MethodLowerer.TryLowerLinq</c>'s IR shape
/// (accumulator seeding/widening, Where's conditional-continue, Max/Min's seed-at-0 rule) with each
/// lambda becoming a direct call to its resolved target (see <c>CilMethodLowerer.Delegates.cs</c>) in
/// place of syntax-tree inlining. A pipeline whose source doesn't trace to a <c>newarr</c>'d array, or
/// whose lambda argument doesn't resolve to a single statically-known method, is a diagnostic — same
/// "no indirect backend" rule as delegate invocation.
/// </summary>
internal sealed partial class CilMethodLowerer
{
    private readonly record struct LinqOp(string Kind, MethodDefinition Target, IrValue Env);

    private sealed record LinqPipeline(
        IrValue ArrayBase,
        IrType ElemType,
        bool Signed,
        IrValue Count,
        IReadOnlyList<LinqOp> Ops
    );

    // A chain call's (never-materialized) result placeholder -> the pipeline it stands for. Mirrors
    // _ldftnProvenance/_pendingDelegateProvenance's "compile-time-only value" pattern.
    private readonly Dictionary<IrValue, LinqPipeline> _pendingLinqPipeline = new();

    private static readonly HashSet<string> LinqChainOps = new(StringComparer.Ordinal)
    {
        "Where",
        "Select",
    };
    private static readonly HashSet<string> LinqTerminals = new(StringComparer.Ordinal)
    {
        "Sum",
        "Count",
        "Max",
        "Min",
        "Any",
        "All",
    };

    /// <summary>True for any <c>System.Linq.Enumerable</c> static method — the single interception
    /// point <see cref="LowerCall"/> uses to divert before its normal external-call diagnostic.</summary>
    private static bool IsLinqEnumerableCall(MethodReference calleeRef) =>
        !calleeRef.HasThis && calleeRef.DeclaringType.FullName == "System.Linq.Enumerable";

    private void LowerLinqCall(MethodReference calleeRef, List<IrValue> stack)
    {
        var name = calleeRef.Name;
        if (LinqChainOps.Contains(name))
        {
            var lambdaEnv = Pop(stack);
            if (!_pendingDelegateProvenance.TryGetValue(lambdaEnv, out var target))
                throw new CilNotSupportedException(
                    $"LINQ '{name}' in '{_method.FullName}' needs a statically-resolved lambda (an "
                        + "indirect delegate target is out of scope)."
                );
            var source = Pop(stack);
            var pipeline = ResolvePipeline(source);
            var ops = pipeline.Ops.Append(new LinqOp(name, target, lambdaEnv)).ToList();
            var placeholder = IrBuilder.ConstInt(IrType.I16, 0);
            _pendingLinqPipeline[placeholder] = pipeline with { Ops = ops };
            stack.Add(placeholder);
            return;
        }
        if (LinqTerminals.Contains(name))
        {
            LowerLinqTerminal(name, calleeRef, stack);
            return;
        }
        throw new CilNotSupportedException(
            $"unsupported LINQ method '{calleeRef.FullName}' in '{_method.FullName}' (phase supports "
                + "Where/Select pipelines ending in Sum/Count/Any/All, plus Max/Min directly on an array)."
        );
    }

    /// <summary>A lambda call as used inside the LINQ loop body — always value-returning (a
    /// predicate/selector/mapper), never void, unlike <see cref="InvokeDelegate"/>'s general case.</summary>
    private IrValue InvokeLambda(MethodDefinition target, IrValue env, IrValue arg) =>
        InvokeDelegate(target, env, [arg])
        ?? throw new CilNotSupportedException(
            $"LINQ lambda '{target.FullName}' in '{_method.FullName}' must return a value."
        );

    private LinqPipeline ResolvePipeline(IrValue source)
    {
        if (_pendingLinqPipeline.TryGetValue(source, out var pipeline))
            return pipeline;
        if (_pendingArrayInfo.TryGetValue(source, out var info))
            return new LinqPipeline(source, info.ElemType, info.Signed, info.Count, []);
        throw new CilNotSupportedException(
            $"LINQ source in '{_method.FullName}' could not be traced to a 'newarr'-allocated array "
                + "(phase supports LINQ directly over an array)."
        );
    }

    private void LowerLinqTerminal(string termOp, MethodReference calleeRef, List<IrValue> stack)
    {
        LinqOp? termLambda = null;
        if (calleeRef.Parameters.Count == 2)
        {
            var env = Pop(stack);
            if (!_pendingDelegateProvenance.TryGetValue(env, out var target))
                throw new CilNotSupportedException(
                    $"LINQ '{termOp}' in '{_method.FullName}' needs a statically-resolved lambda (an "
                        + "indirect delegate target is out of scope)."
                );
            termLambda = new LinqOp(termOp, target, env);
        }
        var source = Pop(stack);
        var pipeline = ResolvePipeline(source);

        bool isMinMax = termOp is "Max" or "Min";
        if (isMinMax && pipeline.Ops.Count > 0)
            throw new CilNotSupportedException(
                $"{termOp}() in '{_method.FullName}' is only supported directly on an array, not after "
                    + "a Where/Select pipeline (phase-supported surface)."
            );
        if (termOp == "All" && termLambda is null)
            throw new CilNotSupportedException(
                $"All() in '{_method.FullName}' requires a predicate."
            );

        var elemType = pipeline.ElemType;
        var elemSigned = pipeline.Signed;
        var accType = termOp switch
        {
            "Count" => IrType.I16,
            "Any" or "All" => IrType.I8,
            "Sum" => elemType.SizeInBytes >= 8 ? elemType : IrType.I32,
            _ => elemType,
        };
        var accSigned = termOp switch
        {
            "Count" or "Any" or "All" => false,
            "Sum" => elemType.SizeInBytes >= 8 ? elemSigned : true,
            _ => elemSigned,
        };

        var acc = _b.Alloca(accType);
        var iSlot = _b.Alloca(IrType.I16);
        var basePtr = pipeline.ArrayBase;

        var start = isMinMax ? 1 : 0;
        _b.Store(IrBuilder.ConstInt(IrType.I16, start), iSlot);
        var initial = isMinMax
            ? WidenToStack(
                _b.Load(ElementPointer(basePtr, IrBuilder.ConstInt(IrType.I16, 0), elemType)),
                elemSigned
            )
            : IrBuilder.ConstInt(accType, termOp == "All" ? 1 : 0);
        _b.Store(CoerceStore(initial, accType), acc);

        var fn = _function;
        var head = fn.AppendBlock("linq.head");
        var body = fn.AppendBlock("linq.body");
        var cont = fn.AppendBlock("linq.cont");
        var done = fn.AppendBlock("linq.done");
        _b.Br(head);

        _b.PositionAtEnd(head);
        _b.CondBr(_b.Compare(IrCompareOp.Ult, _b.Load(iSlot), pipeline.Count), body, done);

        _b.PositionAtEnd(body);
        var e = WidenToStack(
            _b.Load(ElementPointer(basePtr, _b.Load(iSlot), elemType)),
            elemSigned
        );
        foreach (var op in pipeline.Ops)
        {
            if (op.Kind == "Where")
            {
                var keep = InvokeLambda(op.Target, op.Env, e);
                var take = fn.AppendBlock("linq.take");
                _b.CondBr(AsBool(keep), take, cont);
                _b.PositionAtEnd(take);
            }
            else // Select
            {
                e = InvokeLambda(op.Target, op.Env, e);
            }
        }

        switch (termOp)
        {
            case "Sum":
                if (termLambda is { } sel)
                    e = InvokeLambda(sel.Target, sel.Env, e);
                _b.Store(_b.Add(_b.Load(acc), CoerceStore(e, accType)), acc);
                break;
            case "Count":
                if (termLambda is { } pred)
                {
                    var keep = InvokeLambda(pred.Target, pred.Env, e);
                    var take = fn.AppendBlock("linq.take");
                    _b.CondBr(AsBool(keep), take, cont);
                    _b.PositionAtEnd(take);
                }
                _b.Store(_b.Add(_b.Load(acc), IrBuilder.ConstInt(IrType.I16, 1)), acc);
                break;
            case "Max":
            case "Min":
            {
                var cmp = accSigned
                    ? (termOp == "Max" ? IrCompareOp.Sgt : IrCompareOp.Slt)
                    : (termOp == "Max" ? IrCompareOp.Ugt : IrCompareOp.Ult);
                var replace = fn.AppendBlock("linq.rep");
                _b.CondBr(_b.Compare(cmp, CoerceStore(e, accType), _b.Load(acc)), replace, cont);
                _b.PositionAtEnd(replace);
                _b.Store(CoerceStore(e, accType), acc);
                break;
            }
            case "Any":
            case "All":
            {
                var lam = termLambda!.Value;
                var keep = InvokeLambda(lam.Target, lam.Env, e);
                var hit = fn.AppendBlock("linq.hit");
                if (termOp == "Any")
                    _b.CondBr(AsBool(keep), hit, cont);
                else
                    _b.CondBr(AsBool(keep), cont, hit);
                _b.PositionAtEnd(hit);
                _b.Store(IrBuilder.ConstInt(IrType.I8, termOp == "Any" ? 1 : 0), acc);
                break;
            }
        }
        _b.Br(cont);

        _b.PositionAtEnd(cont);
        _b.Store(_b.Add(_b.Load(iSlot), IrBuilder.ConstInt(IrType.I16, 1)), iSlot);
        _b.Br(head);

        _b.PositionAtEnd(done);
        stack.Add(WidenToStack(_b.Load(acc), accSigned));
    }

    /// <summary>A resolved predicate's call result (widened to the CIL stack's i32, per
    /// <see cref="WidenToStack"/>) as a branch condition — nonzero is true, matching <c>brtrue</c>'s
    /// own semantics elsewhere in this lowerer.</summary>
    private IrValue AsBool(IrValue predicateResult) =>
        _b.Compare(IrCompareOp.Ne, predicateResult, IrBuilder.ConstInt(predicateResult.Type, 0));
}
