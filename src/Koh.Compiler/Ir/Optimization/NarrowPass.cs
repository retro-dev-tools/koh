namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Demotes an i32 (or wider) arithmetic/bitwise/compare op back to i8/i16 when the demotion is
/// provably value-equivalent. This is the mirror image of C#'s usual arithmetic conversions:
/// <c>byte + byte</c> computes in <c>int32</c>, and <c>byte c = a + b;</c> doesn't even compile
/// without <c>(byte)(a + b)</c> — so every byte-typed assignment already ends in a <c>trunc</c>.
/// Without this pass, that intermediate <c>int32</c> promotion survives all the way to codegen and
/// 8-bit arithmetic on a CPU with no native 32-bit anything becomes an unrolled 4-byte-wide
/// instruction sequence (or, for mul/div/rem, an actual call to a generic width-N runtime routine).
///
/// Two independent rewrites share this file because they share the same "operand is a provable
/// extension of a narrower value" primitive, but they have different soundness arguments:
///
/// <b>Arithmetic/bitwise</b> (<see cref="IrBinaryOp.Add"/>/<see cref="IrBinaryOp.Sub"/>/
/// <see cref="IrBinaryOp.Mul"/>/<see cref="IrBinaryOp.And"/>/<see cref="IrBinaryOp.Or"/>/
/// <see cref="IrBinaryOp.Xor"/>) are bit-local: modular arithmetic and bitwise ops are ring/lattice
/// homomorphisms from "mod 2^N" down to "mod 2^w" for w &lt; N, so the low w bits of the wide result
/// depend ONLY on the low w bits of each operand — never on the bits above w, regardless of which
/// extension kind (zext or sext) put them there, and regardless of what the other operand's kind
/// is. So an operand qualifies as "representable at width w" if it's <em>either</em> kind of
/// extension from width w, or a compile-time constant (whose low w bits are always well-defined).
/// The one thing this identity does NOT license is discarding the wide computation unless nothing
/// else needs it at full width — hence the "every use is a trunc-to-w or another absorbed node"
/// requirement below, which is also what makes multi-operand chains (<c>(a+b)+c</c>) narrow as one
/// unit rather than only ever the single expression directly touching a <c>trunc</c>.
///
/// Shl and the division-family ops (UDiv/SDiv/URem/SRem/LShr/AShr) are deliberately excluded: a
/// shift's low bits depend on the shift amount in a way that can disagree between widths once the
/// amount reaches/exceeds w (an out-of-range narrow shift is undefined, where the wide shift simply
/// zeroed out), and div/rem/right-shift are not bit-local at all (they depend on the operand's high
/// bits). Demoting any of these would be a miscompile, not a missed optimization.
///
/// <b>Compare</b> is a different — and more dangerous — argument, because CIL's evaluation stack
/// always widens sub-int32 values to i32 (ECMA-335 III.1.1), so a source-level unsigned byte
/// comparison always surfaces as a *signed* i32 <c>Slt</c>-family compare over a *zero-extended*
/// operand. Demoting that back to an i8 comparison by literally keeping the <c>Slt</c> predicate
/// would be a real miscompile: e.g. comparing bytes 200 and 50, <c>Slt(zext(200), zext(50))</c> at
/// i32 is <c>200 &lt; 50 = false</c> (zext keeps both non-negative), but naively re-running
/// <c>Slt</c> at i8 reads 200 as the signed value -56, giving <c>-56 &lt; 50 = true</c> — the wrong
/// answer. The correct rule (proved in the class remarks below <see cref="MapComparePredicate"/>):
/// keep <see cref="IrCompareOp.Eq"/>/<see cref="IrCompareOp.Ne"/> and the unsigned predicates
/// unchanged for either extension kind (both zext and sext happen to preserve narrow *unsigned*
/// ordering), but remap a *signed* predicate to its unsigned counterpart when the operands were
/// zero-extended (an unsigned source type promoted through CIL's always-signed i32 compare) — and
/// only ever demote when both operands share the same extension kind, since mixing kinds breaks
/// every one of these identities.
/// </summary>
public sealed class NarrowPass : IIrFunctionPass
{
    /// <summary>Ops whose low-w-bit result depends only on operands' low-w bits (see class remarks).
    /// Shl and the division family are excluded — see class remarks for why.</summary>
    private static readonly HashSet<IrBinaryOp> BitLocalOps =
    [
        IrBinaryOp.Add,
        IrBinaryOp.Sub,
        IrBinaryOp.Mul,
        IrBinaryOp.And,
        IrBinaryOp.Or,
        IrBinaryOp.Xor,
    ];

