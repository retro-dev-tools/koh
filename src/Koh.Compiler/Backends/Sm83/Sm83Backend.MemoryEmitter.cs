using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>Lowers load/store/gep (pointer and array) instructions.</summary>
    internal sealed class MemoryEmitter : EmitBase
    {
        public MemoryEmitter(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null)
            : base(emitter, fn, allocations, globals, recursive, isEntry, softStackBase, banked) { }

        // ---- Memory --------------------------------------------------------

        public void EmitLoad(LoadInstruction l)
        {
            int dst = _slot[l];
            int n = SizeOf(l.Type);

            if (TryStaticAddr(l.Pointer, out int addr))
            {
                for (int k = 0; k < n; k++)
                {
                    LoadAFromAddr(addr + k);
                    StoreAToAddr(dst + k);
                }
                return;
            }

            LoadPointerToHL(l.Pointer);
            for (int k = 0; k < n; k++)
            {
                _e.U8(0x7E);                     // LD A, (HL)
                StoreAToAddr(dst + k);
                if (k < n - 1) _e.U8(0x23);      // INC HL
            }
        }

        public void EmitStore(StoreInstruction s)
        {
            int n = SizeOf(s.Value.Type);

            if (TryStaticAddr(s.Pointer, out int addr))
            {
                for (int k = 0; k < n; k++)
                {
                    LoadByteToA(s.Value, k);
                    StoreAToAddr(addr + k);
                }
                return;
            }

            LoadPointerToHL(s.Pointer);          // (LoadByteToA below only touches A, not HL)
            for (int k = 0; k < n; k++)
            {
                LoadByteToA(s.Value, k);
                _e.U8(0x77);                     // LD (HL), A
                if (k < n - 1) _e.U8(0x23);      // INC HL
            }
        }

        /// <summary>Compute a dynamic pointer <c>base + index * sizeof(element)</c> into its slot.</summary>
        public void EmitGep(GetElementPtrInstruction g)
        {
            int size = SizeOf(g.ElementType);

            LoadIndexToDE(g.Index);              // offset = index (widened to 16 bits)
            if (size != 1)
            {
                if (Sm83Ops.IsPowerOfTwo(size))
                {
                    for (int s = 0; s < Sm83Ops.Log2(size); s++)
                    {
                        _e.U8(0xCB); _e.U8(0x23);   // SLA E
                        _e.U8(0xCB); _e.U8(0x12);   // RL D   (DE <<= 1)
                    }
                }
                else
                {
                    _e.U8(0x01); _e.U16(size); // LD BC, size
                    _e.Jump(0xCD, _e.RoutineLabel("mul16"));          // HL = DE * size
                    _e.U8(0x54); _e.U8(0x5D);                         // LD D,H ; LD E,L  (offset -> DE)
                }
            }

            LoadPointerToHL(g.BasePointer);      // HL = base
            _e.U8(0x19);                         // ADD HL, DE
            StoreHLToSlot(_slot[g]);
        }

        private void LoadIndexToDE(IrValue index)
        {
            if (SizeOf(index.Type) > 2)
                throw new NotSupportedException("SM83 backend gep index must be <= 16-bit.");
            LoadByteToA(index, 0);
            _e.U8(0x5F);                         // LD E, A
            if (SizeOf(index.Type) == 2)
            {
                LoadByteToA(index, 1);
                _e.U8(0x57);                     // LD D, A
            }
            else
            {
                _e.U8(0x16); _e.U8(0x00);        // LD D, 0
            }
        }

        /// <summary>Load a pointer value into HL: a static address as an immediate, else from its slot.</summary>
        private void LoadPointerToHL(IrValue pointer)
        {
            if (TryStaticAddr(pointer, out int addr))
            {
                LdHL(_e, addr);   // LD HL, addr
            }
            else if (_slot.TryGetValue(pointer, out int slot))
            {
                LoadAFromAddr(slot); _e.U8(0x6F);       // LD A, (slot)   ; LD L, A
                LoadAFromAddr(slot + 1); _e.U8(0x67);   // LD A, (slot+1) ; LD H, A
            }
            else
            {
                throw new NotSupportedException("SM83 backend cannot resolve this pointer operand.");
            }
        }

        private void StoreHLToSlot(int slot)
        {
            _e.U8(0x7D); StoreAToAddr(slot);        // LD A, L ; store low
            _e.U8(0x7C); StoreAToAddr(slot + 1);    // LD A, H ; store high
        }
    }
}
