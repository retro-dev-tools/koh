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