    /// <summary>Target widths this pass demotes to — the SM83's native register widths.</summary>
    private static readonly int[] TargetWidths = [8, 16];

    public bool Run(IrFunction function)
    {
        var changed = false;
        foreach (var width in TargetWidths)
            changed |= RunForWidth(function, width);
        // A demoted node's now-orphaned wide predecessors (evicted from their block slot) and the
        // truncs this pass removed leave newly-dead instructions (e.g. a zext whose only remaining
        // use was the trunc this pass just deleted); DeadCodeEliminationPass cleans those up as part
        // of the fixed-point loop in IrOptimizer, not here.
        changed |= RunCompares(function);
        return changed;
    }

    // ---- Arithmetic/bitwise chain demotion ------------------------------------------------------

    private static bool RunForWidth(IrFunction function, int width)
    {
        // Fresh per width: a demotion at width 8 changes block contents, and width 16's own
        // roots/uses/positions must reflect that rather than a stale snapshot from before.
        var uses = BuildUses(function);
        var positions = BuildPositions(function);

        var roots = new List<ConvInstruction>();
        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
            if (
                instruction is ConvInstruction { Op: IrConvOp.Trunc } t
                && t.Type.Kind == IrTypeKind.Int
                && t.Type.Bits == width
                && t.Operand is BinaryInstruction root
                && BitLocalOps.Contains(root.Op)
                && root.Type.Kind == IrTypeKind.Int
                && root.Type.Bits > width
            )
                roots.Add(t);

        if (roots.Count == 0)
            return false;

        var buildMemo = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
        var rewrites = new List<(ConvInstruction Trunc, IrValue Narrow)>();

        foreach (var trunc in roots)
        {
            var root = (BinaryInstruction)trunc.Operand;
            if (buildMemo.TryGetValue(root, out var already))
            {
                rewrites.Add((trunc, already));
                continue;
            }

            var reach = CollectReach(root, width, uses);
            if (reach is null)
                continue; // this chain has an escaping use or an unsupported operand shape

            var narrow = BuildNarrow(root, width, reach, buildMemo, positions);
            rewrites.Add((trunc, narrow));
        }

        if (rewrites.Count == 0)
            return false;

        foreach (var (trunc, narrow) in rewrites)
        {
            IrOptimizer.ReplaceAllUses(function, trunc, narrow);
            trunc.Parent?.Instructions.Remove(trunc);
        }
        return true;
    }

