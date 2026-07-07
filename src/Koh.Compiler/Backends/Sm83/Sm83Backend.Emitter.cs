using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>A forward reference resolved to an absolute address once its target is placed.</summary>
    private sealed class Label { public int Offset = -1; }

    /// <summary>A growable code buffer with block labels and absolute-address fixups.</summary>
    private sealed class Emitter
    {
        public readonly List<byte> Code = [];
        private readonly Dictionary<IrBasicBlock, Label> _blocks = new(ReferenceEqualityComparer.Instance);
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

        private readonly Dictionary<IrFunction, Label> _thunks = new(ReferenceEqualityComparer.Instance);

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
                int best = regions[0].Base, bestStart = regions[0].Start;
                foreach (var (start, baseAddr) in regions)
                    if (start <= offset && start >= bestStart) { best = baseAddr; bestStart = start; }
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

    /// <summary>
    /// The WRAM layout of one function's frame: fixed addresses for parameters and SSA values,
    /// compile-time addresses for <c>alloca</c>/constant-<c>gep</c> pointers, and scratch for
    /// phi-cycle breaking. Computed for the whole module before any code is emitted so a caller
    /// knows where to place a callee's arguments.
    /// </summary>
    internal sealed class FunctionAllocation
    {
        private static readonly ReferenceEqualityComparer Eq = ReferenceEqualityComparer.Instance;

        public required Dictionary<IrValue, int> Slot { get; init; }
        public required Dictionary<IrValue, int> StaticAddr { get; init; }
        public required int PhiTempBase { get; init; }
        public required int FrameBase { get; init; }
        public required int FrameEnd { get; init; }

        public static FunctionAllocation For(IrFunction fn, int baseAddr)
        {
            var staticAddr = new Dictionary<IrValue, int>(Eq);
            var slot = new Dictionary<IrValue, int>(Eq);
            int wram = baseAddr;
            int phiTempBytes = 0;

            // Parameters get fixed slots first: the caller writes them at these addresses, so they
            // are a stable ABI and are never reused by colouring.
            foreach (var p in fn.Parameters)
            {
                slot[p] = wram;
                wram += SizeOf(p.Type);
            }

            // Permanent storage: alloca objects and constant-index geps (address-taken).
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                {
                    if (instr is PhiInstruction)
                        phiTempBytes += SizeOf(instr.Type); // cycle-breaking may stage one temp per phi
                    switch (instr)
                    {
                        case AllocaInstruction a:
                            staticAddr[a] = wram;
                            wram += SizeOf(a.Allocated);
                            break;
                        case GetElementPtrInstruction g
                            when g.Index is IrConstInt ci && staticAddr.TryGetValue(g.BasePointer, out int b):
                            staticAddr[g] = b + (int)ci.Value * SizeOf(g.ElementType);
                            break;
                    }
                }

            // Colour the remaining SSA value slots (non-void results that aren't static addresses):
            // values whose live ranges never overlap share WRAM bytes.
            var colored = new HashSet<IrValue>(Eq);
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                    if (instr.Type.Kind != IrTypeKind.Void && instr is not AllocaInstruction
                        && !staticAddr.ContainsKey(instr))
                        colored.Add(instr);

            var interference = ComputeInterference(fn, colored);
            int colorBase = wram;
            int colorEnd = colorBase;

            var order = new List<IrValue>();
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                    if (colored.Contains(instr))
                        order.Add(instr);

            foreach (var value in order)
            {
                int size = SizeOf(value.Type);
                int start = colorBase;
                bool placed = false;
                while (!placed)
                {
                    placed = true;
                    foreach (var neighbour in interference[value])
                        if (slot.TryGetValue(neighbour, out int ns)
                            && Overlaps(start, size, ns, SizeOf(neighbour.Type)))
                        {
                            start = ns + SizeOf(neighbour.Type); // move past the conflict, then recheck
                            placed = false;
                            break;
                        }
                }
                slot[value] = start;
                colorEnd = Math.Max(colorEnd, start + size);
            }

            wram = colorEnd;
            int phiTempBase = wram;
            wram += phiTempBytes; // cycle-breaking stages at most one temp per phi, sized to its type

            return new FunctionAllocation
            {
                Slot = slot,
                StaticAddr = staticAddr,
                PhiTempBase = phiTempBase,
                FrameBase = baseAddr,
                FrameEnd = wram,
            };
        }

        private static bool Overlaps(int s1, int z1, int s2, int z2) => s1 < s2 + z2 && s2 < s1 + z1;

        /// <summary>
        /// Live-range interference over the coloured values via SSA liveness (phi operands are used
        /// on predecessor edges). Two values interfere if they can be simultaneously live.
        /// </summary>
        private static Dictionary<IrValue, HashSet<IrValue>> ComputeInterference(
            IrFunction fn, HashSet<IrValue> colored)
        {
            var blocks = fn.Blocks;
            var use = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
            var def = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
            var phis = new Dictionary<IrBasicBlock, List<PhiInstruction>>(Eq);

            foreach (var b in blocks)
            {
                var u = new HashSet<IrValue>(Eq);
                var d = new HashSet<IrValue>(Eq);
                var blockPhis = new List<PhiInstruction>();
                foreach (var instr in b.Instructions)
                    if (instr is PhiInstruction phi && colored.Contains(phi))
                    {
                        d.Add(phi);           // phis define at the top
                        blockPhis.Add(phi);
                    }
                var defined = new HashSet<IrValue>(d, Eq);
                foreach (var instr in b.Instructions)
                {
                    if (instr is PhiInstruction)
                        continue;
                    foreach (var op in instr.Operands)
                        if (colored.Contains(op) && !defined.Contains(op))
                            u.Add(op);        // used before defined in this block
                    if (colored.Contains(instr))
                    {
                        d.Add(instr);
                        defined.Add(instr);
                    }
                }
                use[b] = u;
                def[b] = d;
                phis[b] = blockPhis;
            }

            var liveIn = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
            var liveOut = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
            foreach (var b in blocks)
            {
                liveIn[b] = new HashSet<IrValue>(Eq);
                liveOut[b] = new HashSet<IrValue>(Eq);
            }
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = blocks.Count - 1; i >= 0; i--)
                {
                    var b = blocks[i];
                    var newOut = new HashSet<IrValue>(Eq);
                    foreach (var s in Successors(b))
                    {
                        foreach (var v in liveIn[s])
                            newOut.Add(v);
                        foreach (var phi in phis[s])                    // phi operands: live-out of the matching predecessor
                            foreach (var (val, pred) in phi.Incomings)
                                if (ReferenceEquals(pred, b) && colored.Contains(val))
                                    newOut.Add(val);
                    }
                    var newIn = new HashSet<IrValue>(use[b], Eq);
                    foreach (var v in newOut)
                        if (!def[b].Contains(v))
                            newIn.Add(v);
                    if (!newOut.SetEquals(liveOut[b]) || !newIn.SetEquals(liveIn[b]))
                    {
                        liveOut[b] = newOut;
                        liveIn[b] = newIn;
                        changed = true;
                    }
                }
            }

            var graph = new Dictionary<IrValue, HashSet<IrValue>>(Eq);
            foreach (var v in colored)
                graph[v] = new HashSet<IrValue>(Eq);
            void Interfere(IrValue a, IrValue b)
            {
                if (!ReferenceEquals(a, b))
                {
                    graph[a].Add(b);
                    graph[b].Add(a);
                }
            }

            foreach (var b in blocks)
            {
                var live = new HashSet<IrValue>(liveOut[b], Eq);
                for (int i = b.Instructions.Count - 1; i >= 0; i--)
                {
                    var instr = b.Instructions[i];
                    if (instr is PhiInstruction)
                        continue;
                    if (colored.Contains(instr))
                    {
                        foreach (var w in live)
                            Interfere(instr, w);
                        live.Remove(instr);
                    }
                    // A multi-byte result is emitted byte-by-byte in place (see EmitBinary/EmitConv/
                    // EmitShift), so it must not share a slot with any operand it reads: a partial
                    // overlap would clobber a source byte before it is read. Force disjointness by
                    // interfering the result with its colored operands. (An i8 result reads/writes a
                    // single byte, so full-coincidence or disjoint are both safe — leave it free to
                    // coalesce with a dying operand.)
                    bool wideResult = colored.Contains(instr) && SizeOf(instr.Type) >= 2;
                    foreach (var op in instr.Operands)
                        if (colored.Contains(op))
                        {
                            if (wideResult)
                                Interfere(instr, op);
                            live.Add(op);
                        }
                }
                var blockPhis = phis[b];
                foreach (var p in blockPhis)
                    foreach (var w in live)
                        Interfere(p, w);
                for (int i = 0; i < blockPhis.Count; i++)
                    for (int j = i + 1; j < blockPhis.Count; j++)
                        Interfere(blockPhis[i], blockPhis[j]);
            }

            return graph;
        }

        private static IEnumerable<IrBasicBlock> Successors(IrBasicBlock block) =>
            block.Terminator?.Successors ?? [];
    }
}
