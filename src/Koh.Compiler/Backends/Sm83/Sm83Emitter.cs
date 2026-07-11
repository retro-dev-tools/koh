using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>A forward reference resolved to an absolute address once its target is placed.</summary>
internal sealed class Label
{
    public int Offset = -1;
}

/// <summary>A growable code buffer with block labels and absolute-address fixups.</summary>
internal sealed class Emitter
{
    public readonly List<byte> Code = [];
    private readonly Dictionary<IrBasicBlock, Label> _blocks = new(
        ReferenceEqualityComparer.Instance
    );
    private readonly Dictionary<string, Label> _routines = new(StringComparer.Ordinal);
    private readonly List<(int Pos, Label Target)> _fixups = [];

    /// <summary>Runtime helper routines referenced by generated code (emitted on demand).</summary>
    public readonly HashSet<string> NeededRoutines = new(StringComparer.Ordinal);

    /// <summary>Coalesced source-line ranges over the code section, for .kdbg debug info.</summary>
    public readonly List<LineMapEntry> LineMap = [];

    // Accumulator tracking for redundant-load elimination: the slot address A currently mirrors,
    // valid only while nothing has been emitted since (Code.Count unchanged). Any other emit
    // advances Code.Count and any label/branch clears it, so a skip is only ever taken when A
    // provably still holds the value.
    private int _aSlot = -1;
    private int _aValidCount = -1;

    public void U8(int value) => Code.Add((byte)value);

    /// <summary>Emit a 16-bit value little-endian (low byte then high) — the SM83 encoding for every
    /// immediate address and 16-bit immediate operand.</summary>
    public void U16(int value)
    {
        Code.Add((byte)(value & 0xFF));
        Code.Add((byte)(value >> 8));
    }

    /// <summary>Emit <c>LD A, (addr)</c>, unless A already holds that slot's value.</summary>
    public void LoadA(int addr)
    {
        if (_aSlot == addr && _aValidCount == Code.Count)
            return; // A already mirrors (addr); nothing ran since it was set
        Code.Add(0xFA);
        U16(addr);
        _aSlot = addr;
        _aValidCount = Code.Count;
    }

    /// <summary>Emit <c>LD (addr), A</c>; A now also mirrors that slot.</summary>
    public void StoreA(int addr)
    {
        Code.Add(0xEA);
        U16(addr);
        _aSlot = addr;
        _aValidCount = Code.Count;
    }

    /// <summary>Record that <paramref name="count"/> bytes at <paramref name="offset"/> came
    /// from one source line, extending the previous run when adjacent and identical.</summary>
    public void AddLineRange(int offset, int count, string file, uint line)
    {
        if (LineMap.Count > 0)
        {
            var last = LineMap[^1];
            if (last.Line == line && last.File == file && last.Offset + last.ByteCount == offset)
            {
                LineMap[^1] = last with { ByteCount = last.ByteCount + count };
                return;
            }
        }
        LineMap.Add(new LineMapEntry(offset, count, file, line));
    }

    public Label BlockLabel(IrBasicBlock block)
    {
        if (!_blocks.TryGetValue(block, out var label))
            _blocks[block] = label = new Label();
        return label;
    }

    private readonly Dictionary<IrFunction, Label> _funcs = new(ReferenceEqualityComparer.Instance);

    /// <summary>The label at a function's true entry (before any prologue), which is what a CALL
    /// targets — distinct from the entry basic block's label, which a recursive prologue precedes.</summary>
    public Label FunctionLabel(IrFunction fn)
    {
        if (!_funcs.TryGetValue(fn, out var label))
            _funcs[fn] = label = new Label();
        return label;
    }

    public Label RoutineLabel(string name)
    {
        if (!_routines.TryGetValue(name, out var label))
            _routines[name] = label = new Label();
        NeededRoutines.Add(name);
        return label;
    }

    private readonly Dictionary<IrFunction, Label> _thunks = new(
        ReferenceEqualityComparer.Instance
    );

    /// <summary>The ROM0 far-call thunk for a banked function: it switches to the callee's bank,
    /// CALLs it through the 0x4000 window, and restores the caller's bank on return.</summary>
    public Label ThunkLabel(IrFunction fn)
    {
        if (!_thunks.TryGetValue(fn, out var label))
            _thunks[fn] = label = new Label();
        return label;
    }

    public void PlaceRoutine(string name) => Place(RoutineLabel(name));

    public void Place(Label label)
    {
        label.Offset = Code.Count;
        _aValidCount = -1; // a label is a control-flow join; A's contents are unknown here
    }

    /// <summary>Emit a jump opcode plus a two-byte placeholder patched to the label's address.</summary>
    public void Jump(int opcode, Label target)
    {
        Code.Add((byte)opcode);
        CallTarget(target);
    }

    /// <summary>Emit a two-byte address placeholder (for a CALL/JP whose opcode was already emitted),
    /// patched to the label's absolute address at resolve time.</summary>
    public void CallTarget(Label target)
    {
        _fixups.Add((Code.Count, target));
        Code.Add(0);
        Code.Add(0);
        _aValidCount = -1; // control leaves; do not carry A across the branch
    }

