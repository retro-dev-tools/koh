using Koh.Compiler.Ir;

namespace Koh.Compiler.Backends.Sm83;

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
            wram += p.Type.SizeInBytes;
        }

        // Permanent storage: alloca objects and constant-index geps (address-taken).
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
        {
            if (instr is PhiInstruction)
                phiTempBytes += instr.Type.SizeInBytes; // cycle-breaking may stage one temp per phi
            switch (instr)
            {
                case AllocaInstruction a:
                    staticAddr[a] = wram;
                    wram += a.Allocated.SizeInBytes;
                    break;
                case GetElementPtrInstruction g
                    when g.Index is IrConstInt ci
                        && staticAddr.TryGetValue(g.BasePointer, out int b):
                    staticAddr[g] = b + (int)ci.Value * g.ElementType.SizeInBytes;
                    break;
            }
        }

        // Colour the remaining SSA value slots (non-void results that aren't static addresses):
        // values whose live ranges never overlap share WRAM bytes.
        var colored = new HashSet<IrValue>(Eq);
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
            if (
                instr.Type.Kind != IrTypeKind.Void
                && instr is not AllocaInstruction
                && !staticAddr.ContainsKey(instr)
            )
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
            int size = value.Type.SizeInBytes;
            int start = colorBase;
            bool placed = false;
            while (!placed)
            {
                placed = true;
                foreach (var neighbour in interference[value])
                    if (
                        slot.TryGetValue(neighbour, out int ns)
                        && Overlaps(start, size, ns, neighbour.Type.SizeInBytes)
                    )
                    {
                        start = ns + neighbour.Type.SizeInBytes; // move past the conflict, then recheck
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
        IrFunction fn,
        HashSet<IrValue> colored
    )
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
                    d.Add(phi); // phis define at the top
                    blockPhis.Add(phi);
                }
            var defined = new HashSet<IrValue>(d, Eq);
            foreach (var instr in b.Instructions)
            {
                if (instr is PhiInstruction)
                    continue;
                foreach (var op in instr.Operands)
                    if (colored.Contains(op) && !defined.Contains(op))
                        u.Add(op); // used before defined in this block
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
                    foreach (var phi in phis[s]) // phi operands: live-out of the matching predecessor
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
                bool wideResult = colored.Contains(instr) && instr.Type.SizeInBytes >= 2;
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
