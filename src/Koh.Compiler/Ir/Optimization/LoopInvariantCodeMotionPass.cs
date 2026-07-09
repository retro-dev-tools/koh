namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Loop-invariant code motion: hoists a pure computation whose operands do not change across a loop
/// out of the loop body into a preheader that runs once before the loop. On the SM83 — where the
/// backend spills every SSA value to WRAM and address arithmetic (<c>gep</c>) and strength-reduced
/// shifts are open-coded — moving an invariant expression out of a hot inner loop removes it from
/// every iteration, which is one of the larger wins available to a register-poor accumulator machine.
///
/// Only value-pure, side-effect-free, non-trapping instructions are candidates — <c>binary</c>,
/// <c>icmp</c>, <c>conv</c>, <c>gep</c> (the same set <see cref="LocalCsePass"/> treats as movable).
/// <c>load</c>/<c>store</c>/<c>call</c>/<c>intrinsic</c> are excluded (memory or observable effects),
/// as are <c>alloca</c> (names storage) and <c>phi</c>. Because a candidate is pure it is always safe
/// to speculate into the preheader, and because the preheader dominates every block of the loop the
/// move keeps every original use dominated by its definition.
///
/// A natural loop is found from a back edge <c>tail → header</c> where the header dominates the tail.
/// Hoisting targets the loop's preheader — the single loop-external predecessor of the header — which
/// is reused if one already exists and otherwise synthesised by splicing a new block onto that edge.
/// Loops without a single external entry edge (or whose header is the function entry) are left alone,
/// which is conservative but always correct; the frontend's structured control flow yields a single
/// entry edge in the common case.
/// </summary>
public sealed class LoopInvariantCodeMotionPass : IIrFunctionPass
{
    private static readonly ReferenceEqualityComparer Eq = ReferenceEqualityComparer.Instance;

    public bool Run(IrFunction function)
    {
        if (function.Blocks.Count < 2)
            return false;

        var dom = new Dominators(function);
        var changed = false;

        // Process each natural loop independently. Nested loops are handled across repeated runs of
        // the optimizer: a value hoisted to an inner preheader becomes a candidate for the enclosing
        // loop on the next round, so it migrates outward one level at a time.
        foreach (var loop in FindNaturalLoops(function, dom))
        {
            var preheader = GetOrCreatePreheader(function, loop, dom);
            if (preheader is null)
                continue; // no single external entry edge — skip this loop, conservatively

            if (HoistInvariants(loop, preheader))
            {
                changed = true;
                // The CFG changed (a preheader may have been spliced in); recompute dominance before
                // touching another loop so back-edge and dominance queries stay accurate.
                dom = new Dominators(function);
            }
        }
        return changed;
    }

    /// <summary>One natural loop: its header and the set of blocks it contains.</summary>
    private sealed record Loop(IrBasicBlock Header, HashSet<IrBasicBlock> Blocks);

