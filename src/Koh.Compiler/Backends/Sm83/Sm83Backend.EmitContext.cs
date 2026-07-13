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

        public EmitContext(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null,
            int wramGlobalsSize = 0
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
            Recursive = recursive;
            Banked = banked ?? System.Collections.Immutable.ImmutableHashSet<IrFunction>.Empty;
            IsEntry = isEntry;
            SoftStackBase = softStackBase;
            FrameBase = allocation.FrameBase;
            FrameSize = allocation.FrameEnd - allocation.FrameBase;
            WramGlobalsSize = wramGlobalsSize;
        }

        public bool IsRecursive => Recursive.Contains(Fn);

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
