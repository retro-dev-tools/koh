using Koh.Compiler.Backends.Sm83.Mir;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// A peephole over an already-emitted SM83 region, driven by the <see cref="MirDecoder"/>: the region
/// is lifted to typed instructions with a <see cref="MirEffects"/> footprint each, and rewrites are
/// found by reading those footprints rather than hand-maintained opcode sets. Each rewrite optionally
/// overwrites one opcode byte and deletes a run of bytes, which is what <see cref="Emitter.PeepholeFrom"/>
/// applies:
/// <list type="bullet">
/// <item><c>LD A, 0</c> → <c>XOR A</c> — one byte shorter, but <c>XOR A</c> clobbers the flags, so it is
/// applied only where <see cref="FlagsDeadAfter"/> proves every flag dead (a forward scan reaches an
/// instruction that rewrites all flags before any flag is read or a boundary is hit — sound inside
/// <c>ADC</c>/<c>SBC</c> carry chains, where the next op reads carry).</item>
/// <item><c>LD A,(HL)</c>/<c>LD (HL),A</c> immediately followed by <c>INC HL</c>/<c>DEC HL</c> →
/// the auto-increment/decrement form (<c>LD A,(HL+)</c> etc.), folding the pointer bump into the load or
/// store. Neither instruction touches a flag, so the only guard is that the <c>INC</c>/<c>DEC HL</c> is
/// not a branch target.</item>
/// <item><c>CALL nn ; RET</c> → <c>JP nn</c> — a tail call: instead of calling and returning, jump so the
/// callee's own <c>RET</c> returns straight to this function's caller. Sound only when the <c>CALL</c>
/// targets a directly-returning function entry (not a far-call thunk, a runtime routine, or an interrupt
/// handler whose epilogue is <c>RETI</c>); the caller supplies that whitelist as
/// <c>tailCallSafeCalls</c>. The operand bytes are kept and the trailing <c>RET</c> is deleted, so unlike
/// the pair rules this one overwrites the opcode but deletes a non-adjacent byte.</item>
/// <item>Dead store: <c>LD (a16),A ; … ; LD (a16),A</c> to the same non-MMIO address with no intervening
/// read of memory, control-flow, side effect, or join → the first store is deleted whole (three bytes,
/// no overwrite). Sound because nothing between the two stores can observe the first value before the
/// second overwrites it. The window between the two stores must be free of any memory access — a read
/// might observe the slot, and a write through a register pointer goes to an address unknown here that
/// could be an MMIO register (e.g. an OAM-DMA trigger, which asynchronously reads WRAM); an absolute
/// <c>LD (a16),A</c> is exempt since its address is known — and free of control flow, a modeled side
/// effect, or a join. Only WRAM <c>[0xC000, 0xE000)</c> — where the backend puts its globals/temps and a
/// repeated write is idempotent — is tracked (MMIO/HRAM, VRAM/OAM, cartridge RAM excluded). Those barriers
/// still cover only <em>synchronous</em> observers, so the rule is additionally gated
/// (<c>allowDeadStore</c>) on the module having no interrupt handler: an interrupt can fire between the two
/// stores and a handler can read a shared static, which no per-region scan can see. With no handler there
/// is no asynchronous observer. (This matches the IR-level dead-store pass, likewise conservative — it only
/// elides stores to non-escaping allocas.) A DMA already in flight when the pair runs is the one remaining
/// asynchronous reader; for it to observe the first value the slot would have to sit in the active DMA
/// source page while the program mutates that page mid-transfer, which yields indeterminate OAM regardless
/// of this rewrite — so, as elsewhere, a plain WRAM store is treated as non-volatile. This is the first
/// rule that fires on backend-emitted redundancy the IR pass cannot see.</item>
/// <item>Redundant reload: <c>LD A,(a16)</c> whose value <c>A</c> already holds — because an earlier
/// <c>LD A,(a16)</c> or <c>LD (a16),A</c> to the same WRAM slot ran, with nothing since that could change
/// <c>A</c> (a write to <c>A</c>, a control transfer, a side effect) or that slot (an unknown-pointer
/// memory write) — is deleted whole (three bytes). This catches the cross-instruction reloads the
/// emitter's local <c>A</c>-tracking cannot (its knowledge dies at the next emit); only WRAM is tracked,
/// and it is gated on the same no-interrupt-handler condition as the dead store, since a handler could
/// asynchronously overwrite the slot with a value the elided reload would otherwise have observed.</item>
/// <item>Jump elimination: an unconditional <c>JP a16</c> whose target is the instruction immediately
/// after it — the fall-through — is deleted whole (three bytes). The backend emits a block-terminating
/// <c>JP</c> for every fall-through edge, so this is frequent. Sound even when the jump is itself a branch
/// target: the caller relocates that label onto the fall-through, which is where the jump went anyway.
/// This is the first rule whose deleted bytes include a fixup operand, so <see cref="Emitter.PeepholeFrom"/>
/// also drops the jump's fixup; the caller supplies the target-is-next set (<c>redundantJumps</c>) because
/// the operand is still an unresolved placeholder at peephole time.</item>
/// </list>
/// </summary>
internal static class Sm83Peephole
{
    /// <summary>One rewrite: if <see cref="NewOpcode"/> is non-null, overwrite the byte at
    /// <see cref="Offset"/> with it; then delete <see cref="DeleteCount"/> bytes starting at
    /// <see cref="DeleteStart"/>. The two "collapse a pair" rules overwrite an opcode and delete the next
    /// byte; the tail-call rule overwrites the opcode and deletes the (non-adjacent) trailing <c>RET</c>;
    /// the dead-store rule deletes a whole instruction with no overwrite.</summary>
    public readonly record struct Edit(
        int Offset,
        byte? NewOpcode,
        int DeleteStart,
        int DeleteCount
    )
    {
        /// <summary>An adjacent collapse: overwrite <paramref name="offset"/> and delete the next byte.</summary>
        public Edit(int offset, byte newOpcode)
            : this(offset, newOpcode, offset + 1, 1) { }

        /// <summary>Overwrite <paramref name="offset"/> and delete a single, possibly non-adjacent, byte.</summary>
        public Edit(int offset, byte newOpcode, int deleteOffset)
            : this(offset, newOpcode, deleteOffset, 1) { }

        /// <summary>Delete a whole instruction run — <paramref name="count"/> bytes at
        /// <paramref name="offset"/> — with no opcode overwrite.</summary>
        public static Edit DeleteRun(int offset, int count) => new(offset, null, offset, count);
    }

