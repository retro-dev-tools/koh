using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>Shared per-function emit context: the code buffer, this function's WRAM allocation,
    /// and the byte-level load/store/copy helpers the concern-specific emitters build on.</summary>
    internal abstract class EmitBase
    {
        protected readonly Emitter _e;

        protected readonly IrFunction _fn;

        protected readonly IReadOnlyDictionary<IrFunction, FunctionAllocation> _allocations;

        protected readonly IReadOnlyDictionary<IrGlobal, int> _globals;

        protected readonly Dictionary<IrValue, int> _slot;

        protected readonly Dictionary<IrValue, int> _staticAddr;

        protected readonly int _phiTempBase;

        protected readonly IReadOnlySet<IrFunction> _recursive;

        protected readonly IReadOnlySet<IrFunction> _banked;

        protected readonly bool _isEntry;

        protected readonly int _softStackBase;

        protected readonly int _frameBase;

        protected readonly int _frameSize;

        protected EmitBase(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null)
        {
            _e = emitter;
            _fn = fn;
            _allocations = allocations;
            _globals = globals;
            var allocation = allocations[fn];
            _slot = allocation.Slot;
            _staticAddr = allocation.StaticAddr;
            _phiTempBase = allocation.PhiTempBase;
            _recursive = recursive;
            _banked = banked ?? System.Collections.Immutable.ImmutableHashSet<IrFunction>.Empty;
            _isEntry = isEntry;
            _softStackBase = softStackBase;
            _frameBase = allocation.FrameBase;
            _frameSize = allocation.FrameEnd - allocation.FrameBase;
        }

        protected bool IsRecursive => _recursive.Contains(_fn);

        /// <summary>Whether this function returns its value through <see cref="ReturnScratch"/> rather
        /// than registers: recursive functions (so the frame restore cannot clobber it) and banked
        /// functions (so the far-call thunk's bank restore cannot clobber it).</summary>
        protected bool UsesMemoryReturn(IrFunction fn) => _recursive.Contains(fn) || _banked.Contains(fn);

        /// <summary>Total bytes of a function's parameters (contiguous at its frame base).</summary>
        protected static int ParamBytes(IrFunction fn)
        {
            int bytes = 0;
            foreach (var p in fn.Parameters)
                bytes += SizeOf(p.Type);
            return bytes;
        }

        /// <summary>A compile-time-known address: an alloca/constant-gep, a global's address, or a
        /// constant-address pointer (e.g. <c>(byte*)0xFF40</c> for direct MMIO).</summary>
        protected bool TryStaticAddr(IrValue value, out int addr)
        {
            if (_staticAddr.TryGetValue(value, out addr))
                return true;
            if (value is IrGlobalRef g && _globals.TryGetValue(g.Global, out addr))
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
        protected void CopyToScratch(IrValue value, int scratch, int n)
        {
            for (int k = 0; k < n; k++)
            {
                LoadByteToA(value, k);
                StoreAToAddr(scratch + k);
            }
        }

        /// <summary>Copy N bytes from fixed scratch at <paramref name="scratch"/> into a destination slot.</summary>
        protected void CopyFromScratch(int scratch, int dst, int n)
        {
            for (int k = 0; k < n; k++)
            {
                LoadAFromAddr(scratch + k);
                StoreAToAddr(dst + k);
            }
        }

        // ---- Byte-level helpers -------------------------------------------

        /// <summary>Load byte <paramref name="k"/> (0 = low) of a value into <c>A</c>.</summary>
        protected void LoadByteToA(IrValue value, int k)
        {
            switch (value)
            {
                case IrConstInt c:
                    _e.U8(0x3E);                 // LD A, d8
                    _e.U8(Sm83Ops.ByteOf(value, k));
                    break;
                default:
                    if (_slot.TryGetValue(value, out int addr))
                    {
                        LoadAFromAddr(addr + k);
                    }
                    else if (TryStaticAddr(value, out int ptr))
                    {
                        _e.U8(0x3E);             // pointer literal: LD A, <byte k of address>
                        _e.U8((byte)(ptr >> (8 * k)));
                    }
                    else
                    {
                        throw new NotSupportedException(
                            "SM83 backend operand must be a constant, parameter, prior result, or global.");
                    }
                    break;
            }
        }

        protected void LoadByteToB(IrValue value, int k)
        {
            LoadByteToA(value, k);
            _e.U8(0x47); // LD B, A
        }

        protected void LoadAFromAddr(int addr) => _e.LoadA(addr);   // may be elided if A already holds it

        protected void StoreAToAddr(int addr) => _e.StoreA(addr);
    }
}
