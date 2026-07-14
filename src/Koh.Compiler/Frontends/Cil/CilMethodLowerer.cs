using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Diagnostics;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Raised internally by <see cref="CilTypeMapper"/>/<see cref="CilMethodLowerer"/> for a construct
/// phase 1 doesn't cover (an unsupported CIL opcode, type, or call shape). Always caught within this
/// namespace and converted to a <see cref="Diagnostic"/> — mirrors
/// <c>Koh.Compiler.Frontends.CSharp.CSharpNotSupportedException</c>'s per-method containment, so one
/// bad method reports a diagnostic and is skipped rather than sinking the whole module.
/// </summary>
internal sealed class CilNotSupportedException(string message) : Exception(message);

/// <summary>Maps a Cecil <see cref="TypeReference"/> to a Koh IR type plus its signedness (signedness
/// is not a property of <see cref="IrType"/> itself — see <c>IrType</c>'s remarks — but the CIL frontend
/// needs it once, at the point a narrow local/argument/return is widened onto the simulated evaluation
/// stack, or narrowed back off it).</summary>
internal static class CilTypeMapper
{
    public static (IrType Type, bool Signed) Map(TypeReference typeReference)
    {
        if (typeReference is PointerType pointerType)
        {
            var (element, _) = Map(pointerType.ElementType);
            return (IrType.Pointer(element), false);
        }

        // A reference type (class, delegate, interface) — every instance is heap-allocated and
        // referred to by a raw byte pointer, exactly like CSharpFrontend's class instances (see
        // CSharpFrontend.HeapPointerName / MethodLowerer.LowerNew). `System.Void`'s own IsValueType
        // is not reliable across Cecil readers, so it is excluded explicitly and handled by the
        // switch below rather than by this shortcut.
        if (typeReference.MetadataType != MetadataType.Void && !typeReference.IsValueType)
            return (IrType.Pointer(IrType.I8), false);

        return typeReference.MetadataType switch
        {
            MetadataType.Void => (IrType.Void, false),
            MetadataType.Boolean => (IrType.I8, false),
            MetadataType.SByte => (IrType.I8, true),
            MetadataType.Byte => (IrType.I8, false),
            MetadataType.Int16 => (IrType.I16, true),
            MetadataType.UInt16 => (IrType.I16, false),
            MetadataType.Char => (IrType.I16, false),
            MetadataType.Int32 => (IrType.I32, true),
            MetadataType.UInt32 => (IrType.I32, false),
            MetadataType.Int64 => (IrType.I64, true),
            MetadataType.UInt64 => (IrType.I64, false),
            _ => throw new CilNotSupportedException(
                $"unsupported CIL type '{typeReference.FullName}' (phase 1 supports byte/sbyte/"
                    + "short/ushort/char/int/uint/long/ulong/bool/void and pointers only)."
            ),
        };
    }
}

/// <summary>
/// Lowers every eligible static method of a game module's own assembly to Koh IR (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>, phase 1, and the delegates/closures
/// task). Entry-point selection follows <see cref="ModuleDefinition.EntryPoint"/>, falling back to a
/// single static <c>Main</c>. A compiler-generated type's own members (a lambda body, a display-class
/// ctor/field-holder, a no-capture cache singleton's method) are never swept eagerly — only lowered
/// on demand, the first time a resolved call site references them (see
/// <see cref="CilLoweringContext.EnsureLowered"/>), so a construct this frontend can't yet devirtualize
/// stays a diagnostic at its call site rather than a spurious failure on an unrelated dead member.
/// </summary>
internal static class CilModuleLowerer
{
    public static void Lower(
        ModuleDefinition cecilModule,
        IrModule module,
        DiagnosticBag diagnostics
    )
    {
        var intrinsics = CilIntrinsicIndex.Build(cecilModule);
        var ctx = new CilLoweringContext(module, diagnostics, intrinsics);
        var methods = new List<MethodDefinition>();

        foreach (var type in cecilModule.GetTypes())
        {
            if (type.Name == "<Module>")
                continue;
            foreach (var method in type.Methods)
            {
                // Static, has-a-body methods only, eagerly. Constructors (.ctor/.cctor — a static
                // field initializer emits a .cctor that is IsStatic && HasBody) are excluded here:
                // static-field initialization is out of scope, and an INSTANCE .ctor is reached (when
                // needed at all) via a resolved `newobj`, on demand.
                if (!method.IsStatic || !method.HasBody || method.IsConstructor)
                    continue;
                methods.Add(method);
            }
        }

        // Pass 1: signatures, so calls resolve regardless of declaration order (mirrors
        // CSharpFrontend). A per-method failure here (an unsupported parameter/return type) reports a
        // diagnostic and leaves that method out of FunctionsByMethod — its body pass is skipped below.
        foreach (var method in methods)
            ctx.EnsureSignature(method);

        var entryMethod = ResolveEntryPoint(cecilModule, methods, diagnostics, module.Name);

        // A non-delegate `newobj` anywhere in the assembly (today: only a display-class capture
        // allocation) needs the shared bump-pointer heap global, seeded at the entry's prologue —
        // exactly CSharpFrontend's Pass-0.6 gate (CSharpFrontend.cs UsesHeap/ObjectCreationExpression
        // check), done here on raw IL instead of syntax since this frontend has no syntax tree.
        if (NeedsHeap(cecilModule))
            ctx.EnsureHeapGlobal();

        // Pass 2: bodies. Report per-method so one bad method doesn't sink the whole compile.
        foreach (var method in methods)
        {
            if (!ctx.FunctionsByMethod.TryGetValue(method, out var fn))
                continue;
            ctx.LowerBody(method, fn, isEntry: ReferenceEquals(method, entryMethod));
        }

        // Mark the entry authoritatively (the backend boots into it by flag, not by name-matching) —
        // only once its body actually lowered (FunctionsByMethod still holds it even on a body
        // failure; IsEntry on a diagnosed-broken function is harmless since compilation already failed).
        if (
            entryMethod is not null
            && ctx.FunctionsByMethod.TryGetValue(entryMethod, out var entryFn)
        )
            entryFn.IsEntry = true;
    }

