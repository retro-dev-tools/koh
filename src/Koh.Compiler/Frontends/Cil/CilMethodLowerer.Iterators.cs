using Koh.Compiler.Ir;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Iterator/foreach support: concrete-type propagation (so an interface-typed <c>callvirt</c> whose
/// receiver traces to a known concrete type — the design spike's finding (d): a sealed, compiler-
/// generated state-machine class with private explicit interface implementations — devirtualizes even
/// though the declared method is virtual/non-final/non-sealed-declaring-type) and <c>try/finally</c>
/// lowering (every <c>foreach</c> wraps its loop in a <c>finally</c> that disposes the enumerator).
///
/// Devirtualization here is an EXTENSION of <c>LowerInstanceCall</c>'s existing sealed/final rule (see
/// <c>CilMethodLowerer.Delegates.cs</c>), not a replacement: that rule alone already handles a
/// virtual-but-resolvable call; this adds "or the receiver's concrete type has a matching override",
/// via <see cref="ResolveOverride"/>.
///
/// <c>try/finally</c> lowering does not build a general exception model — the target never throws, so
/// a <c>finally</c> always runs on the normal path. Semantics collapse to: every <c>leave</c> out of the
/// try region gets a CLONE of the handler's instructions inlined at that point (translating
/// <c>endfinally</c> to a branch to the <c>leave</c>'s own target), and the handler's OWN instruction
/// range is excluded from the method's normal linear block computation entirely (it is never reachable
/// except via a <c>leave</c>, which this frontend never emits as a genuine forward branch to the
/// original handler bytes). <c>catch</c>/<c>filter</c>/<c>fault</c> handlers are diagnostics — there is
/// no exception model to lower them against.
/// </summary>
internal sealed partial class CilMethodLowerer
{
    // ---- Concrete-type tracking (see class remarks) -------------------------------------------

    // A value's exact runtime type, when known statically at compile time (a 'newobj' allocation site,
    // or a call whose own concrete return type CilLoweringContext inferred) — mirrors
    // _pendingDelegateProvenance/_pendingArrayInfo's "compile-time-only side table keyed by IrValue
    // reference identity" pattern. Propagated through locals (see LoadLocal/StoreLocal) and 'this'
    // parameters of a sealed declaring type (see LoadArg) exactly the same way.
    private readonly Dictionary<IrValue, TypeDefinition> _pendingConcreteType = new();
    private readonly Dictionary<VariableDefinition, TypeDefinition> _localConcreteType = new();

    /// <summary>Find the method on <paramref name="concreteType"/> that actually implements/overrides
    /// <paramref name="target"/> (an interface method or a virtual method declared higher up) — first
    /// via an explicit <c>.override</c> entry (a private explicit interface implementation; this is
    /// exactly how Roslyn wires an iterator state machine's <c>IEnumerator&lt;T&gt;</c>/<c>IDisposable</c>
    /// members — see the design spike's finding (d)), then by name/parameter-count as a fallback for an
    /// ordinary (implicit, public) override. Returns null if nothing matches — the caller reports a
    /// diagnostic rather than guessing.</summary>
    private static MethodDefinition? ResolveOverride(
        TypeDefinition concreteType,
        MethodDefinition target
    )
    {
        foreach (var candidate in concreteType.Methods)
        foreach (var ov in candidate.Overrides)
        {
            var resolved = CilModuleLowerer.ResolveSafe(ov.DeclaringType) is not null
                ? ov.Resolve()
                : null;
            if (resolved is not null && resolved.FullName == target.FullName)
                return candidate;
        }
        foreach (var candidate in concreteType.Methods)
        {
            if (
                candidate.IsVirtual
                && candidate.Name == target.Name
                && candidate.Parameters.Count == target.Parameters.Count
            )
                return candidate;
        }
        return null;
    }

    // ---- try/finally (see class remarks) -------------------------------------------------------

    // Every instruction inside [HandlerStart, HandlerEnd) of any 'finally' handler this method's body
    // declares — computed once, up front, in Run(). Excluded entirely from normal leader computation
    // and from the main per-instruction simulation loop; only ever visited via a clone driven by
    // LowerFinallyBody at each 'leave' inside the corresponding try region.
    private readonly HashSet<Instruction> _finallyHandlerInstructions = new();
    private List<ExceptionHandler> _finallyHandlers = [];

    /// <summary>Populate <see cref="_finallyHandlerInstructions"/>/<see cref="_finallyHandlers"/> from
    /// <see cref="MethodDefinition.Body"/>'s exception handler table, diagnosing (not throwing into
    /// caller code via an unrelated path) any handler kind this frontend has no exception model for.</summary>
    private void PrepareExceptionHandlers()
    {
        if (!_method.Body.HasExceptionHandlers)
            return;
        foreach (var handler in _method.Body.ExceptionHandlers)
        {
            if (handler.HandlerType != ExceptionHandlerType.Finally)
                throw new CilNotSupportedException(
                    $"'{handler.HandlerType}' exception handler in '{_method.FullName}' is unsupported "
                        + "(phase supports a 'finally' region only — the foreach-disposal shape; the "
                        + "target never throws, so there is no general exception model)."
                );
            _finallyHandlers.Add(handler);
            foreach (var instr in _method.Body.Instructions)
                if (
                    instr.Offset >= handler.HandlerStart.Offset
                    && (handler.HandlerEnd is null || instr.Offset < handler.HandlerEnd.Offset)
                )
                    _finallyHandlerInstructions.Add(instr);
        }
    }

    /// <summary>The innermost 'finally' handler whose try region contains <paramref name="leave"/>, or
    /// null if it isn't inside any (an ordinary <c>leave</c> out of a plain block, if this frontend's
    /// input ever produces one outside a foreach's try/finally — not seen in practice, but handled by
    /// falling back to a direct branch in <see cref="Simulate"/>'s <c>Code.Leave</c> case).</summary>
    private ExceptionHandler? FindEnclosingFinally(Instruction leave)
    {
        ExceptionHandler? best = null;
        foreach (var handler in _finallyHandlers)
        {
            if (leave.Offset < handler.TryStart.Offset || leave.Offset >= handler.TryEnd.Offset)
                continue;
            if (
                best is null
                || handler.TryEnd.Offset - handler.TryStart.Offset
                    < best.TryEnd.Offset - best.TryStart.Offset
            )
                best = handler;
        }
        return best;
    }

    /// <summary>Clone <paramref name="handler"/>'s own instructions inline at the current insertion
    /// point, translating its (small, self-contained) internal control flow with a private mini leader/
    /// block computation — reusing <see cref="Simulate"/>'s opcode handling is not viable here because
    /// <c>Simulate</c> resolves branch targets through the method-wide <see cref="_blockOf"/> table,
    /// which only knows about the NORMAL (non-handler) instruction stream; a handler clone needs its own
    /// fresh blocks every time it's inlined (once per 'leave' that reaches it — always exactly once for
    /// every shape this frontend's fixtures produce, but the mechanism doesn't assume that). Only the
    /// small, fixed opcode set the foreach-disposal shape actually contains is supported: <c>nop</c>,
    /// <c>ldloc*</c>, <c>brfalse*</c>, <c>pop</c>, <c>call</c>/<c>callvirt</c>, and <c>endfinally</c>
    /// (translated to a branch to <paramref name="leaveTarget"/>) — anything else is a diagnostic.</summary>
    private void LowerFinallyBody(ExceptionHandler handler, IrBasicBlock leaveTarget)
    {
        var region = _method
            .Body.Instructions.Where(i =>
                i.Offset >= handler.HandlerStart.Offset
                && (handler.HandlerEnd is null || i.Offset < handler.HandlerEnd.Offset)
            )
            .ToList();
        if (region.Count == 0)
            return;

        var leaders = new HashSet<Instruction> { region[0] };
        foreach (var instr in region)
        {
            var code = instr.OpCode.Code;
            if (code is Code.Brfalse or Code.Brfalse_S or Code.Brtrue or Code.Brtrue_S)
                leaders.Add((Instruction)instr.Operand);
            if (
                (code is Code.Brfalse or Code.Brfalse_S or Code.Brtrue or Code.Brtrue_S)
                && instr.Next is { } next
                && next.Offset < (handler.HandlerEnd?.Offset ?? int.MaxValue)
            )
                leaders.Add(next);
        }

        var localBlockOf = new Dictionary<Instruction, IrBasicBlock>();
        foreach (var instr in region.Where(leaders.Contains))
            localBlockOf[instr] = _function.AppendBlock("finally.clone");

        _b.Br(localBlockOf[region[0]]);
        var stack = new List<IrValue>();
        foreach (var instr in region)
        {
            // Same builder-is-truth rule as the outer Run() loop (see its remarks) — Call/Callvirt here
            // can recurse into EnsureLowered for an as-yet-unlowered target, but that constructs an
            // entirely separate CilMethodLowerer with its own IrBuilder, so THIS builder's position is
            // never touched by it; the auto-fallthrough-Br is still needed for this clone's OWN internal
            // branch (the dispose-guard) the same way it's needed in the outer method body.
            if (
                localBlockOf.TryGetValue(instr, out var leaderBlock)
                && !ReferenceEquals(leaderBlock, _b.CurrentBlock)
            )
            {
                if (_b.CurrentBlock.Terminator is null)
                    _b.Br(leaderBlock);
                _b.PositionAtEnd(leaderBlock);
            }

            switch (instr.OpCode.Code)
            {
                case Code.Nop:
                    break;
                case Code.Pop:
                    Pop(stack);
                    break;
                case Code.Ldloc_0:
                    stack.Add(LoadLocal(_method.Body.Variables[0]));
                    break;
                case Code.Ldloc_1:
                    stack.Add(LoadLocal(_method.Body.Variables[1]));
                    break;
                case Code.Ldloc_2:
                    stack.Add(LoadLocal(_method.Body.Variables[2]));
                    break;
                case Code.Ldloc_3:
                    stack.Add(LoadLocal(_method.Body.Variables[3]));
                    break;
                case Code.Ldloc_S:
                case Code.Ldloc:
                    stack.Add(LoadLocal((VariableDefinition)instr.Operand));
                    break;
                case Code.Brfalse:
                case Code.Brfalse_S:
                case Code.Brtrue:
                case Code.Brtrue_S:
                {
                    var v = AsComparable(Pop(stack));
                    var isTrue = instr.OpCode.Code is Code.Brtrue or Code.Brtrue_S;
                    var cmp = _b.Compare(
                        isTrue ? IrCompareOp.Ne : IrCompareOp.Eq,
                        v,
                        IrBuilder.ConstInt(v.Type, 0)
                    );
                    var target = localBlockOf[(Instruction)instr.Operand];
                    var fallThrough = localBlockOf[instr.Next!];
                    _b.CondBr(cmp, target, fallThrough);
                    break;
                }
                case Code.Call:
                    LowerCall((MethodReference)instr.Operand, stack);
                    break;
                case Code.Callvirt:
                    LowerCallvirt((MethodReference)instr.Operand, stack);
                    break;
                case Code.Endfinally:
                    _b.Br(leaveTarget);
                    break;
                default:
                    throw new CilNotSupportedException(
                        $"unsupported opcode '{instr.OpCode.Name}' inside a 'finally' handler in "
                            + $"'{_method.FullName}' (phase supports the foreach-disposal shape only)."
                    );
            }
        }
    }
}
