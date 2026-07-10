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

        public readonly Dictionary<IrValue, int> StaticAddr;

        public readonly int PhiTempBase;

        public readonly IReadOnlySet<IrFunction> Recursive;

        public readonly IReadOnlySet<IrFunction> Banked;

        public readonly bool IsEntry;

        public readonly int SoftStackBase;

        public readonly int FrameBase;

        public readonly int FrameSize;

        public EmitContext(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null
        )
        {
            E = emitter;
            Fn = fn;
            Allocations = allocations;
            Globals = globals;
            var allocation = allocations[fn];
            Slot = allocation.Slot;
            StaticAddr = allocation.StaticAddr;
            PhiTempBase = allocation.PhiTempBase;
            Recursive = recursive;
            Banked = banked ?? System.Collections.Immutable.ImmutableHashSet<IrFunction>.Empty;
            IsEntry = isEntry;
            SoftStackBase = softStackBase;
            FrameBase = allocation.FrameBase;
            FrameSize = allocation.FrameEnd - allocation.FrameBase;
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
                    if (Slot.TryGetValue(value, out int addr))
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