    /// <summary>True if any method body in the assembly (hand-written or compiler-generated —
    /// a display-class ctor lives on the generated type, not the hand-written one) constructs a
    /// non-delegate reference type. A delegate's own <c>newobj</c> (<c>Func`2::.ctor(object, native
    /// int)</c>) is intercepted, never allocated (see <c>CilMethodLowerer.LowerNewobj</c>), so it must
    /// not itself trigger the heap.</summary>
    private static bool NeedsHeap(ModuleDefinition cecilModule)
    {
        foreach (var type in cecilModule.GetTypes())
        {
            if (type.Name == "<Module>")
                continue;
            foreach (var method in type.Methods)
            {
                // A static constructor (`.cctor`) is never lowered by this frontend (excluded from
                // the eager sweep the same as any other constructor, and never reached on demand — a
                // no-capture lambda's cache idiom is intercepted entirely, so the `<>c` singleton
                // `.cctor` that would `newobj <>c::.ctor()` to build it is dead code from this
                // frontend's perspective) — skip it so it can't false-positive NeedsHeap.
                if (!method.HasBody || (method.IsConstructor && method.IsStatic))
                    continue;
                foreach (var instr in method.Body.Instructions)
                {
                    // A 'newarr' (see CilMethodLowerer.Arrays.cs) always needs the heap — there is no
                    // delegate-construction-style interception for it the way Newobj has.
                    if (instr.OpCode.Code == Code.Newarr)
                        return true;
                    if (instr.OpCode.Code != Code.Newobj)
                        continue;
                    var ctorRef = (MethodReference)instr.Operand;
                    var declaringType = ResolveSafe(ctorRef.DeclaringType);
                    if (declaringType is not null && !IsDelegateType(declaringType))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>A delegate type's (<c>Func`2</c>, a user-declared <c>delegate</c>, …) direct base is
    /// always exactly <c>System.MulticastDelegate</c> (never <c>System.Delegate</c> for a real,
    /// instantiable delegate type — that is MulticastDelegate's own base, one level further up).</summary>
    internal static bool IsDelegateType(TypeDefinition type) =>
        type.BaseType?.FullName == "System.MulticastDelegate";

    internal static TypeDefinition? ResolveSafe(TypeReference typeReference)
    {
        try
        {
            return typeReference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    /// <summary><see cref="ModuleDefinition.EntryPoint"/> when the assembly declares one (an EXE-style
    /// build); otherwise the single static <c>Main</c> among the module's own eligible methods. More
    /// than one is a diagnostic — which one the backend would boot into would otherwise be a silent,
    /// order-dependent guess.</summary>
    private static MethodDefinition? ResolveEntryPoint(
        ModuleDefinition cecilModule,
        List<MethodDefinition> methods,
        DiagnosticBag diagnostics,
        string moduleName
    )
    {
        if (cecilModule.EntryPoint is { } declared)
            return declared;

        var mains = methods.Where(m => m.Name == "Main").ToList();
        if (mains.Count == 0)
        {
            diagnostics.Report(
                default,
                "the 'cil' frontend found no entry point (no assembly entry point and no static "
                    + "'Main' method).",
                DiagnosticSeverity.Error,
                moduleName
            );
            return null;
        }
        if (mains.Count > 1)
        {
            diagnostics.Report(
                default,
                "multiple static 'Main' methods found ("
                    + string.Join(", ", mains.Select(m => $"{m.DeclaringType.FullName}.{m.Name}"))
                    + "); the entry point is ambiguous.",
                DiagnosticSeverity.Error,
                moduleName
            );
            return null;
        }
        return mains[0];
    }
}

/// <summary>
/// Lowers one method body: splits it into basic blocks at branch targets and after branches, then
/// simulates the CIL evaluation stack at compile time within each block, mapping locals/arguments to
/// <c>alloca</c>s exactly like <c>CSharpFrontend</c>'s <c>MethodLowerer</c>. Produces no phis —
/// <c>Mem2RegPass</c> (default-on in <see cref="CompilerDriver"/>) does SSA construction.
///
/// Stack-typing discipline follows the real CLR evaluation stack (ECMA-335 III.1.1): the stack only
/// ever holds int32/int64/native-int/float/pointer values — never an 8/16-bit type. So every local or
/// argument read narrower than 32 bits is widened right there (sign- or zero-extended per its declared
/// signedness), arithmetic always operates on same-width operands as a result, and <c>conv.i1/u1/i2/u2</c>
/// truncate then re-widen to 32 bits, exactly mirroring what those opcodes do on real hardware. The
/// redundant zext/trunc pairs this produces are expected — cleaning them up is the (out-of-phase-1)
/// narrowing pass's job, not this lowerer's.
///
/// A block's entry stack is usually empty (Roslyn IL clears the stack at nearly every block boundary).
/// Where a forward branch delivers a non-empty stack (<c>?:</c>, <c>&amp;&amp;</c>, <c>||</c> — see
/// <see cref="Deliver"/>), the pending value(s) are spilled to a dedicated <c>alloca</c> created in the
/// function's entry block (so it dominates every other block regardless of which predecessor reaches
/// the join first) and reloaded at the top of the target block. Because Roslyn never leaves a value on
/// the stack across a backward branch, blocks are processed in program order and a block's entry depth
/// is always known — via a prior forward delivery, or implicitly zero — by the time it's reached.
/// </summary>
internal sealed partial class CilMethodLowerer
{
    private readonly MethodDefinition _method;
    private readonly IrFunction _function;
    private readonly CilLoweringContext _ctx;
    private readonly bool _isEntry;

    private readonly IrBuilder _b = new();
    private readonly Dictionary<
        VariableDefinition,
        (AllocaInstruction Alloca, IrType Type, bool Signed)
    > _locals = new();
    private readonly Dictionary<
        ParameterDefinition,
        (AllocaInstruction Alloca, IrType Type, bool Signed)
    > _params = new();

    // Every basic-block leader (jump target, fallthrough-after-a-branch, or the first instruction) maps
    // to the IrBasicBlock it starts. Built once, up front, so a forward branch can always resolve its
    // target block.
    private readonly Dictionary<Instruction, IrBasicBlock> _blockOf = new();

    // Spill allocas for a block reached with a non-empty CIL stack (see class remarks). Created lazily,
    // in the function's entry block, the first time some predecessor delivers to that target.
    private readonly Dictionary<IrBasicBlock, List<AllocaInstruction>> _spillSlots = new();

    private IrBasicBlock _entryBlock = null!;

    public CilMethodLowerer(
        MethodDefinition method,
        IrFunction function,
        CilLoweringContext ctx,
        bool isEntry
    )
    {
        _method = method;
        _function = function;
        _ctx = ctx;
        _isEntry = isEntry;
    }

    /// <summary>The parameter/local slot for CIL argument index <paramref name="index"/>, accounting
    /// for the implicit <c>this</c> the real CLR evaluation stack (and <c>ldarg.0..3</c>'s macro
    /// encoding) always counts as argument 0 on an instance method — <see cref="MethodDefinition.Parameters"/>
    /// itself never includes it.</summary>
    private ParameterDefinition ArgAt(int index) =>
        _method.HasThis
            ? index == 0
                ? _method.Body.ThisParameter
                : _method.Parameters[index - 1]
            : _method.Parameters[index];

    public void Run()
    {
        var body = _method.Body;
        var instructions = body.Instructions;
        if (instructions.Count == 0)
            throw new CilNotSupportedException(
                $"method '{_method.FullName}' has an empty body (unsupported)."
            );

        DetectDelegateCacheIdioms();
        PrepareExceptionHandlers();

        // Every leader gets its own block, created up front and in program order — the first leader
        // (instructions[0]) becomes the entry block, so parameter/local allocas (added to it next) sit
        // before any of the method's own translated instructions.
        foreach (var instr in ComputeLeaders(instructions))
            _blockOf[instr] = _function.AppendBlock();
        _entryBlock = _blockOf[instructions[0]];

        _b.PositionAtEnd(_entryBlock);

        var paramIndex = 0;
        if (_method.HasThis)
        {
            var thisParam = _method.Body.ThisParameter;
            var (thisType, _) = CilTypeMapper.Map(_method.DeclaringType);
            var thisAlloca = _b.Alloca(thisType);
            _b.Store(_function.Parameters[paramIndex], thisAlloca);
            _params[thisParam] = (thisAlloca, thisType, false);
            paramIndex++;
        }
        for (var i = 0; i < _method.Parameters.Count; i++, paramIndex++)
        {
            var p = _method.Parameters[i];
            var (irType, signed) = CilTypeMapper.Map(p.ParameterType);
            var alloca = _b.Alloca(irType);
            _b.Store(_function.Parameters[paramIndex], alloca);
            _params[p] = (alloca, irType, signed);
        }

        if (body.HasVariables)
        {
            foreach (var v in body.Variables)
            {
                var (irType, signed) = CilTypeMapper.Map(v.VariableType);
                var alloca = _b.Alloca(irType);
                _locals[v] = (alloca, irType, signed);
            }
        }

        // The entry function alone seeds the shared heap pointer (mirrors CSharpFrontend's
        // MethodLowerer, which does the same for its own `_staticInits` — see CilLoweringContext's
        // remarks). A no-op when the assembly never allocates (HeapGlobal stays null).
        if (_isEntry && _ctx.HeapGlobal is { } heap)
            _b.Store(
                IrBuilder.ConstInt(IrType.I16, CilLoweringContext.HeapTop),
                IrBuilder.GlobalRef(heap)
            );

        var stack = new List<IrValue>();

        foreach (var instr in instructions)
        {
            // A 'finally' handler's own instruction range is never part of the normal instruction
            // stream — it is only ever reached via a cloned copy emitted at each 'leave' (see
            // CilMethodLowerer.Iterators.cs's LowerFinallyBody).
            if (_finallyHandlerInstructions.Contains(instr))
                continue;

            // Always resolve "where are we" from the builder itself, never a separately tracked
            // variable — a helper Simulate calls into (EmitZeroFill/EmitZeroFillDynamic,
            // LowerFinallyBody's own clone) can reposition the builder to an ad-hoc block it created
            // (e.g. a zero-fill loop's "done" block) and leave it positioned there for the caller to
            // keep appending to; a stale local snapshot of "the current block" would then rewind to the
            // wrong (empty, since-abandoned) block on the next leader transition, orphaning whatever was
            // actually appended to the ad-hoc block (verified by a real bug this fixed: an iterator's
            // dead-at-runtime-but-still-compiled 'newobj' branch inside GetEnumerator).
            if (
                _blockOf.TryGetValue(instr, out var leaderBlock)
                && !ReferenceEquals(leaderBlock, _b.CurrentBlock)
            )
            {
                if (_b.CurrentBlock.Terminator is null)
                {
                    // Fell through a leader boundary without an explicit branch/ret (e.g. a block that
                    // is someone else's jump target but otherwise just runs into the next instruction).
                    Deliver(stack, leaderBlock);
                    _b.Br(leaderBlock);
                }
                _b.PositionAtEnd(leaderBlock);
                stack = EntryStack(leaderBlock);
            }

            Simulate(instr, stack);
        }
    }

    /// <summary>Leader instructions: the first instruction, every branch target, and the instruction
    /// immediately after any branch or <c>ret</c> (a block boundary even with no label pointing at it —
    /// the next byte is reachable only by falling through, or not at all, but either way it starts a
    /// fresh block per <see cref="Run"/>'s "implicit fallthrough" handling). A branch instruction that
    /// is part of a recognized delegate-cache idiom (<see cref="DetectDelegateCacheIdioms"/>) is
    /// excluded from leader contribution — that guard is resolved entirely at compile time (see
    /// <see cref="_suppressed"/>'s remarks), so it must not fork the control-flow graph.</summary>
    private IEnumerable<Instruction> ComputeLeaders(
        Mono.Collections.Generic.Collection<Instruction> instructions
    )
    {
        var leaders = new HashSet<Instruction>();
        if (instructions.Count > 0)
            leaders.Add(instructions[0]);

        foreach (var instr in instructions)
        {
            // A 'finally' handler's own instructions are never leaders of the normal block graph — see
            // CilMethodLowerer.Iterators.cs's remarks (they're only ever reached via a clone).
            if (_finallyHandlerInstructions.Contains(instr))
                continue;
            if (_suppressed.Contains(instr) || !IsBranchOrReturn(instr.OpCode.Code))
                continue;
            foreach (var target in BranchTargets(instr))
                if (!_finallyHandlerInstructions.Contains(target))
                    leaders.Add(target);
            if (instr.Next is not null && !_finallyHandlerInstructions.Contains(instr.Next))
                leaders.Add(instr.Next);
        }

        // Preserve program order (the caller relies on it to create blocks front-to-back). Defensively
        // exclude handler-range instructions from the returned set too, even though nothing above
        // should have added one.
        return instructions.Where(i =>
            leaders.Contains(i) && !_finallyHandlerInstructions.Contains(i)
        );
    }

    private static bool IsBranchOrReturn(Code code) =>
        code
            is Code.Ret
                or Code.Br
                or Code.Br_S
                or Code.Brtrue
                or Code.Brtrue_S
                or Code.Brfalse
                or Code.Brfalse_S
                or Code.Beq
                or Code.Beq_S
                or Code.Bne_Un
                or Code.Bne_Un_S
                or Code.Blt
                or Code.Blt_S
                or Code.Blt_Un
                or Code.Blt_Un_S
                or Code.Ble
                or Code.Ble_S
                or Code.Ble_Un
                or Code.Ble_Un_S
                or Code.Bgt
                or Code.Bgt_S
                or Code.Bgt_Un
                or Code.Bgt_Un_S
                or Code.Bge
                or Code.Bge_S
                or Code.Bge_Un
                or Code.Bge_Un_S
                or Code.Leave
                or Code.Leave_S;

    private static IEnumerable<Instruction> BranchTargets(Instruction instr) =>
        instr.OpCode.Code == Code.Ret ? [] : [(Instruction)instr.Operand];

    /// <summary>The stack an already-positioned block starts with: reloads from its spill allocas
    /// (see <see cref="Deliver"/>) if any predecessor already delivered a non-empty stack to it, else
    /// empty — the overwhelmingly common case.</summary>
    private List<IrValue> EntryStack(IrBasicBlock block)
    {
        if (!_spillSlots.TryGetValue(block, out var slots))
            return [];
        var stack = new List<IrValue>(slots.Count);
        foreach (var slot in slots)
            stack.Add(_b.Load(slot));
        return stack;
    }

    /// <summary>Spill whatever remains on the simulated stack across a control-flow edge into
    /// <paramref name="target"/>'s spill allocas, creating them (in the entry block, so they dominate
    /// every block) the first time <paramref name="target"/> is reached with a non-empty stack. A no-op
    /// when <paramref name="remainder"/> is empty — the common case pays nothing.</summary>
    private void Deliver(List<IrValue> remainder, IrBasicBlock target)
    {
        if (remainder.Count == 0)
            return;

        if (!_spillSlots.TryGetValue(target, out var slots))
        {
            slots = [];
            foreach (var v in remainder)
            {
                var alloca = new AllocaInstruction(v.Type) { Parent = _entryBlock };
                _entryBlock.Instructions.Insert(0, alloca);
                slots.Add(alloca);
            }
            _spillSlots[target] = slots;
        }

        for (var i = 0; i < remainder.Count; i++)
            _b.Store(CoerceStore(remainder[i], slots[i].Allocated), slots[i]);
    }

    private static IrValue Pop(List<IrValue> stack)
    {
        var v = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return v;
    }

    /// <summary>Widen a value narrower than 32 bits up to i32, per real CLR stack typing (see class
    /// remarks) — a no-op for anything already 32-bit-or-wider (including pointers).</summary>
    private IrValue WidenToStack(IrValue raw, bool signed)
    {
        if (raw.Type.Kind != IrTypeKind.Int || raw.Type.Bits >= 32)
            return raw;
        return _b.Conv(signed ? IrConvOp.SExt : IrConvOp.ZExt, raw, IrType.I32);
    }

    /// <summary>Narrow (or, best-effort, widen) a stack value down to a storage/return target type —
    /// the mirror of <see cref="WidenToStack"/>, used by stores back to a local/argument/return slot.</summary>
    private IrValue CoerceStore(IrValue value, IrType target)
    {
        if (value.Type.StructurallyEquals(target))
            return value;
        if (value.Type.Kind == IrTypeKind.Int && target.Kind == IrTypeKind.Int)
        {
            if (value.Type.Bits > target.Bits)
                return _b.Conv(IrConvOp.Trunc, value, target);
            if (value.Type.Bits < target.Bits)
                return _b.Conv(IrConvOp.ZExt, value, target);
        }
        return value;
    }

    /// <summary>A raw pointer can't feed <c>icmp</c> (<see cref="IrVerifier"/> requires integer
    /// operands); reinterpret it as an address-width integer first. A no-op for anything already
    /// integer-typed.</summary>
    private IrValue AsComparable(IrValue v) =>
        v.Type.Kind == IrTypeKind.Pointer
            ? _b.Conv(IrConvOp.Bitcast, v, IrType.Int(v.Type.SizeInBits))
            : v;

    private IrValue LoadLocal(VariableDefinition v)
    {
        var (alloca, _, signed) = _locals[v];
        var loaded = WidenToStack(_b.Load(alloca), signed);
        // Carry a resolved delegate's target method forward across the load — see
        // CilMethodLowerer.Delegates.cs's remarks on _pendingDelegateProvenance/_localDelegateTarget.
        if (_localDelegateTarget.TryGetValue(v, out var target))
            _pendingDelegateProvenance[loaded] = target;
        // Same round-trip for a 'newarr'-allocated array's element type/signedness/count — see
        // CilMethodLowerer.Arrays.cs's remarks on _pendingArrayInfo/_localArrayInfo.
        if (_localArrayInfo.TryGetValue(v, out var arrayInfo))
            _pendingArrayInfo[loaded] = arrayInfo;
        if (_localConcreteType.TryGetValue(v, out var concreteType))
            _pendingConcreteType[loaded] = concreteType;
        return loaded;
    }

    private void StoreLocal(VariableDefinition v, IrValue value)
    {
        var (alloca, type, _) = _locals[v];
        if (_pendingDelegateProvenance.TryGetValue(value, out var target))
            _localDelegateTarget[v] = target;
        else
            _localDelegateTarget.Remove(v);
        if (_pendingArrayInfo.TryGetValue(value, out var arrayInfo))
            _localArrayInfo[v] = arrayInfo;
        else
            _localArrayInfo.Remove(v);
        // Same round-trip for a Newobj/known-return's concrete type — see
        // CilMethodLowerer.Iterators.cs's remarks on _pendingConcreteType/_localConcreteType. NOTE:
        // like the two tables above, this is a simple last-write-wins side table with no phi-aware
        // merge across predecessors — sound only because every join this frontend actually lowers (the
        // iterator kickoff/GetEnumerator reuse-vs-fresh shapes) has every predecessor agree on the
        // concrete type; a real divergence would silently lose provenance (falling back to the
        // ordinary sealed/final devirtualization rule, not a miscompile) rather than being asserted.
        if (_pendingConcreteType.TryGetValue(value, out var concreteType))
            _localConcreteType[v] = concreteType;
        else
            _localConcreteType.Remove(v);
        _b.Store(CoerceStore(value, type), alloca);
    }

    private IrValue LoadArg(ParameterDefinition p)
    {
        var (alloca, _, signed) = _params[p];
        var loaded = WidenToStack(_b.Load(alloca), signed);
        // A sealed declaring type's own 'this' is always exactly that type (no subclass can exist to
        // make it otherwise) — seeds concrete-type devirtualization for an iterator state machine's own
        // instance methods (GetEnumerator/MoveNext/Dispose/get_Current all read 'this' this way). Not
        // safe to assume for a non-sealed declaring type (a derived instance could be calling through
        // a base method), so restricted to sealed — see CilMethodLowerer.Iterators.cs's remarks.
        if (
            _method.HasThis
            && ReferenceEquals(p, _method.Body.ThisParameter)
            && _method.DeclaringType.IsSealed
        )
            _pendingConcreteType[loaded] = _method.DeclaringType;
        return loaded;
    }

    private void StoreArg(ParameterDefinition p, IrValue value)
    {
        var (alloca, type, _) = _params[p];
        _b.Store(CoerceStore(value, type), alloca);
    }

    private void Simulate(Instruction instr, List<IrValue> stack)
    {
        // A delegate-cache idiom's interior instructions (see DetectDelegateCacheIdioms) resolve
        // entirely at compile time and emit nothing at runtime — every one of them, including its
        // starting `ldsfld`, is either suppressed outright or replaced below.
        if (_suppressed.Contains(instr))
            return;
        if (_ldsfldDelegateProvenance.TryGetValue(instr, out var cachedTarget))
        {
            var env = IrBuilder.ConstInt(IrType.Pointer(IrType.I8), 0);
            _pendingDelegateProvenance[env] = cachedTarget;
            stack.Add(env);
            return;
        }

        var code = instr.OpCode.Code;
        switch (code)
        {
            case Code.Nop:
                break;

            // ---- Constants ------------------------------------------------------------------
            case Code.Ldc_I4_M1:
                stack.Add(IrBuilder.ConstInt(IrType.I32, -1));
                break;
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_2:
            case Code.Ldc_I4_3:
            case Code.Ldc_I4_4:
            case Code.Ldc_I4_5:
            case Code.Ldc_I4_6:
            case Code.Ldc_I4_7:
            case Code.Ldc_I4_8:
                stack.Add(IrBuilder.ConstInt(IrType.I32, code - Code.Ldc_I4_0));
                break;
            case Code.Ldc_I4_S:
                stack.Add(IrBuilder.ConstInt(IrType.I32, (sbyte)instr.Operand));
                break;
            case Code.Ldc_I4:
                stack.Add(IrBuilder.ConstInt(IrType.I32, (int)instr.Operand));
                break;

            // ---- Locals / arguments -----------------------------------------------------------
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
            case Code.Ldloca_S:
            case Code.Ldloca:
                stack.Add(_locals[(VariableDefinition)instr.Operand].Alloca);
                break;
            case Code.Stloc_0:
                StoreLocal(_method.Body.Variables[0], Pop(stack));
                break;
            case Code.Stloc_1:
                StoreLocal(_method.Body.Variables[1], Pop(stack));
                break;
            case Code.Stloc_2:
                StoreLocal(_method.Body.Variables[2], Pop(stack));
                break;
            case Code.Stloc_3:
                StoreLocal(_method.Body.Variables[3], Pop(stack));
                break;
            case Code.Stloc_S:
            case Code.Stloc:
                StoreLocal((VariableDefinition)instr.Operand, Pop(stack));
                break;
            case Code.Ldarg_0:
                stack.Add(LoadArg(ArgAt(0)));
                break;
            case Code.Ldarg_1:
                stack.Add(LoadArg(ArgAt(1)));
                break;
            case Code.Ldarg_2:
                stack.Add(LoadArg(ArgAt(2)));
                break;
            case Code.Ldarg_3:
                stack.Add(LoadArg(ArgAt(3)));
                break;
            case Code.Ldarg_S:
            case Code.Ldarg:
                stack.Add(LoadArg((ParameterDefinition)instr.Operand));
                break;
            case Code.Starg_S:
            case Code.Starg:
                StoreArg((ParameterDefinition)instr.Operand, Pop(stack));
                break;

            case Code.Dup:
                stack.Add(stack[^1]);
                break;
            case Code.Pop:
                Pop(stack);
                break;

            // ---- Arithmetic / logic ------------------------------------------------------------
            case Code.Add:
                BinaryOp(stack, IrBinaryOp.Add);
                break;
            case Code.Sub:
                BinaryOp(stack, IrBinaryOp.Sub);
                break;
            case Code.Mul:
                BinaryOp(stack, IrBinaryOp.Mul);
                break;
            case Code.And:
                BinaryOp(stack, IrBinaryOp.And);
                break;
            case Code.Or:
                BinaryOp(stack, IrBinaryOp.Or);
                break;
            case Code.Xor:
                BinaryOp(stack, IrBinaryOp.Xor);
                break;
            case Code.Shl:
                ShiftOp(stack, IrBinaryOp.Shl);
                break;
            case Code.Shr:
                ShiftOp(stack, IrBinaryOp.AShr);
                break;
            case Code.Shr_Un:
                ShiftOp(stack, IrBinaryOp.LShr);
                break;
            case Code.Neg:
            {
                var a = Pop(stack);
                stack.Add(_b.Sub(IrBuilder.ConstInt(a.Type, 0), a));
                break;
            }
            case Code.Not:
            {
                var a = Pop(stack);
                stack.Add(_b.Binary(IrBinaryOp.Xor, a, IrBuilder.ConstInt(a.Type, -1)));
                break;
            }

            case Code.Ceq:
                CompareOp(stack, IrCompareOp.Eq);
                break;
            case Code.Clt:
                CompareOp(stack, IrCompareOp.Slt);
                break;
            case Code.Clt_Un:
                CompareOp(stack, IrCompareOp.Ult);
                break;
            case Code.Cgt:
                CompareOp(stack, IrCompareOp.Sgt);
                break;
            case Code.Cgt_Un:
                CompareOp(stack, IrCompareOp.Ugt);
                break;

            // ---- Conversions ---------------------------------------------------------------------
            // conv.i1/u1/i2/u2 narrow then re-widen to i32 (real CLR semantics — see class remarks).
            case Code.Conv_I1:
                stack.Add(
                    _b.Conv(
                        IrConvOp.SExt,
                        _b.Conv(IrConvOp.Trunc, Pop(stack), IrType.I8),
                        IrType.I32
                    )
                );
                break;
            case Code.Conv_U1:
                stack.Add(
                    _b.Conv(
                        IrConvOp.ZExt,
                        _b.Conv(IrConvOp.Trunc, Pop(stack), IrType.I8),
                        IrType.I32
                    )
                );
                break;
            case Code.Conv_I2:
                stack.Add(
                    _b.Conv(
                        IrConvOp.SExt,
                        _b.Conv(IrConvOp.Trunc, Pop(stack), IrType.I16),
                        IrType.I32
                    )
                );
                break;
            case Code.Conv_U2:
                stack.Add(
                    _b.Conv(
                        IrConvOp.ZExt,
                        _b.Conv(IrConvOp.Trunc, Pop(stack), IrType.I16),
                        IrType.I32
                    )
                );
                break;
            case Code.Conv_I4:
            case Code.Conv_U4:
            {
                var v = Pop(stack);
                stack.Add(v.Type.Bits > 32 ? _b.Conv(IrConvOp.Trunc, v, IrType.I32) : v);
                break;
            }

            // ---- Control flow ---------------------------------------------------------------------
            case Code.Br:
            case Code.Br_S:
            {
                var target = _blockOf[(Instruction)instr.Operand];
                Deliver(stack, target);
                _b.Br(target);
                break;
            }
            case Code.Brtrue:
            case Code.Brtrue_S:
            case Code.Brfalse:
            case Code.Brfalse_S:
            {
                var v = AsComparable(Pop(stack));
                var isTrue = code is Code.Brtrue or Code.Brtrue_S;
                var cmp = _b.Compare(
                    isTrue ? IrCompareOp.Ne : IrCompareOp.Eq,
                    v,
                    IrBuilder.ConstInt(v.Type, 0)
                );
                var target = _blockOf[(Instruction)instr.Operand];
                var fallThrough = _blockOf[instr.Next!];
                Deliver(stack, target);
                Deliver(stack, fallThrough);
                _b.CondBr(cmp, target, fallThrough);
                break;
            }
            case Code.Beq:
            case Code.Beq_S:
            case Code.Bne_Un:
            case Code.Bne_Un_S:
            case Code.Blt:
            case Code.Blt_S:
            case Code.Blt_Un:
            case Code.Blt_Un_S:
            case Code.Ble:
            case Code.Ble_S:
            case Code.Ble_Un:
            case Code.Ble_Un_S:
            case Code.Bgt:
            case Code.Bgt_S:
            case Code.Bgt_Un:
            case Code.Bgt_Un_S:
            case Code.Bge:
            case Code.Bge_S:
            case Code.Bge_Un:
            case Code.Bge_Un_S:
            {
                var b = AsComparable(Pop(stack));
                var a = AsComparable(Pop(stack));
                if (!a.Type.StructurallyEquals(b.Type))
                    b = CoerceStore(b, a.Type);
                var predicate = code switch
                {
                    Code.Beq or Code.Beq_S => IrCompareOp.Eq,
                    Code.Bne_Un or Code.Bne_Un_S => IrCompareOp.Ne,
                    Code.Blt or Code.Blt_S => IrCompareOp.Slt,
                    Code.Blt_Un or Code.Blt_Un_S => IrCompareOp.Ult,
                    Code.Ble or Code.Ble_S => IrCompareOp.Sle,
                    Code.Ble_Un or Code.Ble_Un_S => IrCompareOp.Ule,
                    Code.Bgt or Code.Bgt_S => IrCompareOp.Sgt,
                    Code.Bgt_Un or Code.Bgt_Un_S => IrCompareOp.Ugt,
                    Code.Bge or Code.Bge_S => IrCompareOp.Sge,
                    Code.Bge_Un or Code.Bge_Un_S => IrCompareOp.Uge,
                    _ => throw new InvalidOperationException(),
                };
                var cmp = _b.Compare(predicate, a, b);
                var target = _blockOf[(Instruction)instr.Operand];
                var fallThrough = _blockOf[instr.Next!];
                Deliver(stack, target);
                Deliver(stack, fallThrough);
                _b.CondBr(cmp, target, fallThrough);
                break;
            }

            case Code.Call:
                LowerCall((MethodReference)instr.Operand, stack);
                break;
            case Code.Callvirt:
                LowerCallvirt((MethodReference)instr.Operand, stack);
                break;
            case Code.Newobj:
                LowerNewobj((MethodReference)instr.Operand, stack);
                break;
            case Code.Ldftn:
            {
                var target =
                    ((MethodReference)instr.Operand).Resolve()
                    ?? throw new CilNotSupportedException(
                        $"cannot resolve 'ldftn' target in '{_method.FullName}'."
                    );
                // A placeholder value that is never itself emitted as real IR — see LowerNewobj, which
                // consumes it purely to look up the method it stands for in _ldftnProvenance.
                var placeholder = IrBuilder.ConstInt(IrType.I16, 0);
                _ldftnProvenance[placeholder] = target;
                stack.Add(placeholder);
                break;
            }
            case Code.Ldvirtftn:
            {
                // A method-group delegate off a virtual method (`Func<T> f = obj.Method;`): unlike
                // `ldftn`, `ldvirtftn`'s whole point is RUNTIME dispatch through the receiver it also
                // pops here (Roslyn's own emitted shape always `dup`s the env first, so the outer
                // delegate `newobj` still gets its copy — see LowerNewobj's remarks). The instruction's
                // own operand only names the vtable SLOT (e.g. `Base::Get`), not the receiver's actual
                // runtime override — binding that token statically would silently invoke the wrong
                // method whenever the receiver's concrete type differs from the token's declaring
                // type. Exactly the same guard LowerInstanceCall already applies to a `callvirt` that
                // can't be devirtualized: resolvable only when the target is non-virtual, final, or
                // its declaring type is sealed (task 3 adds concrete-type tracking to do better).
                Pop(stack);
                var target =
                    ((MethodReference)instr.Operand).Resolve()
                    ?? throw new CilNotSupportedException(
                        $"cannot resolve 'ldvirtftn' target in '{_method.FullName}'."
                    );
                if (target.IsVirtual && !target.IsFinal && !target.DeclaringType.IsSealed)
                    throw new CilNotSupportedException(
                        $"'ldvirtftn' on '{target.FullName}' in '{_method.FullName}' cannot be "
                            + "resolved to a single target (its vtable-slot operand only identifies "
                            + "the declared method, not the receiver's actual runtime override; "
                            + "devirtualization needs a sealed declaring type or a final method; an "
                            + "indirect-call backend is out of scope)."
                    );
                var placeholder = IrBuilder.ConstInt(IrType.I16, 0);
                _ldftnProvenance[placeholder] = target;
                stack.Add(placeholder);
                break;
            }
            case Code.Ldfld:
            {
                var fieldRef = (FieldReference)instr.Operand;
                var objRef = Pop(stack);
                var (ptr, fieldType, signed) = FieldPointer(fieldRef, objRef);
                stack.Add(WidenToStack(_b.Load(ptr), signed));
                break;
            }
            case Code.Ldflda:
            {
                var fieldRef = (FieldReference)instr.Operand;
                var objRef = Pop(stack);
                var (ptr, _, _) = FieldPointer(fieldRef, objRef);
                stack.Add(ptr);
                break;
            }
            case Code.Stfld:
            {
                var fieldRef = (FieldReference)instr.Operand;
                var value = Pop(stack);
                var objRef = Pop(stack);
                var (ptr, fieldType, _) = FieldPointer(fieldRef, objRef);
                _b.Store(CoerceStore(value, fieldType), ptr);
                break;
            }
            case Code.Ldnull:
                stack.Add(IrBuilder.ConstInt(IrType.Pointer(IrType.I8), 0));
                break;

            // ---- SZ-arrays (see CilMethodLowerer.Arrays.cs) ------------------------------------
            case Code.Newarr:
                LowerNewarr((TypeReference)instr.Operand, stack);
                break;
            case Code.Stelem_I1:
                StoreElem(stack, IrType.I8);
                break;
            case Code.Stelem_I2:
                StoreElem(stack, IrType.I16);
                break;
            case Code.Stelem_I4:
                StoreElem(stack, IrType.I32);
                break;
            case Code.Ldelem_I1:
                LoadElem(stack, IrType.I8, signed: true);
                break;
            case Code.Ldelem_U1:
                LoadElem(stack, IrType.I8, signed: false);
                break;
            case Code.Ldelem_I2:
                LoadElem(stack, IrType.I16, signed: true);
                break;
            case Code.Ldelem_U2:
                LoadElem(stack, IrType.I16, signed: false);
                break;
            case Code.Ldelem_I4:
                LoadElem(stack, IrType.I32, signed: true);
                break;
            case Code.Ldelem_U4:
                LoadElem(stack, IrType.I32, signed: false);
                break;

            // ---- try/finally (see CilMethodLowerer.Iterators.cs) ------------------------------
            case Code.Leave:
            case Code.Leave_S:
            {
                // 'leave' always empties the CIL evaluation stack (ECMA-335 III.3.36) — never a
                // Deliver here, unlike every other branch above.
                var targetBlock = _blockOf[(Instruction)instr.Operand];
                if (FindEnclosingFinally(instr) is { } handler)
                {
                    var finallyEntry = _function.AppendBlock("finally.entry");
                    _b.Br(finallyEntry);
                    _b.PositionAtEnd(finallyEntry);
                    LowerFinallyBody(handler, targetBlock);
                }
                else
                {
                    _b.Br(targetBlock);
                }
                break;
            }

            case Code.Ret:
                if (_function.ReturnType.Kind == IrTypeKind.Void)
                    _b.Ret();
                else
                {
                    var retValue = Pop(stack);
                    // A LINQ chain placeholder (see CilMethodLowerer.Linq.cs) escaping without a
                    // terminal is never valid IR to build (it's a never-materialized I16 constant, not a
                    // real value) — diagnose it here explicitly rather than letting it reach CoerceStore/
                    // IrVerifier as an opaque type-mismatch.
                    if (_pendingLinqPipeline.ContainsKey(retValue))
                        throw new CilNotSupportedException(
                            $"LINQ pipeline in '{_method.FullName}' is returned without a terminal "
                                + "operation (phase supports Where/Select pipelines ending in "
                                + "Sum/Count/Any/All only)."
                        );
                    // Record this ret site's concrete-type provenance (or its absence) for the callee-
                    // side devirtualization the design spike's iterator finding needs — see
                    // CilMethodLowerer.Iterators.cs's remarks on CilLoweringContext.RecordConcreteReturn.
                    _ctx.RecordConcreteReturn(
                        _method,
                        _pendingConcreteType.TryGetValue(retValue, out var retType) ? retType : null
                    );
                    _b.Ret(CoerceStore(retValue, _function.ReturnType));
                }
                break;

            default:
                throw new CilNotSupportedException(
                    $"unsupported CIL opcode '{instr.OpCode.Name}' in '{_method.FullName}' "
                        + "(phase 1 opcode subset)."
                );
        }
    }

    private void BinaryOp(List<IrValue> stack, IrBinaryOp op)
    {
        var b = Pop(stack);
        var a = Pop(stack);
        if (!a.Type.StructurallyEquals(b.Type))
            b = CoerceStore(b, a.Type);
        stack.Add(_b.Binary(op, a, b));
    }

    private void ShiftOp(List<IrValue> stack, IrBinaryOp op)
    {
        var count = Pop(stack);
        var value = Pop(stack);
        if (!count.Type.StructurallyEquals(value.Type))
            count = CoerceStore(count, value.Type);
        stack.Add(_b.Binary(op, value, count));
    }

    private void CompareOp(List<IrValue> stack, IrCompareOp op)
    {
        var b = AsComparable(Pop(stack));
        var a = AsComparable(Pop(stack));
        if (!a.Type.StructurallyEquals(b.Type))
            b = CoerceStore(b, a.Type);
        var cmp = _b.Compare(op, a, b);
        stack.Add(_b.Conv(IrConvOp.ZExt, cmp, IrType.I32));
    }

    /// <summary>A <c>call</c>: an instance call (always non-virtual by CIL definition — a base-ctor
    /// call, a <c>base.Method()</c> call, or a devirtualization-irrelevant direct instance call — see
    /// <see cref="LowerInstanceCall"/>), a <c>[KohIntrinsic]</c>-attributed method (Hardware/Gb — see
    /// <see cref="LowerIntrinsicCall"/>), a call to another static function of this same game module,
    /// or — out of scope — anything else, reported as a diagnostic rather than silently miscompiled.</summary>
    private void LowerCall(MethodReference calleeRef, List<IrValue> stack)
    {
        if (calleeRef.HasThis)
        {
            LowerInstanceCall(calleeRef, stack, isVirtualDispatch: false);
            return;
        }

        if (IsLinqEnumerableCall(calleeRef))
        {
            LowerLinqCall(calleeRef, stack);
            return;
        }

        // System.Environment::get_CurrentManagedThreadId() -> a constant. Not a hardware address (see
        // CLAUDE.md's no-hardcoded-hardware-addresses rule for this frontend) — a runtime-semantics
        // substitution for a single-threaded target, so a lowered iterator's GetEnumerator reuse-vs-
        // fresh-copy thread check becomes ordinary, foldable code (see
        // CilMethodLowerer.Iterators.cs's remarks). Intercepted by name before Resolve() — the BCL
        // method's own body (an internal call) is never something this frontend could lower anyway.
        if (
            !calleeRef.HasThis
            && calleeRef.DeclaringType.FullName == "System.Environment"
            && calleeRef.Name == "get_CurrentManagedThreadId"
        )
        {
            stack.Add(IrBuilder.ConstInt(IrType.I32, 1));
            return;
        }

        MethodDefinition? def;
        try
        {
            def = calleeRef.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            def = null;
        }
        if (def is null)
            throw new CilNotSupportedException($"cannot resolve call to '{calleeRef.FullName}'.");

        var argCount = calleeRef.Parameters.Count;
        var args = new IrValue[argCount];
        for (var i = argCount - 1; i >= 0; i--)
            args[i] = Pop(stack);

        if (_ctx.Intrinsics.TryGetValue(def, out var entry))
        {
            LowerIntrinsicCall(def, entry, args, stack);
            return;
        }

        if (_ctx.FunctionsByMethod.TryGetValue(def, out var callee))
        {
            // The stack's own widen-narrow-to-i32 discipline (see class remarks) means an argument
            // narrower than i32 arrives here already widened; narrow it back to the callee's declared
            // parameter type (e.g. i8 for a byte parameter) before the call, matching what stloc/starg
            // do for locals/arguments and what the real CLR calling convention truncates on entry.
            for (var i = 0; i < args.Length; i++)
                args[i] = CoerceStore(args[i], callee.Parameters[i].Type);
            var call = _b.Call(callee, args);
            if (callee.ReturnType.Kind != IrTypeKind.Void)
            {
                var result = WidenToStack(call, IsSignedReturn(def));
                // Thread this static call's callee-inferred concrete return type (see
                // CilLoweringContext.GetConcreteReturnType) onto the pushed result, so a subsequent
                // callvirt through an interface-typed result (the iterator kickoff's own shape) can
                // devirtualize — see CilMethodLowerer.Iterators.cs.
                if (_ctx.GetConcreteReturnType(def) is { } concreteType)
                    _pendingConcreteType[result] = concreteType;
                stack.Add(result);
            }
            return;
        }

        throw new CilNotSupportedException(
            $"call to unsupported external method '{def.FullName}' (phase 1: only "
                + "[KohIntrinsic] members and other game-module static methods are supported)."
        );
    }

    private static bool IsSignedReturn(MethodDefinition def) =>
        CilTypeMapper.Map(def.ReturnType).Signed;

    private void LowerIntrinsicCall(
        MethodDefinition def,
        CilIntrinsicIndex.Entry entry,
        IReadOnlyList<IrValue> args,
        List<IrValue> stack
    )
    {
        switch (entry.Kind)
        {
            case "register" when def.IsSetter:
                _b.Store(
                    CoerceStore(args[0], IrType.I8),
                    IrBuilder.GlobalRef(RegisterGlobal(entry.Address))
                );
                break;
            case "register" when def.IsGetter:
                stack.Add(
                    _b.Conv(
                        IrConvOp.ZExt,
                        _b.Load(IrBuilder.GlobalRef(RegisterGlobal(entry.Address))),
                        IrType.I32
                    )
                );
                break;
            case "region":
                stack.Add(IrBuilder.GlobalRef(RegionGlobal(entry.Address)));
                break;
            case "ei":
                _b.Intrinsic("ei");
                break;
            case "di":
                _b.Intrinsic("di");
                break;
            case "halt":
                _b.Intrinsic("halt");
                break;
            case "nop":
                _b.Intrinsic("nop");
                break;
            case "stop":
                _b.Intrinsic("stop");
                break;
            default:
                throw new CilNotSupportedException(
                    $"unsupported [KohIntrinsic] kind '{entry.Kind}' on '{def.FullName}' (phase 1 "
                        + "supports register/region/ei/di/halt/nop/stop only)."
                );
        }
    }

    private IrGlobal RegisterGlobal(int address) => _ctx.RegisterGlobal(address);

    private IrGlobal RegionGlobal(int address) => _ctx.RegionGlobal(address);
}