    /// <summary>
    /// Backward-walk <paramref name="root"/>'s operand tree (through <see cref="BitLocalOps"/>
    /// binaries only) to find every wide node that would need to be absorbed into a width-
    /// <paramref name="width"/> replacement, then verify the whole set is safe to fully replace:
    /// every use of every node in the set is either a trunc to exactly <paramref name="width"/>
    /// (which needs nothing but the low bits, by construction) or another node in the set (an
    /// internal edge that disappears once both ends are rebuilt narrow). If anything in the set
    /// escapes to a consumer that isn't one of those two shapes, the whole set is rejected — a
    /// partial narrowing would either leave a needed wide value undefined or silently duplicate
    /// work, so this is all-or-nothing per root (see class remarks: "if in doubt, do not demote").
    /// Returns null when the chain doesn't qualify.
    /// </summary>
    private static HashSet<BinaryInstruction>? CollectReach(
        BinaryInstruction root,
        int width,
        Dictionary<IrValue, List<IrInstruction>> uses
    )
    {
        var reach = new HashSet<BinaryInstruction>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<BinaryInstruction>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!reach.Add(node))
                continue;
            foreach (var operand in new[] { node.Left, node.Right })
            {
                if (IsNarrowLeaf(operand, width))
                    continue;
                if (
                    operand is BinaryInstruction inner
                    && BitLocalOps.Contains(inner.Op)
                    && inner.Type.Kind == IrTypeKind.Int
                    && inner.Type.Bits > width
                )
                {
                    stack.Push(inner);
                    continue;
                }
                // Operand is neither a matching extension/constant leaf nor a chainable binary:
                // we have no way to produce its low `width` bits without inserting new code, which
                // this pass deliberately never does (see class remarks).
                return null;
            }
        }

        foreach (var node in reach)
        foreach (var use in uses.GetValueOrDefault(node) ?? [])
        {
            if (use is ConvInstruction { Op: IrConvOp.Trunc } t && t.Type.Bits == width)
                continue;
            if (use is BinaryInstruction b && reach.Contains(b))
                continue;
            return null; // escapes to a consumer that still needs the wide value
        }

        return reach;
    }

    /// <summary>An operand this pass can read a width-<paramref name="width"/> value out of without
    /// emitting new code: a zext/sext whose source is already exactly that width (the low bits are
    /// the source verbatim, regardless of extension kind — see class remarks), or a constant
    /// (whose low bits are truncated in place).</summary>
    private static bool IsNarrowLeaf(IrValue v, int width) =>
        v is IrConstInt
        || (
            v is ConvInstruction { Op: IrConvOp.ZExt or IrConvOp.SExt } c
            && c.Operand.Type.Kind == IrTypeKind.Int
            && c.Operand.Type.Bits == width
        );

    /// <summary>Construct the width-<paramref name="width"/> replacement for <paramref name="v"/>,
    /// splicing each newly built binary into the exact block slot its wide original occupied (so
    /// dominance/ordering is inherited for free, exactly as <see cref="StrengthReductionPass"/>
    /// does for its in-place rewrites). Memoized so a node shared by two roots (or reached twice in
    /// one root's tree) is only rebuilt once.</summary>
    private static IrValue BuildNarrow(
        IrValue v,
        int width,
        HashSet<BinaryInstruction> reach,
        Dictionary<IrValue, IrValue> memo,
        Dictionary<IrInstruction, (IrBasicBlock Block, int Index)> positions
    )
    {
        if (memo.TryGetValue(v, out var cached))
            return cached;

        IrValue result;
        switch (v)
        {
            case IrConstInt c:
                result = new IrConstInt(IrType.Int(width), IntWidth.Wrap(c.Value, width));
                break;
            case ConvInstruction { Op: IrConvOp.ZExt or IrConvOp.SExt } conv:
                result = conv.Operand; // already exactly `width` bits — reuse verbatim
                break;
            case BinaryInstruction b when reach.Contains(b):
                var left = BuildNarrow(b.Left, width, reach, memo, positions);
                var right = BuildNarrow(b.Right, width, reach, memo, positions);
                var replacement = new BinaryInstruction(b.Op, left, right) { Source = b.Source };
                if (positions.TryGetValue(b, out var pos))
                {
                    replacement.Parent = pos.Block;
                    pos.Block.Instructions[pos.Index] = replacement;
                }
                result = replacement;
                break;
            default:
                // CollectReach only ever adds a node whose operands are IsNarrowLeaf or a chained
                // BinaryInstruction it also verified — this shape is unreachable in a correct call.
                throw new InvalidOperationException(
                    $"NarrowPass.BuildNarrow: unexpected operand shape {v.GetType().Name}"
                );
        }

        memo[v] = result;
        return result;
    }

    // ---- Compare demotion ------------------------------------------------------------------------

    private static bool RunCompares(IrFunction function)
    {
        var changed = false;
        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                if (block.Instructions[i] is not CompareInstruction c)
                    continue;
                var replacement = TryDemoteCompare(c);
                if (replacement is null)
                    continue;
                replacement.Parent = block;
                replacement.Source = c.Source;
                block.Instructions[i] = replacement;
                IrOptimizer.ReplaceAllUses(function, c, replacement);
                changed = true;
            }
        }
        return changed;
    }

    private static CompareInstruction? TryDemoteCompare(CompareInstruction c)
    {
        if (c.Left.Type.Kind != IrTypeKind.Int)
            return null;
        var wide = c.Left.Type.Bits;

        var leftExt = TryGetExtension(c.Left, out var leftWidth, out var leftZext);
        var rightExt = TryGetExtension(c.Right, out var rightWidth, out var rightZext);

        int width;
        bool zext;
        if (leftExt && rightExt)
        {
            if (leftWidth != rightWidth || leftZext != rightZext)
                return null; // mixed kind/width breaks every identity below — see class remarks
            (width, zext) = (leftWidth, leftZext);
        }
        else if (leftExt)
        {
            (width, zext) = (leftWidth, leftZext);
        }
        else if (rightExt)
        {
            (width, zext) = (rightWidth, rightZext);
        }
        else
        {
            return null; // no anchor to determine a target width from
        }

        if ((width != 8 && width != 16) || width >= wide)
            return null;

        var newLeft = leftExt
            ? ((ConvInstruction)c.Left).Operand
            : NarrowConstant(c.Left, width, zext);
        var newRight = rightExt
            ? ((ConvInstruction)c.Right).Operand
            : NarrowConstant(c.Right, width, zext);
        if (newLeft is null || newRight is null)
            return null;

        return new CompareInstruction(MapComparePredicate(c.Op, zext), newLeft, newRight);
    }

    private static bool TryGetExtension(IrValue v, out int width, out bool zext)
    {
        if (v is ConvInstruction { Op: IrConvOp.ZExt } z)
        {
            width = z.Operand.Type.Bits;
            zext = true;
            return true;
        }
        if (v is ConvInstruction { Op: IrConvOp.SExt } s)
        {
            width = s.Operand.Type.Bits;
            zext = false;
            return true;
        }
        width = 0;
        zext = false;
        return false;
    }

    /// <summary>A constant operand qualifies only if it's genuinely representable as an extension of
    /// the given kind at <paramref name="width"/> bits — unlike the bit-local arithmetic case, a
    /// compare's low bits are NOT all that matters (the whole value's ordering is), so an
    /// out-of-range constant (e.g. comparing a zero-extended byte against 300) must block the
    /// demotion rather than silently truncating away the bits that make the comparison meaningful.</summary>
    private static IrValue? NarrowConstant(IrValue v, int width, bool zext)
    {
        if (v is not IrConstInt c)
            return null;
        var wideBits = c.Type.Bits;
        var wideUnsigned = IntWidth.ToUnsigned(c.Value, wideBits);
        if (zext)
        {
            if (wideUnsigned >= 1UL << width)
                return null;
            return new IrConstInt(IrType.Int(width), (long)wideUnsigned);
        }
        var narrowBits = wideUnsigned & IntWidth.Mask(width);
        var reExtended = IntWidth.ToUnsigned(IntWidth.ToSigned((long)narrowBits, width), wideBits);
        if (reExtended != wideUnsigned)
            return null;
        return new IrConstInt(IrType.Int(width), (long)narrowBits);
    }

    /// <summary>
    /// The predicate to use at the narrow width, given the (shared) extension kind of both operands.
    /// Both zext and sext preserve narrow *unsigned* ordering under a wide unsigned compare — for
    /// zext this is immediate (its unsigned value never changes); for sext it holds because sign-
    /// extending a negative narrow value adds the same constant offset <c>2^wide - 2^width</c> to
    /// its unsigned reading whenever its sign bit is set, which is a strictly monotonic function of
    /// the original unsigned value across the whole domain (the "negative" half all lands above the
    /// "non-negative" half, in the same relative order). So <see cref="IrCompareOp.Ult"/>/<see
    /// cref="IrCompareOp.Ule"/>/<see cref="IrCompareOp.Ugt"/>/<see cref="IrCompareOp.Uge"/> demote
    /// unchanged either way. Signed ordering is preserved by sext directly (that's what sign
    /// extension means), so a signed predicate demotes unchanged under sext — but under zext the
    /// wide values are always non-negative, so a wide *signed* compare over zext operands is really
    /// an unsigned compare in disguise, and must remap to the unsigned predicate or it silently
    /// reinterprets high bytes (0x80-0xFF) of the narrow value as negative (see class remarks for
    /// the worked 200-vs-50 counterexample this remap exists to avoid).
    /// </summary>
    private static IrCompareOp MapComparePredicate(IrCompareOp op, bool zext) =>
        op switch
        {
            IrCompareOp.Eq => IrCompareOp.Eq,
            IrCompareOp.Ne => IrCompareOp.Ne,
            IrCompareOp.Ult or IrCompareOp.Ule or IrCompareOp.Ugt or IrCompareOp.Uge => op,
            IrCompareOp.Slt => zext ? IrCompareOp.Ult : IrCompareOp.Slt,
            IrCompareOp.Sle => zext ? IrCompareOp.Ule : IrCompareOp.Sle,
            IrCompareOp.Sgt => zext ? IrCompareOp.Ugt : IrCompareOp.Sgt,
            IrCompareOp.Sge => zext ? IrCompareOp.Uge : IrCompareOp.Sge,
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

    // ---- Shared helpers ---------------------------------------------------------------------------

    private static Dictionary<IrValue, List<IrInstruction>> BuildUses(IrFunction function)
    {
        var uses = new Dictionary<IrValue, List<IrInstruction>>(ReferenceEqualityComparer.Instance);
        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
        foreach (var operand in instruction.Operands)
        {
            if (!uses.TryGetValue(operand, out var list))
                uses[operand] = list = [];
            list.Add(instruction);
        }
        return uses;
    }

    private static Dictionary<IrInstruction, (IrBasicBlock, int)> BuildPositions(
        IrFunction function
    )
    {
        var positions = new Dictionary<IrInstruction, (IrBasicBlock, int)>(
            ReferenceEqualityComparer.Instance
        );
        foreach (var block in function.Blocks)
            for (var i = 0; i < block.Instructions.Count; i++)
                positions[block.Instructions[i]] = (block, i);
        return positions;
    }
}