    /// <summary>Discover natural loops from back edges, merging edges that share a header.</summary>
    private static List<Loop> FindNaturalLoops(IrFunction function, Dominators dom)
    {
        var byHeader = new Dictionary<IrBasicBlock, HashSet<IrBasicBlock>>(Eq);
        foreach (var tail in function.Blocks)
        {
            if (tail.Terminator is not { } terminator)
                continue;
            foreach (var header in terminator.Successors)
            {
                // A back edge is one whose target dominates its source.
                if (!dom.Dominates(header, tail))
                    continue;
                if (!byHeader.TryGetValue(header, out var body))
                    byHeader[header] = body = new HashSet<IrBasicBlock>(Eq) { header };
                CollectLoopBody(tail, header, body);
            }
        }
        return byHeader.Select(kv => new Loop(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Add to <paramref name="body"/> every block that reaches <paramref name="tail"/>
    /// without passing through the header — the standard natural-loop body walk over predecessors.</summary>
    private static void CollectLoopBody(
        IrBasicBlock tail,
        IrBasicBlock header,
        HashSet<IrBasicBlock> body
    )
    {
        var preds = Predecessors(header.Parent);
        var work = new Stack<IrBasicBlock>();
        if (body.Add(tail))
            work.Push(tail);
        while (work.Count > 0)
        {
            var block = work.Pop();
            if (ReferenceEquals(block, header))
                continue;
            foreach (var pred in preds.GetValueOrDefault(block, []))
                if (body.Add(pred))
                    work.Push(pred);
        }
    }

    /// <summary>Find or synthesise the loop's preheader: the unique loop-external predecessor of the
    /// header, through which control enters the loop. Returns null when there isn't exactly one
    /// external entry edge or the header is the function entry — cases this pass declines to handle.</summary>
    private static IrBasicBlock? GetOrCreatePreheader(
        IrFunction function,
        Loop loop,
        Dominators dom
    )
    {
        var header = loop.Header;
        if (ReferenceEquals(header, function.EntryBlock))
            return null; // an entry-header has no external predecessor block to hoist into

        var preds = Predecessors(function).GetValueOrDefault(header, []);
        var external = preds.Where(p => !loop.Blocks.Contains(p)).ToList();
        if (external.Count != 1)
            return null; // irreducible entry or multiple entry edges — leave the loop alone

        var entry = external[0];

        // Reuse an existing preheader: a block whose only successor is the header. Splicing one in
        // every run would otherwise grow the CFG without bound across optimizer fixed-point rounds.
        if (
            entry.Terminator is BrInstruction { Target: var t }
            && ReferenceEquals(t, header)
            && dom.Dominates(entry, header)
        )
            return entry;

        // Splice a fresh preheader P onto the entry edge: entry now branches to P, P branches to the
        // header, and the header's phi incomings that arrived from `entry` are rerouted through P.
        var preheader = new IrBasicBlock(function)
        {
            Name = NextName(function, header, "preheader"),
        };
        preheader.Instructions.Add(new BrInstruction(header) { Parent = preheader });
        function.Blocks.Insert(function.Blocks.IndexOf(header), preheader);

        RedirectTerminator(entry, header, preheader);
        RerouteHeaderPhis(header, entry, preheader);
        return preheader;
    }

    /// <summary>Move every loop-invariant instruction into <paramref name="preheader"/>, ahead of its
    /// terminator. Invariants are grown to a fixed point: an instruction is invariant when all its
    /// operands are constants, defined outside the loop, or themselves already hoisted.</summary>
    private static bool HoistInvariants(Loop loop, IrBasicBlock preheader)
    {
        // A value is "available in the preheader" if it isn't defined inside the loop, or it has been
        // moved there. Seed with everything defined outside the loop; grow as we hoist.
        var available = new HashSet<IrValue>(Eq);
        var moved = false;
        bool progress;
        do
        {
            progress = false;
            foreach (var block in loop.Blocks)
            {
                if (ReferenceEquals(block, preheader))
                    continue;
                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    var instruction = block.Instructions[i];
                    if (!IsHoistable(instruction))
                        continue;
                    if (!instruction.Operands.All(op => IsAvailable(op, loop, available)))
                        continue;

                    block.Instructions.RemoveAt(i);
                    i--;
                    instruction.Parent = preheader;
                    // Insert before the preheader's terminator so it still ends in a branch.
                    preheader.Instructions.Insert(preheader.Instructions.Count - 1, instruction);
                    available.Add(instruction);
                    moved = true;
                    progress = true;
                }
            }
        } while (progress);
        return moved;
    }

    /// <summary>Pure, non-trapping, side-effect-free instructions that are safe to speculate.</summary>
    private static bool IsHoistable(IrInstruction instruction) =>
        instruction
            is BinaryInstruction
                or CompareInstruction
                or ConvInstruction
                or GetElementPtrInstruction;

    /// <summary>An operand is available in the preheader if it is not defined inside the loop, or it
    /// is an instruction already hoisted there.</summary>
    private static bool IsAvailable(IrValue operand, Loop loop, HashSet<IrValue> available)
    {
        if (available.Contains(operand))
            return true;
        // Non-instruction operands (constants, params, globals) are always available. An instruction
        // operand is available only if its defining block is outside the loop.
        if (operand is not IrInstruction def)
            return true;
        return def.Parent is not { } parent || !loop.Blocks.Contains(parent);
    }

    // ---- CFG plumbing --------------------------------------------------------

    private static Dictionary<IrBasicBlock, List<IrBasicBlock>> Predecessors(IrFunction function)
    {
        var preds = new Dictionary<IrBasicBlock, List<IrBasicBlock>>(Eq);
        foreach (var block in function.Blocks)
            if (block.Terminator is { } terminator)
                foreach (var successor in terminator.Successors)
                {
                    preds.TryAdd(successor, []);
                    preds[successor].Add(block);
                }
        return preds;
    }

    /// <summary>Rebuild <paramref name="block"/>'s terminator with every edge to <paramref name="from"/>
    /// retargeted to <paramref name="to"/>. Terminator targets are immutable, so the instruction is
    /// replaced in place, preserving its source position.</summary>
    private static void RedirectTerminator(IrBasicBlock block, IrBasicBlock from, IrBasicBlock to)
    {
        var old = block.Instructions[^1];
        IrInstruction replacement = old switch
        {
            BrInstruction => new BrInstruction(to),
            CondBrInstruction c => new CondBrInstruction(
                c.Condition,
                ReferenceEquals(c.IfTrue, from) ? to : c.IfTrue,
                ReferenceEquals(c.IfFalse, from) ? to : c.IfFalse
            ),
            SwitchInstruction s => new SwitchInstruction(
                s.Value,
                ReferenceEquals(s.Default, from) ? to : s.Default,
                s.Cases.Select(c => (c.Case, ReferenceEquals(c.Target, from) ? to : c.Target))
                    .ToList()
            ),
            _ => old,
        };
        replacement.Parent = block;
        replacement.Source = old.Source;
        block.Instructions[^1] = replacement;
    }

    /// <summary>Reroute the header's phi incomings that arrive from <paramref name="from"/> so they
    /// arrive from <paramref name="through"/> instead — the header's predecessor set just changed
    /// from {from, latches…} to {through, latches…}.</summary>
    private static void RerouteHeaderPhis(
        IrBasicBlock header,
        IrBasicBlock from,
        IrBasicBlock through
    )
    {
        foreach (var instruction in header.Instructions)
        {
            if (instruction is not PhiInstruction phi)
                continue;
            var incoming = phi.Incomings.Where(i => ReferenceEquals(i.Block, from)).ToList();
            if (incoming.Count == 0)
                continue;
            phi.RemoveIncomingsFrom(from);
            foreach (var (value, _) in incoming)
                phi.AddIncoming(value, through);
        }
    }

    private static string NextName(IrFunction function, IrBasicBlock near, string hint)
    {
        var baseName = (near.Name ?? "loop") + "." + hint;
        var name = baseName;
        var n = 0;
        while (function.Blocks.Any(b => b.Name == name))
            name = baseName + "." + ++n;
        return name;
    }
}
