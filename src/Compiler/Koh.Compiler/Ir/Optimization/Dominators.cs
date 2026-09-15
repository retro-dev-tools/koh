namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// The dominator tree of a function's CFG and the dominance frontiers derived from it, used by
/// <see cref="Mem2RegPass"/>. Immediate dominators are computed with the Cooper–Harvey–Kennedy
/// iterative algorithm over reverse postorder — simple and fast on the small, reducible CFGs the
/// frontend produces.
/// </summary>
internal sealed class Dominators
{
    private readonly Dictionary<IrBasicBlock, IrBasicBlock> _idom;
    private readonly Dictionary<IrBasicBlock, int> _postNumber;
    private readonly Dictionary<IrBasicBlock, List<IrBasicBlock>> _preds;
    private readonly Dictionary<IrBasicBlock, List<IrBasicBlock>> _children;
    private readonly List<IrBasicBlock> _reversePostorder;
    private readonly IrBasicBlock _entry;

    public Dominators(IrFunction function)
    {
        _entry = function.EntryBlock!;
        _preds = Predecessors(function);

        var postorder = Postorder(_entry);
        _postNumber = new Dictionary<IrBasicBlock, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < postorder.Count; i++)
            _postNumber[postorder[i]] = i;
        postorder.Reverse(); // reverse-postorder in place — no extra allocation
        _reversePostorder = postorder;

        _idom = ComputeIdoms();
        _children = new Dictionary<IrBasicBlock, List<IrBasicBlock>>(
            ReferenceEqualityComparer.Instance
        );
        foreach (var block in _reversePostorder)
            if (!ReferenceEquals(block, _entry) && _idom.TryGetValue(block, out var parent))
            {
                _children.TryAdd(parent, []);
                _children[parent].Add(block);
            }
    }

    public IReadOnlyList<IrBasicBlock> ChildrenOf(IrBasicBlock block) =>
        _children.TryGetValue(block, out var kids) ? kids : [];

    /// <summary>The predecessors of <paramref name="block"/> in the CFG this instance was built over.
    /// The map is computed once at construction, so this is a lookup rather than a rescan.</summary>
    public IReadOnlyList<IrBasicBlock> PredecessorsOf(IrBasicBlock block) =>
        _preds.GetValueOrDefault(block, []);

    /// <summary>True if <paramref name="a"/> dominates <paramref name="b"/> — every path from the
    /// entry to <paramref name="b"/> passes through <paramref name="a"/> (a block dominates itself).
    /// Walks up the immediate-dominator chain from <paramref name="b"/> to the entry.</summary>
    public bool Dominates(IrBasicBlock a, IrBasicBlock b)
    {
        var runner = b;
        while (runner is not null)
        {
            if (ReferenceEquals(runner, a))
                return true;
            var next = _idom.GetValueOrDefault(runner);
            if (next is null || ReferenceEquals(next, runner))
                return false; // reached the entry without meeting `a`
            runner = next;
        }
        return false;
    }

    public Dictionary<IrBasicBlock, HashSet<IrBasicBlock>> DominanceFrontiers()
    {
        var frontier = new Dictionary<IrBasicBlock, HashSet<IrBasicBlock>>(
            ReferenceEqualityComparer.Instance
        );
        foreach (var block in _reversePostorder)
        {
            var preds = _preds.GetValueOrDefault(block, []);
            if (preds.Count < 2)
                continue;
            foreach (var pred in preds)
            {
                var runner = pred;
                while (
                    runner is not null
                    && !ReferenceEquals(runner, _idom.GetValueOrDefault(block))
                    && _postNumber.ContainsKey(runner)
                )
                {
                    frontier.TryAdd(
                        runner,
                        new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance)
                    );
                    frontier[runner].Add(block);
                    if (!_idom.TryGetValue(runner, out var next) || ReferenceEquals(next, runner))
                        break;
                    runner = next;
                }
            }
        }
        return frontier;
    }

    private Dictionary<IrBasicBlock, IrBasicBlock> ComputeIdoms()
    {
        var idom = new Dictionary<IrBasicBlock, IrBasicBlock>(ReferenceEqualityComparer.Instance)
        {
            [_entry] = _entry,
        };

        bool changed;
        do
        {
            changed = false;
            foreach (var block in _reversePostorder)
            {
                if (ReferenceEquals(block, _entry))
                    continue;

                IrBasicBlock? newIdom = null;
                foreach (var pred in _preds.GetValueOrDefault(block, []))
                {
                    if (!idom.ContainsKey(pred))
                        continue;
                    newIdom = newIdom is null ? pred : Intersect(pred, newIdom, idom);
                }

                if (
                    newIdom is not null
                    && (!idom.TryGetValue(block, out var cur) || !ReferenceEquals(cur, newIdom))
                )
                {
                    idom[block] = newIdom;
                    changed = true;
                }
            }
        } while (changed);

        return idom;
    }

    private IrBasicBlock Intersect(
        IrBasicBlock a,
        IrBasicBlock b,
        Dictionary<IrBasicBlock, IrBasicBlock> idom
    )
    {
        while (!ReferenceEquals(a, b))
        {
            while (_postNumber[a] < _postNumber[b])
                a = idom[a];
            while (_postNumber[b] < _postNumber[a])
                b = idom[b];
        }
        return a;
    }

    private static Dictionary<IrBasicBlock, List<IrBasicBlock>> Predecessors(IrFunction function)
    {
        var preds = new Dictionary<IrBasicBlock, List<IrBasicBlock>>(
            ReferenceEqualityComparer.Instance
        );
        foreach (var block in function.Blocks)
            if (block.Terminator is { } terminator)
                foreach (var successor in terminator.Successors)
                {
                    preds.TryAdd(successor, []);
                    preds[successor].Add(block);
                }
        return preds;
    }

    /// <summary>Iterative DFS postorder from entry (reachable blocks only).</summary>
    private static List<IrBasicBlock> Postorder(IrBasicBlock entry)
    {
        var visited = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { entry };
        var post = new List<IrBasicBlock>();
        var stack = new Stack<(IrBasicBlock Block, List<IrBasicBlock> Succ, int Index)>();
        stack.Push((entry, SuccessorsOf(entry), 0));

        while (stack.Count > 0)
        {
            var (block, succ, index) = stack.Pop();
            if (index < succ.Count)
            {
                stack.Push((block, succ, index + 1));
                var next = succ[index];
                if (visited.Add(next))
                    stack.Push((next, SuccessorsOf(next), 0));
            }
            else
            {
                post.Add(block);
            }
        }
        return post;
    }

    private static List<IrBasicBlock> SuccessorsOf(IrBasicBlock block) =>
        block.Terminator is { } terminator ? terminator.Successors.ToList() : [];
}