    /// <summary>The rewrites applicable in <c>[start, end)</c>. Not necessarily in ascending offset order
    /// (the dead-store rule appends a deletion of an earlier store once the store that kills it is seen), so
    /// the caller sorts the byte positions it deletes.
    /// <paramref name="boundaries"/> holds the absolute offsets of branch targets / block joins, across
    /// which liveness cannot be assumed and instructions must not be folded away.
    /// <paramref name="tailCallSafeCalls"/> holds the absolute offsets of <c>CALL</c> opcodes whose target
    /// is a directly-returning function entry, the only ones the tail-call rule may rewrite (see the type
    /// summary).
    /// <paramref name="redundantJumps"/> holds the absolute offsets of unconditional <c>JP a16</c> opcodes
    /// whose target is the instruction immediately after them — a jump to the fall-through, deleted whole by
    /// the jump-elimination rule. The caller computes it from the fixups, since the operand is an unresolved
    /// placeholder in the bytes here.</summary>
    public static List<Edit> FindEdits(
        IReadOnlyList<byte> code,
        int start,
        int end,
        HashSet<int> boundaries,
        HashSet<int> tailCallSafeCalls,
        HashSet<int> redundantJumps,
        bool allowDeadStore
    )
    {
        // Lift the region to typed instructions. Offsets in `instrs` are relative to `start`; the shared
        // decoder recovers boundaries and computes each instruction's effect footprint.
        var region = new byte[end - start];
        for (var i = 0; i < region.Length; i++)
            region[i] = code[start + i];
        var instrs = MirDecoder.Decode(region).Instructions;

        // Rule 4 (dead store) state: the last `LD (a16),A` seen with a pending, not-yet-observed value —
        // its address, the absolute offset of its opcode, and its length — or null once a barrier clears it.
        int? pendingStoreAddr = null;
        int pendingStoreOffset = 0,
            pendingStoreLength = 0;

        // Rule 5 (redundant reload) state: the WRAM addresses whose value A provably holds right now — after
        // a `LD A,(a16)` or `LD (a16),A` to that slot, until something could change A or the slot. Gated on
        // the same no-interrupt-handler condition as the dead store (an interrupt could asynchronously
        // rewrite a slot, so the reload A skips would have observed a value A no longer holds).
        var aMirrors = new HashSet<int>();

        var edits = new List<Edit>();
        for (var i = 0; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            var abs = start + instr.Offset;

            // A join makes A's contents unknown on entry (another path reaches here), so nothing A held on
            // the fall-through path can be assumed. The store/load below re-establish it if they run.
            if (boundaries.Contains(abs))
                aMirrors.Clear();

            // Rule 4 — dead store: LD (a16),A whose value is overwritten by a later LD (a16),A to the same
            // WRAM address before anything reads it. Handled first because it is stateful, and because an
            // LD (a16),A (0xEA) is matched by none of the pair/tail rules below.
            if (instr is { Opcode: 0xEA, Length: 3 })
            {
                var addr = instr.Bytes[1] | (instr.Bytes[2] << 8);
                // Only WRAM [0xC000, 0xE000): that is where the backend places its globals and temps, and a
                // WRAM byte is plain memory. Outside it a repeated write may not be idempotent — MMIO/HRAM
                // registers, VRAM/OAM under PPU timing, cartridge RAM — so those are never elided.
                var trackable = addr is >= 0xC000 and < 0xE000;
                // This store overwrites a pending one to the same slot (and the pending store is not itself
                // a branch target, so deleting it can't strand an edge): the pending store is dead.
                if (
                    allowDeadStore
                    && trackable
                    && pendingStoreAddr == addr
                    && !boundaries.Contains(pendingStoreOffset)
                )
                    edits.Add(Edit.DeleteRun(pendingStoreOffset, pendingStoreLength));
                pendingStoreAddr = trackable ? addr : null;
                pendingStoreOffset = abs;
                pendingStoreLength = instr.Length;
                // The store writes A's value to a known slot without changing A, so A now also mirrors that
                // slot (existing mirrors stay valid — A and their memory are both unchanged).
                if (allowDeadStore && trackable)
                    aMirrors.Add(addr);
                continue;
            }

            // Rule 5 — redundant reload: LD A,(a16) whose value A already holds from an earlier load of, or
            // store to, the same WRAM slot with nothing since that could change A or that slot. Delete the
            // load. Like the barrier below it is a memory read, so it also ends any dead-store window.
            if (instr is { Opcode: 0xFA, Length: 3 })
            {
                var addr = instr.Bytes[1] | (instr.Bytes[2] << 8);
                var trackable = addr is >= 0xC000 and < 0xE000;
                if (allowDeadStore)
                {
                    if (trackable && aMirrors.Contains(addr) && !boundaries.Contains(abs))
                    {
                        edits.Add(Edit.DeleteRun(abs, instr.Length)); // A already holds (addr)
                    }
                    else
                    {
                        // A is redefined to memory[addr]: it now mirrors exactly that slot, or nothing
                        // trackable if the source is outside WRAM (possibly volatile).
                        aMirrors.Clear();
                        if (trackable)
                            aMirrors.Add(addr);
                    }
                }
                pendingStoreAddr = null; // a memory read ends any pending dead-store window
                continue;
            }
            // Any memory access ends the window in which a pending store is dead: a read might observe the
            // slot, and a write through a register pointer (LD (HL),A etc.) goes to an address unknown here
            // that could be an MMIO register — e.g. an OAM-DMA trigger, which then reads WRAM asynchronously.
            // (An absolute LD (a16),A carries a known address and is handled above, so it is not barred here.)
            // A control transfer, a modeled side effect, or a join (another path could read the slot) also
            // end the window. What survives between the two stores is therefore memory-access-free.
            var effects = instr.Effects;
            if (
                effects.MemRead
                || effects.MemWrite
                || effects.Control != MirControl.Fallthrough
                || effects.SideEffect
                || boundaries.Contains(abs)
            )
                pendingStoreAddr = null;

            // A's mirror set is invalidated by anything that could change A or a tracked slot: a write to A,
            // a memory write through an unknown pointer (an absolute LD (a16),A is handled above), or a
            // control transfer / side effect (a callee may clobber A and shared statics). A plain memory
            // read leaves A and every slot intact, so it does not clear the set.
            if (
                allowDeadStore
                && (
                    (effects.RegWrite & Sm83Register.A) != 0
                    || effects.MemWrite
                    || effects.Control != MirControl.Fallthrough
                    || effects.SideEffect
                )
            )
                aMirrors.Clear();

            // Rule 1 — LD A, 0 → XOR A, when the flags XOR A would clobber are dead.
            if (IsLoadAZero(instr))
            {
                if (FlagsDeadAfter(instrs, i, start, boundaries))
                    edits.Add(new Edit(abs, 0xAF));
                continue;
            }

            // Rule 2 — fold a following INC/DEC HL into an (HL) load/store. The INC/DEC HL must be the
            // next instruction and not itself a branch target.
            if (
                i + 1 < instrs.Count
                && !boundaries.Contains(start + instrs[i + 1].Offset)
                && TryFoldHlStep(instr.Opcode, instrs[i + 1].Opcode) is { } folded
            )
            {
                edits.Add(new Edit(abs, folded));
                i++; // the INC/DEC HL is consumed by the fold — don't reconsider it
                continue;
            }

            // Rule 3 — tail call: CALL nn ; RET → JP nn. Only when the CALL targets a directly-returning
            // function entry (tailCallSafeCalls) and the RET that follows it is not itself a branch target
            // (something jumping to it would lose the return). The CALL's operand bytes stay; the RET byte
            // is deleted, so this is the one rule whose deleted byte is not adjacent to the opcode.
            if (
                instr.Opcode == 0xCD
                && tailCallSafeCalls.Contains(abs)
                && i + 1 < instrs.Count
                && instrs[i + 1].Opcode == 0xC9
                && !boundaries.Contains(start + instrs[i + 1].Offset)
            )
            {
                edits.Add(new Edit(abs, 0xC3, start + instrs[i + 1].Offset));
                i++; // the RET is folded away
                continue;
            }

            // Rule 6 — jump elimination: an unconditional JP a16 whose target is the very next instruction
            // is a no-op (control reaches that instruction either way). Delete it whole. Sound even when the
            // JP is itself a branch target: the caller relocates that label onto the fall-through, which is
            // exactly where the jump went. The JP's operand is a fixup, so the caller drops it (see
            // PeepholeFrom); every earlier rule deletes only non-fixup bytes.
            if (instr is { Opcode: 0xC3, Length: 3 } && redundantJumps.Contains(abs))
                edits.Add(Edit.DeleteRun(abs, instr.Length));
        }
        return edits;
    }

