using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>Lowers load/store/gep (pointer and array) instructions.</summary>
    internal sealed class MemoryEmitter
    {
        private readonly EmitContext _ctx;
        private readonly Emitter _e;

        public MemoryEmitter(EmitContext ctx)
        {
            _ctx = ctx;
            _e = ctx.E;
        }

        // ---- Memory --------------------------------------------------------

        public void EmitLoad(LoadInstruction l)
        {
            int n = SizeOf(l.Type);

            // Layer 2 (stride-1 pointer residency): this load is the pointer local's own reload from its
            // fixed WRAM home — the register left behind by the previous iteration (or the preheader
            // sync, on the first) is already the live value, so there is nothing to emit at all (see
            // FunctionAllocation's "Loop-pointer register residency" region).
            if (_ctx.Register.ContainsKey(l) && !_ctx.Slot.ContainsKey(l))
                return;

            // Layer 2, continued: this load is the pointer's one designated dereference — the fused
            // opcode both reads through the resident register and advances it to represent `reload + 1`
            // (the paired gep's value), so that gep needs no separate emission at all either.
            if (_ctx.FusedPointerSite.TryGetValue(l, out var fusedReg))
            {
                if (fusedReg == Mir.Sm83Register.Hl)
                    _e.U8(0x2A); // LD A, (HL+)
                else
                {
                    _e.U8(0x1A); // LD A, (DE)
                    _e.U8(0x13); // INC DE   (SM83 has no (de+) addressing mode)
                }
                _ctx.StoreResultByte(l, 0);
                return;
            }

            int dst = _ctx.Slot[l];

            if (_ctx.TryStaticAddr(l.Pointer, out int addr))
            {
                for (int k = 0; k < n; k++)
                {
                    _ctx.LoadAFromAddr(addr + k);
                    _ctx.StoreAToAddr(dst + k);
                }
                return;
            }

            LoadPointerToHL(l.Pointer);
            for (int k = 0; k < n; k++)
            {
                _e.U8(0x7E); // LD A, (HL)
                _ctx.StoreAToAddr(dst + k);
                if (k < n - 1)
                    _e.U8(0x23); // INC HL
            }
        }

        public void EmitStore(StoreInstruction s)
        {
            int n = SizeOf(s.Value.Type);

            // Layer 2 (stride-1 pointer residency): symmetric with the fused load above.
            if (_ctx.FusedPointerSite.TryGetValue(s, out var fusedReg))
            {
                _ctx.LoadByteToA(s.Value, 0);
                if (fusedReg == Mir.Sm83Register.Hl)
                    _e.U8(0x22); // LD (HL+), A
                else
                {
                    _e.U8(0x12); // LD (DE), A
                    _e.U8(0x13); // INC DE
                }
                return;
            }

            if (_ctx.TryStaticAddr(s.Pointer, out int addr))
            {
                for (int k = 0; k < n; k++)
                {
                    _ctx.LoadByteToA(s.Value, k);
                    _ctx.StoreAToAddr(addr + k);
                }
                return;
            }

            LoadPointerToHL(s.Pointer); // (LoadByteToA below only touches A, not HL)
            for (int k = 0; k < n; k++)
            {
                _ctx.LoadByteToA(s.Value, k);
                _e.U8(0x77); // LD (HL), A
                if (k < n - 1)
                    _e.U8(0x23); // INC HL
            }
        }

        /// <summary>Compute a dynamic pointer <c>base + index * sizeof(element)</c> into its slot.</summary>
        public void EmitGep(GetElementPtrInstruction g)
        {
            ComputeGepIntoHL(g);
            _ctx.StoreRegPair(_ctx.Slot[g], 2, hi: 0x7C, lo: 0x7D); // HL -> slot
        }

        /// <summary>Compute <paramref name="g"/>'s address into <c>HL</c> without touching its slot — the
        /// shared core of <see cref="EmitGep"/> (materialize-to-slot) and the fused path in
        /// <see cref="LoadPointerToHL"/> (materialize-and-use-immediately, for a gep with exactly one
        /// consumer; see <see cref="EmitContext.FusedGep"/>).</summary>
        private void ComputeGepIntoHL(GetElementPtrInstruction g)
        {
            int size = SizeOf(g.ElementType);

            LoadIndexToDE(g.Index); // offset = index (widened to 16 bits)
            if (size != 1)
            {
                if (System.Numerics.BitOperations.IsPow2(size))
                {
                    int shift = System.Numerics.BitOperations.Log2((uint)size);
                    for (int s = 0; s < shift; s++)
                    {
                        _e.U8(0xCB);
                        _e.U8(0x23); // SLA E
                        _e.U8(0xCB);
                        _e.U8(0x12); // RL D   (DE <<= 1)
                    }
                }
                else
                {
                    _e.U8(0x01);
                    _e.U16(size); // LD BC, size
                    _e.Jump(0xCD, _e.RoutineLabel("mul16")); // HL = DE * size
                    _e.U8(0x54);
                    _e.U8(0x5D); // LD D,H ; LD E,L  (offset -> DE)
                }
            }

            LoadPointerToHL(g.BasePointer); // HL = base
            _e.U8(0x19); // ADD HL, DE
        }

        private void LoadIndexToDE(IrValue index)
        {
            if (SizeOf(index.Type) > 2)
                throw new NotSupportedException("SM83 backend gep index must be <= 16-bit.");
            _ctx.LoadByteToA(index, 0);
            _e.U8(0x5F); // LD E, A
            if (SizeOf(index.Type) == 2)
            {
                _ctx.LoadByteToA(index, 1);
                _e.U8(0x57); // LD D, A
            }
            else
            {
                _e.U8(0x16);
                _e.U8(0x00); // LD D, 0
            }
        }

        /// <summary>Load a pointer value into HL: a static address as an immediate, a fused single-use
        /// <c>gep</c> computed inline (skipping its slot entirely — see <see cref="EmitContext.FusedGep"/>),
        /// else reloaded from its slot.
        /// <para>TODO: this method checks <c>Slot</c> without checking <c>Register</c> first (unlike
        /// <c>LoadByteToA</c>). A register-resident pointer used as a Load/Store/Gep base OUTSIDE its own
        /// designated fused-dereference site would therefore read a stale <c>Slot</c> value here. Currently a
        /// non-issue: every admission path (Layer 1 Phase 2's <c>TryFindPhiDerefSite</c>, Layer 2's
        /// <c>TryFindPointerReloadShape</c>) provably admits a candidate only when it has at most one in-loop
        /// dereference, and that one dereference is always caught by <c>EmitLoad</c>/<c>EmitStore</c>'s own
        /// <c>FusedPointerSite</c> check before this method is ever called. Flag for whoever next widens
        /// residency admission to allow more than one dereference per resident.</para></summary>
        private void LoadPointerToHL(IrValue pointer)
        {
            if (pointer is GetElementPtrInstruction fusedGep && _ctx.FusedGep.Contains(fusedGep))
            {
                ComputeGepIntoHL(fusedGep);
                return;
            }
            if (_ctx.TryStaticAddr(pointer, out int addr))
            {
                LdHL(_e, addr); // LD HL, addr
            }
            else if (_ctx.Slot.TryGetValue(pointer, out int slot))
            {
                _ctx.LoadAFromAddr(slot);
                _e.U8(0x6F); // LD A, (slot)   ; LD L, A
                _ctx.LoadAFromAddr(slot + 1);
                _e.U8(0x67); // LD A, (slot+1) ; LD H, A
            }
            else
            {
                throw new NotSupportedException(
                    "SM83 backend cannot resolve this pointer operand."
                );
            }
        }
    }
}