    /// <summary>
    /// Run the flag-aware peephole over the just-emitted function region [<paramref name="start"/>,
    /// Code.Count) and relocate this region's labels, fixups, and line map for any bytes it removes.
    /// Safe to run per-function because nothing has been emitted after the region yet (so no later
    /// offset needs shifting) and the region's entry sits at <paramref name="start"/>, which never
    /// moves — so every cross-function reference (funcAddr, forward CALL fixups) stays valid.
    /// <paramref name="allowDeadStore"/> enables the dead-store rule; the backend passes false when the
    /// module has an interrupt handler, which could asynchronously read a stored slot (see
    /// <see cref="Sm83Peephole"/>).
    /// </summary>
    public void PeepholeFrom(int start, bool allowDeadStore)
    {
        int end = Code.Count;
        if (start >= end)
            return;

        // Every label whose target lands in this region — block/function/routine/thunk labels and the
        // anonymous branch-edge labels that EmitCondBr/EmitSwitch create (which live only in the fixup
        // list). All of these must relocate when bytes move, and all are join points for liveness.
        var labels = new HashSet<Label>(ReferenceEqualityComparer.Instance);
        void Collect<T>(Dictionary<T, Label> dict)
            where T : notnull
        {
            foreach (var label in dict.Values)
                if (label.Offset >= start && label.Offset <= end)
                    labels.Add(label);
        }
        Collect(_blocks);
        Collect(_funcs);
        Collect(_routines);
        Collect(_thunks);

        // Function labels whose target is a directly-returning entry — the only CALLs the tail-call fold
        // may touch. Interrupt handlers are excluded (their epilogue is RETI, not RET); a far-call thunk
        // (`_thunks`) or runtime routine (`_routines`) target is deliberately left out, so a banked callee
        // (reached via its thunk) is never folded.
        var safeFuncLabels = new HashSet<Label>(ReferenceEqualityComparer.Instance);
        foreach (var (fn, label) in _funcs)
            if (fn.InterruptVector is null)
                safeFuncLabels.Add(label);

        // One pass over the fixups feeds both: the anonymous branch-edge labels that live only in the fixup
        // list (which must relocate and are join points), and the CALL opcodes — one byte before their
        // operand fixup — that target a safe entry. The CALL opcode sits one byte before its fixup position.
        var tailCallSafeCalls = new HashSet<int>();
        foreach (var (pos, target) in _fixups)
        {
            if (target.Offset >= start && target.Offset <= end)
                labels.Add(target);
            if (pos - 1 >= start && pos - 1 < end && safeFuncLabels.Contains(target))
                tailCallSafeCalls.Add(pos - 1);
        }

        var boundaries = new HashSet<int>();
        foreach (var label in labels)
            boundaries.Add(label.Offset);

        var edits = Sm83Peephole.FindEdits(
            Code,
            start,
            end,
            boundaries,
            tailCallSafeCalls,
            allowDeadStore
        );
        if (edits.Count == 0)
            return;

        // Each edit optionally overwrites one opcode byte, then deletes a run of bytes (the next byte for a
        // two-into-one collapse, the trailing RET for a tail call, or a whole instruction for a dead store).
        // Overwrites are position-independent; the deleted positions are disjoint across edits but not
        // pre-sorted (the dead-store rule deletes an earlier offset), so gather then sort them.
        foreach (var edit in edits)
            if (edit.NewOpcode is { } opcode)
                Code[edit.Offset] = opcode;
        var deletes = edits
            .SelectMany(edit => Enumerable.Range(edit.DeleteStart, edit.DeleteCount))
            .ToArray();
        Array.Sort(deletes);

        // Number of deleted bytes strictly below an offset — its leftward shift.
        int DeletesBelow(int offset)
        {
            var idx = Array.BinarySearch(deletes, offset);
            return idx < 0 ? ~idx : idx;
        }
        int Remap(int offset) => offset - DeletesBelow(offset);

        foreach (var label in labels)
            label.Offset = Remap(label.Offset);

        for (var i = 0; i < _fixups.Count; i++)
            if (_fixups[i].Pos >= start && _fixups[i].Pos < end)
                _fixups[i] = (Remap(_fixups[i].Pos), _fixups[i].Target);

        // Shrink every line-map entry that covers deleted bytes. An entry can start before this region
        // yet extend into it (AddLineRange coalesces adjacent lines across the function boundary), so
        // its byte count must drop even when its offset stays put.
        for (var i = 0; i < LineMap.Count; i++)
        {
            var entry = LineMap[i];
            var shrink = DeletesBelow(entry.Offset + entry.ByteCount) - DeletesBelow(entry.Offset);
            if (shrink == 0 && Remap(entry.Offset) == entry.Offset)
                continue;
            LineMap[i] = entry with
            {
                Offset = Remap(entry.Offset),
                ByteCount = entry.ByteCount - shrink,
            };
        }

        for (var i = deletes.Length - 1; i >= 0; i--)
            Code.RemoveAt(deletes[i]);
        _aValidCount = -1; // A-tracking is invalidated by the rewrite
    }

    /// <summary>Patch every fixup to its target's absolute address. The buffer is partitioned into
    /// contiguous regions given as (bufferStart, base) pairs sorted by start; an offset in a region
    /// resolves to <c>base + (offset - bufferStart)</c>. ROM0 uses its physical base; each banked
    /// region uses the 0x4000 window base.</summary>
    public void Resolve(IReadOnlyList<(int Start, int Base)> regions)
    {
        int RegionBaseFor(int offset)
        {
            int best = regions[0].Base,
                bestStart = regions[0].Start;
            foreach (var (start, baseAddr) in regions)
                if (start <= offset && start >= bestStart)
                {
                    best = baseAddr;
                    bestStart = start;
                }
            return best + (offset - bestStart);
        }

        foreach (var (pos, target) in _fixups)
        {
            if (target.Offset < 0)
                throw new InvalidOperationException("unplaced jump target");
            int addr = RegionBaseFor(target.Offset);
            Code[pos] = (byte)(addr & 0xFF);
            Code[pos + 1] = (byte)(addr >> 8);
        }
    }
}