    private static bool IsLoadAZero(MirInstruction instr) =>
        instr is { Opcode: 0x3E, Length: 2 } && instr.Bytes[1] == 0x00;

    /// <summary>The auto-increment/decrement opcode that folds <paramref name="load"/> (an <c>(HL)</c>
    /// accumulator load/store) with <paramref name="step"/> (<c>INC HL</c> 0x23 / <c>DEC HL</c> 0x2B),
    /// or null if the pair is not foldable.</summary>
    private static byte? TryFoldHlStep(byte load, byte step) =>
        (load, step) switch
        {
            (0x7E, 0x23) => 0x2A, // LD A,(HL) ; INC HL → LD A,(HL+)
            (0x7E, 0x2B) => 0x3A, // LD A,(HL) ; DEC HL → LD A,(HL-)
            (0x77, 0x23) => 0x22, // LD (HL),A ; INC HL → LD (HL+),A
            (0x77, 0x2B) => 0x32, // LD (HL),A ; DEC HL → LD (HL-),A
            _ => null,
        };

    /// <summary>True when every CPU flag is dead immediately after instruction <paramref name="index"/>:
    /// scanning forward, an instruction that rewrites all four flags is reached before any flag is read
    /// or a boundary is hit. Reads the decoded <see cref="MirEffects"/> instead of hand-rolled opcode
    /// sets, so it is exact where the old scan was conservative (e.g. a CB rotate that reads no carry but
    /// rewrites all flags now proves the flags dead).</summary>
    private static bool FlagsDeadAfter(
        IReadOnlyList<MirInstruction> instrs,
        int index,
        int start,
        HashSet<int> boundaries
    )
    {
        for (var i = index + 1; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            if (boundaries.Contains(start + instr.Offset))
                return false; // a branch target / join: assume flags live
            var e = instr.Effects;
            if (e.Control != MirControl.Fallthrough)
                return false; // a branch/call/return/halt ends the straight-line run
            if (e.FlagRead != Sm83Flags.None)
                return false; // a flag is consumed before being fully redefined
            if (e.FlagWrite == Sm83Flags.All)
                return true; // all four flags overwritten without a read: dead
        }
        return false; // reached the region end without a full redefinition: be conservative
    }
}
