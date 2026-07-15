using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>The single owner of one function's emit state: the code buffer, this function's
    /// WRAM allocation, and the byte-level load/store/copy helpers. Concern emitters hold one of these
    /// by reference (composition) rather than each re-deriving the state.</summary>
    internal sealed class EmitContext
    {
        public readonly Emitter E;

        public readonly IrFunction Fn;

        public readonly IReadOnlyDictionary<IrFunction, FunctionAllocation> Allocations;

        public readonly IReadOnlyDictionary<IrGlobal, int> Globals;

        public readonly Dictionary<IrValue, int> Slot;

        public readonly Dictionary<IrValue, Mir.Sm83Register> Register;

        public readonly Dictionary<IrValue, int> StaticAddr;

        public readonly int PhiTempBase;

        /// <summary>Layer-1 loop-induction residency: per preheader block, the one-time register loads
        /// <see cref="FunctionEmitter"/> must emit right before that block's (unconditional) branch to
        /// the loop header it precedes. See <see cref="FunctionAllocation.LoopInductionPreheaderSync"/>.</summary>
        public readonly Dictionary<
            IrBasicBlock,
            List<FunctionAllocation.LoopInductionSync>
        > LoopInductionPreheaderSync;

        /// <summary>Layer-2 pointer residency: see <see cref="FunctionAllocation.FusedPointerSite"/>.
        /// Consulted by <see cref="MemoryEmitter"/>'s <c>EmitLoad</c>/<c>EmitStore</c>.</summary>
        public readonly Dictionary<IrInstruction, Mir.Sm83Register> FusedPointerSite;

        /// <summary>Layer-2 pointer residency's preheader sync: see
        /// <see cref="FunctionAllocation.PointerHomePreheaderSync"/>. Consulted by
        /// <see cref="FunctionEmitter"/>'s <c>BrInstruction</c> dispatch, alongside
        /// <see cref="LoopInductionPreheaderSync"/>.</summary>
        public readonly Dictionary<
            IrBasicBlock,
            List<FunctionAllocation.PointerHomeSync>
        > PointerHomePreheaderSync;

        public readonly IReadOnlySet<IrFunction> Recursive;

        public readonly IReadOnlySet<IrFunction> Banked;

        public readonly bool IsEntry;

        public readonly int SoftStackBase;

        public readonly int FrameBase;

        public readonly int FrameSize;

        /// <summary>Byte size of the contiguous WRAM-globals region starting at <see cref="WramBase"/> —
        /// every module-scope static field/array's fixed storage. Zero unless this context is the entry
        /// function, which is the only one that needs it (to emit the boot-only zero-clear).</summary>
        public readonly int WramGlobalsSize;

        /// <summary>The OAM DMA source scratch global's WRAM address (see
        /// <see cref="Sm83Backend.OamDmaSourceAddress"/>), or -1 if the program never calls
        /// <c>Hardware.RunOamDma</c>. Only consulted when this context is the entry function (to emit
        /// the boot-only HRAM trampoline install, mirroring <see cref="WramGlobalsSize"/>'s own gating).</summary>
        public readonly int OamDmaSrcAddr;

        /// <summary>Dynamic <c>gep</c> instructions eligible to be computed directly at their single
        /// consuming load/store instead of through a WRAM slot round-trip (see
        /// <see cref="MemoryEmitter.LoadPointerToHL"/>). A naive per-instruction lowering emits every
        /// SSA value's result to its slot and reloads it at every use — correct, but on an address that
        /// is used exactly once, immediately, that round trip is pure waste: computing the address right
        /// before the load/store it feeds is exactly as correct and skips two WRAM round-trips (measured
        /// ~11% fewer dots/iteration on a tight byte-copy loop — see <see cref="ComputeFusedGeps"/> for
        /// the eligibility rule, why it must be "immediately next" and not just "later in the block," and
        /// why that is safe).</summary>
        public readonly HashSet<GetElementPtrInstruction> FusedGep;

        public EmitContext(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null,
            int wramGlobalsSize = 0,
            int oamDmaSrcAddr = -1
        )
        {
            E = emitter;
            Fn = fn;
            Allocations = allocations;
            Globals = globals;
            var allocation = allocations[fn];
            Slot = allocation.Slot;
            Register = allocation.Register;
            StaticAddr = allocation.StaticAddr;
            PhiTempBase = allocation.PhiTempBase;
            LoopInductionPreheaderSync = allocation.LoopInductionPreheaderSync;
            FusedPointerSite = allocation.FusedPointerSite;
            PointerHomePreheaderSync = allocation.PointerHomePreheaderSync;
            Recursive = recursive;
            Banked = banked ?? System.Collections.Immutable.ImmutableHashSet<IrFunction>.Empty;
            IsEntry = isEntry;
            SoftStackBase = softStackBase;
            FrameBase = allocation.FrameBase;
            FrameSize = allocation.FrameEnd - allocation.FrameBase;
            WramGlobalsSize = wramGlobalsSize;
            OamDmaSrcAddr = oamDmaSrcAddr;
            FusedGep = ComputeFusedGeps(fn, Slot);
        }

        public bool IsRecursive => Recursive.Contains(Fn);

        /// <summary>A dynamic <c>gep</c> (one with a WRAM slot — a constant-index gep is already a
        /// compile-time address and never reaches here) is fusable when its result has exactly one use in
        /// the whole function, and that use is the <em>immediately following instruction</em> in the same
        /// block — a <see cref="LoadInstruction"/> or <see cref="StoreInstruction"/> reading/writing
        /// through it. All three restrictions matter:
        /// <list type="bullet">
        /// <item>Single use: fusing recomputes the address at its use site instead of materializing it
        /// once to a slot, so a second use would recompute it twice — only correct/valuable when there is
        /// exactly one consumer.</item>
        /// <item>Same block: <see cref="LoopInvariantCodeMotionPass"/> may have hoisted this very gep out
        /// of an enclosing loop specifically so it is computed once instead of every iteration. Fusing it
        /// back into a use inside that loop would silently undo the hoist and recompute it every
        /// iteration — a correctness-preserving but performance-hostile move this pass must not make.</item>
        /// <item><b>Immediately next, not just "later in the block":</b> deferring a gep's computation
        /// from its own position to a later use is only sound if nothing gets a chance to run in between.
        /// <see cref="FunctionAllocation"/> assigns every value's WRAM slot from the IR's original
        /// instruction order and freely reuses a slot once that value's original last use is behind it —
        /// it has no idea this pass will later delay reading that operand. Fuse across a gap and an
        /// intervening instruction can be allocated the very slot the deferred computation still needs to
        /// read, silently corrupting the address it computes. Requiring the use to be the *immediately
        /// next* instruction closes that gap to zero width: nothing can be allocated in between, so the
        /// allocator's assumptions about every operand's liveness stay valid. (An earlier version of this
        /// rule allowed any later same-block use and produced exactly this corruption — caught by
        /// <c>CSharpEndToEndTests.Pointer_IndexedStoreWithVariableOffset</c> and 27 similar end-to-end
        /// failures.)</item>
        /// </list>
        /// This never reorders or removes an actual load/store — those keep their original relative
        /// order and still execute exactly once each; only the pure gep arithmetic moves, and only by one
        /// instruction slot.</summary>
        private static HashSet<GetElementPtrInstruction> ComputeFusedGeps(
            IrFunction fn,
            Dictionary<IrValue, int> slot
        )
        {
            var useCounts = new Dictionary<IrValue, int>(ReferenceEqualityComparer.Instance);
            foreach (var block in fn.Blocks)
            foreach (var instr in block.Instructions)
            foreach (var operand in instr.Operands)
                useCounts[operand] = useCounts.GetValueOrDefault(operand) + 1;

            var fused = new HashSet<GetElementPtrInstruction>(ReferenceEqualityComparer.Instance);
            foreach (var block in fn.Blocks)
            {
                var instructions = block.Instructions;
                for (var i = 0; i < instructions.Count - 1; i++)
                {
                    if (instructions[i] is not GetElementPtrInstruction g)
                        continue;
                    if (!slot.ContainsKey(g)) // a static (constant-address) gep needs no fusion
                        continue;
                    if (useCounts.GetValueOrDefault(g) != 1)
                        continue;

                    var next = instructions[i + 1];
                    if (
                        (next is LoadInstruction l && ReferenceEquals(l.Pointer, g))
                        || (next is StoreInstruction s && ReferenceEquals(s.Pointer, g))
                    )
                        fused.Add(g);
                }
            }
            return fused;
        }

        /// <summary>Whether this function returns its value through <see cref="ReturnScratch"/> rather
        /// than registers: recursive functions (so the frame restore cannot clobber it) and banked
        /// functions (so the far-call thunk's bank restore cannot clobber it).</summary>
        public bool UsesMemoryReturn(IrFunction fn) =>
            Recursive.Contains(fn) || Banked.Contains(fn);

        /// <summary>Total bytes of a function's parameters (contiguous at its frame base).</summary>
        public int ParamBytes(IrFunction fn)
        {
            int bytes = 0;
            foreach (var p in fn.Parameters)
                bytes += SizeOf(p.Type);
            return bytes;
        }

        /// <summary>A compile-time-known address: an alloca/constant-gep, a global's address, or a
        /// constant-address pointer (e.g. <c>(byte*)0xFF40</c> for direct MMIO).</summary>
        public bool TryStaticAddr(IrValue value, out int addr)
        {
            if (StaticAddr.TryGetValue(value, out addr))
                return true;
            if (value is IrGlobalRef g && Globals.TryGetValue(g.Global, out addr))
                return true;
            if (value is IrConstInt c && value.Type.Kind == IrTypeKind.Pointer)
            {
                addr = (int)c.Value;
                return true;
            }
            addr = 0;
            return false;
        }

        /// <summary>Copy the N low bytes of a value into fixed scratch at <paramref name="scratch"/>.</summary>
        public void CopyToScratch(IrValue value, int scratch, int n)
        {
            for (int k = 0; k < n; k++)
            {
                LoadByteToA(value, k);
                StoreAToAddr(scratch + k);
            }
        }

        /// <summary>Copy N bytes from fixed scratch at <paramref name="scratch"/> into a destination slot.</summary>
        public void CopyFromScratch(int scratch, int dst, int n)
        {
            for (int k = 0; k < n; k++)
            {
                LoadAFromAddr(scratch + k);
                StoreAToAddr(dst + k);
            }
        }

        // ---- Byte-level helpers -------------------------------------------

        /// <summary>Load byte <paramref name="k"/> (0 = low) of a value into <c>A</c>.</summary>
        public void LoadByteToA(IrValue value, int k)
        {
            switch (value)
            {
                case IrConstInt c:
                    E.U8(0x3E); // LD A, d8
                    E.U8(Sm83Ops.ByteOf(value, k));
                    break;
                default:
                    if (Register.TryGetValue(value, out var reg))
                    {
                        // Register-resident: LD A, r (byte k selects the low/high half of a pair).
                        E.U8(ResidentToAOpcode(reg, k));
                    }
                    else if (Slot.TryGetValue(value, out int addr))
                    {
                        LoadAFromAddr(addr + k);
                    }
                    else if (TryStaticAddr(value, out int ptr))
                    {
                        E.U8(0x3E); // pointer literal: LD A, <byte k of address>
                        E.U8((byte)(ptr >> (8 * k)));
                    }
                    else
                    {
                        throw new NotSupportedException(
                            "SM83 backend operand must be a constant, parameter, prior result, or global."
                        );
                    }
                    break;
            }
        }

        public void LoadByteToB(IrValue value, int k)
        {
            LoadByteToA(value, k);
            E.U8(0x47); // LD B, A
        }

        /// <summary>Store byte <paramref name="k"/> of an instruction's result (already in <c>A</c>) to its
        /// home: a CPU register if the value is register-resident (byte <paramref name="k"/> selects the
        /// low/high half of a pair), else its WRAM slot. Emitters that produce a residency-eligible result
        /// store through this instead of <see cref="StoreAToAddr"/> so the value can be kept in a register.</summary>
        public void StoreResultByte(IrValue value, int k)
        {
            if (Register.TryGetValue(value, out var reg))
                E.U8(AToResidentOpcode(reg, k));
            else
                StoreAToAddr(Slot[value] + k);
        }

        /// <summary>Load the low <paramref name="n"/> bytes of a value into a CPU register (single or pair),
        /// low byte first — used by a caller to place a call argument in the callee's parameter register.</summary>
        public void LoadValueIntoRegister(IrValue value, Mir.Sm83Register reg, int n)
        {
            for (int k = 0; k < n; k++)
            {
                LoadByteToA(value, k);
                E.U8(AToResidentOpcode(reg, k));
            }
        }

        /// <summary>Load the 16-bit value currently stored AT a fixed WRAM address into a register pair —
        /// Layer 2's preheader sync, where the register's initial value must come from memory (a pointer
        /// local's own home slot) rather than an SSA value's bytes (contrast <see cref="LoadValueIntoRegister"/>,
        /// which loads a *value*, not a dereference).</summary>
        public void LoadAddressContentsIntoRegisterPair(int addr, Mir.Sm83Register reg)
        {
            LoadAFromAddr(addr);
            E.U8(AToResidentOpcode(reg, 0));
            LoadAFromAddr(addr + 1);
            E.U8(AToResidentOpcode(reg, 1));
        }

        /// <summary><c>LD A, r</c> opcode to read byte <paramref name="k"/> of a resident value (0 = low).
        /// A pair reads its low register for byte 0 and its high register for byte 1.</summary>
        private static byte ResidentToAOpcode(Mir.Sm83Register reg, int k) =>
            (reg, k) switch
            {
                (Mir.Sm83Register.C, 0) => 0x79, // LD A, C
                (Mir.Sm83Register.D, 0) => 0x7A, // LD A, D
                (Mir.Sm83Register.E, 0) => 0x7B, // LD A, E
                (Mir.Sm83Register.H, 0) => 0x7C, // LD A, H
                (Mir.Sm83Register.L, 0) => 0x7D, // LD A, L
                (Mir.Sm83Register.Hl, 0) => 0x7D, // LD A, L
                (Mir.Sm83Register.Hl, 1) => 0x7C, // LD A, H
                (Mir.Sm83Register.De, 0) => 0x7B, // LD A, E
                (Mir.Sm83Register.De, 1) => 0x7A, // LD A, D
                _ => throw new NotSupportedException(
                    $"cannot load resident {reg} byte {k} into A."
                ),
            };

        /// <summary><c>LD r, A</c> opcode to write byte <paramref name="k"/> of a resident value (0 = low).</summary>
        private static byte AToResidentOpcode(Mir.Sm83Register reg, int k) =>
            (reg, k) switch
            {
                (Mir.Sm83Register.C, 0) => 0x4F, // LD C, A
                (Mir.Sm83Register.D, 0) => 0x57, // LD D, A
                (Mir.Sm83Register.E, 0) => 0x5F, // LD E, A
                (Mir.Sm83Register.H, 0) => 0x67, // LD H, A
                (Mir.Sm83Register.L, 0) => 0x6F, // LD L, A
                (Mir.Sm83Register.Hl, 0) => 0x6F, // LD L, A
                (Mir.Sm83Register.Hl, 1) => 0x67, // LD H, A
                (Mir.Sm83Register.De, 0) => 0x5F, // LD E, A
                (Mir.Sm83Register.De, 1) => 0x57, // LD D, A
                _ => throw new NotSupportedException(
                    $"cannot store A into resident {reg} byte {k}."
                ),
            };

        public void LoadAFromAddr(int addr) => E.LoadA(addr); // may be elided if A already holds it

        public void StoreAToAddr(int addr) => E.StoreA(addr);

        /// <summary>Store a register pair to a slot, low byte first (via A). <paramref name="lo"/> and
        /// <paramref name="hi"/> are the <c>LD A,r</c> opcodes for the low and high registers.</summary>
        public void StoreRegPair(int dst, int n, int hi, int lo)
        {
            E.U8((byte)lo);
            StoreAToAddr(dst);
            if (n == 2)
            {
                E.U8((byte)hi);
                StoreAToAddr(dst + 1);
            }
        }
    }
}
