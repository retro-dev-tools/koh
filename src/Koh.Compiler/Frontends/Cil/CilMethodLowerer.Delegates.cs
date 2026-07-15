using Koh.Compiler.Ir;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Objects, fields, and delegate/closure call-site interception (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>'s delegates/closures task). This is
/// frontend-level interception during CIL lowering, not an IR pass — the spike behind this task found
/// every fact it needs (a <c>ldftn</c> operand, a <c>newobj</c> type operand, the <c>&lt;&gt;9__</c>
/// cache idiom's fixed instruction shape, <c>MethodDefinition.Overrides</c>) is Cecil metadata already
/// erased by the time IR exists. There is no indirect-call backend: any delegate/virtual call that
/// cannot be resolved to exactly one target method is a diagnostic, never a throw into user code and
/// never a silent miscompile.
/// </summary>
internal sealed partial class CilMethodLowerer
{
    // ---- Delegate-cache idiom (see DetectDelegateCacheIdioms) ------------------------------------

    // Every instruction consumed by a recognized `<>9__`-style cache idiom except its starting
    // `ldsfld` (tracked separately below) — treated as a complete no-op by Simulate. The whole idiom
    // resolves at compile time to a single statically-known delegate target, so none of its guard/
    // branch/store machinery may survive into the emitted IR (see ComputeLeaders' matching exclusion).
    private readonly HashSet<Instruction> _suppressed = new();

    // The idiom's starting `ldsfld <cache field>` -> the one method its single `stsfld` (within the
    // idiom) resolves to. Simulate replaces it with a synthesized null env + this provenance, rather
    // than a real load of the (never-materialized) static field.
    private readonly Dictionary<Instruction, MethodDefinition> _ldsfldDelegateProvenance = new();

    // A `ldftn` pushes a placeholder IrValue (never emitted as real IR — see Simulate's Code.Ldftn
    // case) standing for its resolved target method; LowerNewobj's delegate-construction interception
    // consumes it immediately, one instruction later, matching Roslyn's fixed `ldftn; newobj Func`N`
    // shape (both configs — see the design spike).
    private readonly Dictionary<IrValue, MethodDefinition> _ldftnProvenance = new();

    // A resolved delegate's runtime representation is just its env pointer (a real IrValue — null for
    // a no-capture lambda, the display-class heap pointer for a capturing one); the target method is
    // compile-time provenance carried alongside it in this side table, keyed by the env IrValue's own
    // reference identity. Because the env is a real pointer it round-trips through an alloca/reload
    // (Debug's `stloc`/`ldloc` plumbing) or a `dup` (Release) for free — only this table needs to be
    // kept in sync at each hop (Dup does that for free too, since it pushes the same reference twice).
    private readonly Dictionary<IrValue, MethodDefinition> _pendingDelegateProvenance = new();

    // The mirror of _pendingDelegateProvenance for a value that has been committed to a local (assumed
    // single-store, matching every shape the design spike found): StoreLocal records it here, LoadLocal
    // re-derives a fresh _pendingDelegateProvenance entry for the value it loads.
    private readonly Dictionary<VariableDefinition, MethodDefinition> _localDelegateTarget = new();

    // A ReadOnlySpan<T>/Span<T> local's own storage address (see TryLowerSpanCall — the exact same
    // stable IrValue AddressOfLocal returns for every 'ldloca'/'ldloca.s' of that local, matching
    // _pendingArrayInfo/_structLocals' own reference-identity keying elsewhere in this frontend) -> its
    // element type/signedness/length, recorded the moment its constructing '.ctor(void*, int32)' call is
    // intercepted; consulted by a later 'get_Item'/'get_Length' call on the SAME local.
    private readonly Dictionary<
        IrValue,
        (IrType ElemType, bool Signed, IrValue Count)
    > _spanBackingInfo = new();

    /// <summary>
    /// Recognizes Roslyn's no-capture-lambda cache guard (design spike, finding (a)):
    /// <code>
    /// ldsfld  &lt;cache field&gt;      ; instr — the "start"
    /// dup
    /// brtrue.s SKIP
    /// pop
    /// ldsfld  &lt;singleton&gt;        ; e.g. `&lt;&gt;c::&lt;&gt;9`, ignored — the lambda body never
    ///                               ;   touches its own `this` (a `&lt;&gt;c` singleton has no fields)
    /// ldftn   M
    /// newobj  instance void Func`N::.ctor(object, native int)
    /// dup
    /// stsfld  &lt;cache field&gt;      ; must be the SAME field as the start, and fall straight into SKIP
    /// SKIP: ...
    /// </code>
    /// Structural, not name-based (per the design spike's explicit correction) — matched purely by
    /// opcode shape and the requirement that the delegate constructor's declaring type derives from
    /// <c>System.MulticastDelegate</c>. Confirmed config-stable (Debug adds only `nop`s elsewhere, never
    /// inside this run) by the spike's Cecil dumps of both Debug and Release Roslyn output. On a match,
    /// the 8 interior instructions are marked <see cref="_suppressed"/> and the start is recorded in
    /// <see cref="_ldsfldDelegateProvenance"/>; <see cref="ComputeLeaders"/> then never forks a block at
    /// the (now-dead) guard branch, so the whole idiom collapses to nothing at runtime.
    /// </summary>
    private void DetectDelegateCacheIdioms()
    {
        foreach (var instr in _method.Body.Instructions)
        {
            if (instr.OpCode.Code != Code.Ldsfld)
                continue;
            var n1 = instr.Next;
            if (n1?.OpCode.Code != Code.Dup)
                continue;
            var n2 = n1.Next;
            if (n2 is null || n2.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S))
                continue;
            var target = n2.Operand as Instruction;
            var n3 = n2.Next;
            if (n3?.OpCode.Code != Code.Pop)
                continue;
            var n4 = n3.Next;
            if (n4?.OpCode.Code != Code.Ldsfld)
                continue;
            var n5 = n4.Next;
            if (n5?.OpCode.Code != Code.Ldftn)
                continue;
            var n6 = n5.Next;
            if (n6?.OpCode.Code != Code.Newobj)
                continue;
            if (
                n6.Operand is not MethodReference ctorRef
                || CilModuleLowerer.ResolveSafe(ctorRef.DeclaringType) is not { } delegateType
                || !CilModuleLowerer.IsDelegateType(delegateType)
            )
                continue;
            var n7 = n6.Next;
            if (n7?.OpCode.Code != Code.Dup)
                continue;
            var n8 = n7.Next;
            if (n8?.OpCode.Code != Code.Stsfld)
                continue;
            if (!ReferenceEquals(n8.Next, target))
                continue; // must fall straight through into the guard's own branch target

            var startField = (instr.Operand as FieldReference)?.Resolve();
            var storedField = (n8.Operand as FieldReference)?.Resolve();
            if (startField is null || !ReferenceEquals(startField, storedField))
                continue;

            if (
                n5.Operand is not MethodReference methodRef
                || methodRef.Resolve() is not { } methodDef
            )
                continue;

            foreach (var s in new[] { n1, n2, n3, n4, n5, n6, n7, n8 })
                _suppressed.Add(s);
            _ldsfldDelegateProvenance[instr] = methodDef;
        }
    }

    // ---- Fields ------------------------------------------------------------------------------------

    /// <summary>The address of instance field <paramref name="fieldRef"/> on <paramref name="objRef"/>
    /// — an element-scaled <c>gep</c> (offset / element-size = index), exactly
    /// <c>CSharpFrontend.MethodLowerer.WritePlace</c>'s class-field addressing, which is what lets the
    /// same aligned-offset invariant (<see cref="CilClassLayout"/>'s remarks) drive both frontends'
    /// field access identically. <see cref="CilClassLayout.FieldInfo.Nested"/> is forwarded so a
    /// struct-typed field's caller (Ldfld/Stfld/Ldflda — see <c>CilMethodLowerer.Structs.cs</c>) can
    /// tell a scalar field (load/store a value) from an aggregate one (address only, byte-copy).</summary>
    private (IrValue Pointer, IrType Type, bool Signed, CilClassLayout? Nested) FieldPointer(
        FieldReference fieldRef,
        IrValue objRef
    )
    {
        var fieldDef =
            fieldRef.Resolve()
            ?? throw new CilNotSupportedException(
                $"cannot resolve field '{fieldRef.FullName}' in '{_method.FullName}'."
            );
        var layout = _ctx.GetLayout(fieldDef.DeclaringType);
        if (!layout.Fields.TryGetValue(fieldDef, out var info))
            throw new CilNotSupportedException(
                $"field '{fieldRef.FullName}' in '{_method.FullName}' is not supported (a static "
                    + "field, or a field whose type is outside the supported subset, is not laid out)."
            );
        var basePtr =
            objRef.Type.Kind == IrTypeKind.Pointer
                ? objRef
                : _b.Conv(IrConvOp.Bitcast, objRef, IrType.Pointer(IrType.I8));
        var elementSize = Math.Max(info.Type.SizeInBytes, 1);
        var ptr = _b.Gep(
            basePtr,
            IrBuilder.ConstInt(IrType.I16, info.Offset / elementSize),
            info.Type
        );
        return (ptr, info.Type, info.Signed, info.Nested);
    }

    // ---- Object construction -------------------------------------------------------------------------

    /// <summary>
    /// <c>newobj</c>: a delegate constructor (<c>Func`N::.ctor(object, native int)</c> — no IL body
    /// exists to call, <see cref="MethodImplAttributes.Runtime"/>) is intercepted, never allocated —
    /// see <see cref="_pendingDelegateProvenance"/>'s remarks. Anything else is a real heap allocation:
    /// bump the shared <c>__heap</c> pointer down by the type's laid-out size (see
    /// <see cref="CilClassLayout"/>), zero-fill it (heap memory is uninitialized), then call its
    /// constructor as an ordinary instance method — <c>System.Object::.ctor()</c> itself is a no-op
    /// (see <see cref="LowerInstanceCall"/>) — and leave the new instance pointer on the stack, exactly
    /// <c>CSharpFrontend.MethodLowerer.LowerNew</c>'s shape.
    /// </summary>
    private void LowerNewobj(MethodReference ctorRef, List<IrValue> stack)
    {
        var declaringType =
            CilModuleLowerer.ResolveSafe(ctorRef.DeclaringType)
            ?? throw new CilNotSupportedException(
                $"cannot resolve type of '{ctorRef.FullName}' in '{_method.FullName}'."
            );

        if (CilModuleLowerer.IsDelegateType(declaringType))
        {
            // Stack order (bottom to top): env object, then the ldftn method pointer — see the design
            // spike's finding (a)/(b): both the no-capture cache idiom and the direct capturing-lambda
            // shape push the env first, then `ldftn`, then `newobj`.
            var methodPtrValue = Pop(stack);
            var envValue = Pop(stack);
            if (!_ldftnProvenance.TryGetValue(methodPtrValue, out var target))
                throw new CilNotSupportedException(
                    $"delegate construction in '{_method.FullName}' could not be resolved to a single "
                        + "target method (expected a direct 'ldftn' immediately feeding the delegate "
                        + "constructor; an indirect-call backend is out of scope)."
                );
            _pendingDelegateProvenance[envValue] = target;
            stack.Add(envValue);
            return;
        }

        var ctorDef =
            ctorRef.Resolve()
            ?? throw new CilNotSupportedException(
                $"cannot resolve constructor '{ctorRef.FullName}' in '{_method.FullName}'."
            );

        var argCount = ctorRef.Parameters.Count;
        var ctorArgs = new IrValue[argCount];
        for (var i = argCount - 1; i >= 0; i--)
            ctorArgs[i] = Pop(stack);

        var layout = _ctx.GetLayout(declaringType);
        var heap =
            _ctx.HeapGlobal
            ?? throw new CilNotSupportedException(
                $"'new {declaringType.Name}' in '{_method.FullName}' needs the heap, but none was "
                    + "provisioned for this module."
            );
        var heapRef = IrBuilder.GlobalRef(heap);
        var raw = _b.Sub(_b.Load(heapRef), IrBuilder.ConstInt(IrType.I16, layout.Size));
        _b.Store(raw, heapRef);
        var basePtr = _b.Conv(IrConvOp.Bitcast, raw, IrType.Pointer(IrType.I8));
        EmitZeroFill(basePtr, layout.Size);

        var callee =
            _ctx.EnsureLowered(ctorDef)
            ?? throw new CilNotSupportedException(
                $"cannot lower constructor '{ctorDef.FullName}'."
            );
        var allArgs = new IrValue[argCount + 1];
        allArgs[0] = CoerceStore(basePtr, callee.Parameters[0].Type);
        for (var i = 0; i < argCount; i++)
            allArgs[i + 1] = PrepareArg(
                ctorArgs[i],
                ctorDef.Parameters,
                i,
                callee.Parameters[i + 1].Type
            );
        _b.Call(callee, allArgs);

        // The allocation's exact runtime type is known statically (right here, at the 'newobj' site) —
        // feeds concrete-type devirtualization (see CilMethodLowerer.Iterators.cs), most importantly
        // an iterator kickoff's `newobj <Iter>d__N::.ctor(...)` immediately followed by 'ret' through an
        // interface-typed return.
        _pendingConcreteType[basePtr] = declaringType;
        stack.Add(basePtr);
    }

    /// <summary>Zero <paramref name="size"/> bytes at <paramref name="basePtr"/> in a runtime loop
    /// (O(1) code regardless of size) — identical shape to
    /// <c>CSharpFrontend.MethodLowerer.EmitZeroFill</c>.</summary>
    private void EmitZeroFill(IrValue basePtr, int size)
    {
        if (size <= 0)
            return;
        var iSlot = _b.Alloca(IrType.I16);
        _b.Store(IrBuilder.ConstInt(IrType.I16, 0), iSlot);
        var head = _function.AppendBlock("newobj.zero.head");
        var body = _function.AppendBlock("newobj.zero.body");
        var done = _function.AppendBlock("newobj.zero.done");
        _b.Br(head);
        _b.PositionAtEnd(head);
        _b.CondBr(
            _b.Compare(IrCompareOp.Ult, _b.Load(iSlot), IrBuilder.ConstInt(IrType.I16, size)),
            body,
            done
        );
        _b.PositionAtEnd(body);
        _b.Store(IrBuilder.ConstInt(IrType.I8, 0), _b.Gep(basePtr, _b.Load(iSlot), IrType.I8));
        _b.Store(
            _b.Binary(IrBinaryOp.Add, _b.Load(iSlot), IrBuilder.ConstInt(IrType.I16, 1)),
            iSlot
        );
        _b.Br(head);
        _b.PositionAtEnd(done);
    }

    // ---- Calls through an instance (callvirt/instance call) ------------------------------------------

    /// <summary><c>callvirt</c>: a delegate's <c>Invoke</c> (no IL body — see
    /// <see cref="LowerDelegateInvoke"/>) is resolved through <see cref="_pendingDelegateProvenance"/>;
    /// anything else devirtualizes through <see cref="LowerInstanceCall"/>.</summary>
    private void LowerCallvirt(MethodReference calleeRef, List<IrValue> stack)
    {
        var declaringType = CilModuleLowerer.ResolveSafe(calleeRef.DeclaringType);
        if (
            declaringType is not null
            && CilModuleLowerer.IsDelegateType(declaringType)
            && calleeRef.Name == "Invoke"
        )
        {
            LowerDelegateInvoke(calleeRef, stack);
            return;
        }
        LowerInstanceCall(calleeRef, stack, isVirtualDispatch: true);
    }

    /// <summary>A delegate <c>Invoke</c> whose receiver traces (through <see
    /// cref="_pendingDelegateProvenance"/>) to exactly one statically-known target method becomes a
    /// direct call passing the env pointer as the target's <c>this</c> — the IR inliner then flattens
    /// it. An untraceable receiver (a delegate parameter with more than one possible caller-supplied
    /// target, or one built from a shape this frontend doesn't recognize) is a diagnostic, never a
    /// throw into user code — matches the design spike's explicit "no indirect-call backend" scope.</summary>
    private void LowerDelegateInvoke(MethodReference calleeRef, List<IrValue> stack)
    {
        var argCount = calleeRef.Parameters.Count;
        var args = new IrValue[argCount];
        for (var i = argCount - 1; i >= 0; i--)
            args[i] = Pop(stack);
        var receiver = Pop(stack);

        if (!_pendingDelegateProvenance.TryGetValue(receiver, out var target))
            throw new CilNotSupportedException(
                $"delegate invocation in '{_method.FullName}' could not be resolved to a single "
                    + "target method (the CIL frontend only supports a delegate whose target is "
                    + "statically known at the invocation site; an indirect-call backend is out of scope)."
            );

        var result = InvokeDelegate(target, receiver, args);
        if (result is not null)
            stack.Add(result);
    }

    /// <summary>Call a resolved delegate target directly, env pointer as <c>this</c> — the single
    /// mechanism behind both a real <c>Invoke</c> call site (see <see cref="LowerDelegateInvoke"/>)
    /// and an inlined LINQ lambda call (see <c>CilMethodLowerer.Linq.cs</c>), since both reduce to
    /// "call this statically-known instance method with this env and these args". Returns null for a
    /// void target (a LINQ predicate/selector is never void, but <c>LowerDelegateInvoke</c> needs the
    /// void case).</summary>
    private IrValue? InvokeDelegate(
        MethodDefinition target,
        IrValue env,
        IReadOnlyList<IrValue> args
    )
    {
        var callee =
            _ctx.EnsureLowered(target)
            ?? throw new CilNotSupportedException(
                $"cannot lower delegate target '{target.FullName}'."
            );
        var allArgs = new IrValue[args.Count + 1];
        allArgs[0] = CoerceStore(env, callee.Parameters[0].Type);
        for (var i = 0; i < args.Count; i++)
            allArgs[i + 1] = CoerceStore(args[i], callee.Parameters[i + 1].Type);
        var call = _b.Call(callee, allArgs);
        return callee.ReturnType.Kind != IrTypeKind.Void
            ? WidenToStack(call, IsSignedReturn(target))
            : null;
    }

    /// <summary>An instance call reached via plain <c>call</c> (always non-virtual by CIL definition —
    /// a base-ctor call, a <c>base.Method()</c> call) or a devirtualized <c>callvirt</c>.
    /// <c>System.Object::.ctor()</c> is a no-op (the managed CLR's own base ctor does nothing
    /// observable either). A genuinely virtual <c>callvirt</c> — <paramref name="isVirtualDispatch"/>,
    /// on a method that is virtual, not final, and whose declaring type isn't sealed — cannot be
    /// resolved to one target without concrete-type tracking (out of this task's scope; task 3's
    /// iterators need it) and is a diagnostic.</summary>
    /// <summary>
    /// Intercepts the three <c>System.ReadOnlySpan&lt;T&gt;</c>/<c>System.Span&lt;T&gt;</c> instance
    /// members Roslyn's own lowering of a <c>"..."u8</c> literal needs: the pointer/length constructor,
    /// and reading an element back out (<c>get_Item</c>, plus <c>get_Length</c> for completeness). A
    /// real Cecil dump of <c>ReadOnlySpan&lt;byte&gt; s = "hi"u8; return s[1];</c> confirms the exact
    /// shape: <c>ldloca.s V ; ldsflda &lt;blob&gt; ; ldc.i4 N ; call instance void
    /// ReadOnlySpan&lt;byte&gt;::.ctor(void*, int32)</c>, later followed by <c>ldloca.s V ; ldc.i4 idx ;
    /// call instance !0&amp; ReadOnlySpan&lt;byte&gt;::get_Item(int32) ; ldind.u1</c>. This is standard
    /// C#'s ONLY way to spell a byte-array-of-constants literal inline (see
    /// <c>CilMethodLowerer.Arrays.cs</c>'s <c>DetectArrayLiteralIdioms</c> remarks on why Koh's old
    /// "string literal as byte[] initializer" idiom cannot exist in a compiled assembly at all — it was
    /// Koh-legal-but-C#-illegal).
    ///
    /// Neither member's real implementation is lowerable (a <c>ref struct</c>'s internal managed-pointer
    /// field has no representable <see cref="IrType"/> — see <see cref="CilTypeMapper.Map"/>'s matching
    /// ReadOnlySpan/Span case), so both are intercepted by shape here instead: the span is represented
    /// exactly like any other array in this frontend (<c>CilMethodLowerer.Arrays.cs</c>) — its "value" is
    /// a raw base pointer, physically stored into the span-local's own <c>alloca</c> (so a later
    /// <c>ldloca</c> reference to the SAME local sees it, via <see cref="_spanBackingInfo"/> keyed by
    /// that alloca's own reference-stable <see cref="IrValue"/> — see <see cref="AddressOfLocal"/>).
    /// Returns false (no-op) for any other Span/ReadOnlySpan member, e.g. <c>Slice</c>/<c>CopyTo</c> —
    /// out of this task's scope, and a diagnostic (via the ordinary BCL-method path below) rather than a
    /// silent miscompile.
    /// </summary>
    private bool TryLowerSpanCall(
        MethodReference calleeRef,
        IrValue thisValue,
        IrValue[] args,
        List<IrValue> stack
    )
    {
        if (calleeRef.DeclaringType is not GenericInstanceType git)
            return false;
        if (
            git.ElementType.Namespace != "System"
            || git.ElementType.Name is not ("ReadOnlySpan`1" or "Span`1")
        )
            return false;
        var (elemType, elemSigned) = CilTypeMapper.Map(git.GenericArguments[0]);

        switch (calleeRef.Name)
        {
            case ".ctor" when args.Length == 2:
                _b.Store(CoerceStore(args[0], IrType.Pointer(elemType)), thisValue);
                _spanBackingInfo[thisValue] = (elemType, elemSigned, args[1]);
                return true;
            case "get_Item" when args.Length == 1:
            {
                if (!_spanBackingInfo.TryGetValue(thisValue, out var info))
                    throw new CilNotSupportedException(
                        $"'{calleeRef.FullName}' in '{_method.FullName}' indexes a "
                            + "ReadOnlySpan<T>/Span<T> this frontend cannot trace back to its "
                            + "constructing '.ctor' call (only the direct pointer/length "
                            + "constructor — the `\"...\"u8` literal's own shape — is supported)."
                    );
                var basePtr = _b.Load(thisValue);
                stack.Add(ElementPointer(basePtr, args[0], info.ElemType));
                return true;
            }
            case "get_Length" when args.Length == 0:
            {
                if (!_spanBackingInfo.TryGetValue(thisValue, out var info))
                    throw new CilNotSupportedException(
                        $"'{calleeRef.FullName}' in '{_method.FullName}' reads the length of a "
                            + "ReadOnlySpan<T>/Span<T> this frontend cannot trace back to its "
                            + "constructing '.ctor' call."
                    );
                stack.Add(WidenToStack(CoerceStore(info.Count, IrType.I32), signed: false));
                return true;
            }
            default:
                return false;
        }
    }

    private void LowerInstanceCall(
        MethodReference calleeRef,
        List<IrValue> stack,
        bool isVirtualDispatch
    )
    {
        var argCount = calleeRef.Parameters.Count;
        var args = new IrValue[argCount];
        for (var i = argCount - 1; i >= 0; i--)
            args[i] = Pop(stack);
        var thisValue = Pop(stack);

        if (TryLowerSpanCall(calleeRef, thisValue, args, stack))
            return;
        if (TryLowerStringCall(calleeRef, thisValue, args, stack))
            return;

        if (
            calleeRef.DeclaringType.FullName == "System.Object"
            && calleeRef.Name == ".ctor"
            && argCount == 0
        )
            return;

        var def =
            calleeRef.Resolve()
            ?? throw new CilNotSupportedException(
                $"cannot resolve instance call '{calleeRef.FullName}' in '{_method.FullName}'."
            );

        if (isVirtualDispatch && def.IsVirtual && !def.IsFinal && !def.DeclaringType.IsSealed)
        {
            // Not resolvable by the declaration alone — try concrete-type devirtualization (see
            // CilMethodLowerer.Iterators.cs): if the receiver traces to a known concrete type (a
            // 'newobj' allocation, or another call whose own concrete return type is known), find that
            // type's actual override of this interface/virtual method and call it directly instead.
            if (
                _pendingConcreteType.TryGetValue(thisValue, out var concreteType)
                && ResolveOverride(concreteType, def) is { } resolved
            )
            {
                def = resolved;
            }
            else
            {
                throw new CilNotSupportedException(
                    $"virtual call '{calleeRef.FullName}' in '{_method.FullName}' cannot be resolved to "
                        + "a single target (devirtualization needs a sealed declaring type, a final "
                        + "method, or a receiver traceable to a known concrete type with a matching "
                        + "override; an indirect-call backend is out of scope)."
                );
            }
        }

        var callee =
            _ctx.EnsureLowered(def)
            ?? throw new CilNotSupportedException(
                $"cannot lower instance method '{def.FullName}'."
            );
        var allArgs = new IrValue[argCount + 1];
        allArgs[0] = CoerceStore(thisValue, callee.Parameters[0].Type);
        for (var i = 0; i < argCount; i++)
            allArgs[i + 1] = PrepareArg(args[i], def.Parameters, i, callee.Parameters[i + 1].Type);
        var call = _b.Call(callee, allArgs);
        if (callee.ReturnType.Kind != IrTypeKind.Void)
        {
            var result = WidenToStack(call, IsSignedReturn(def));
            if (_ctx.GetConcreteReturnType(def) is { } resultConcreteType)
                _pendingConcreteType[result] = resultConcreteType;
            stack.Add(result);
        }
    }
}
