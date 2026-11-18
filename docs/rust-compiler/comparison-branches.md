# Direct comparison branches

The SM83 backend can branch directly from a block's terminal comparison when its
Boolean result has exactly one use: that block's branch condition. Both operands
must have the same emitted storage width. Every instruction operand, edge
argument and terminator operand contributes to the use count. Other shapes retain
the existing materialized Boolean path, including escaped/shared predicates,
intervening operations and differing storage widths.

The existing comparison implementation is split into a shared jump-producing
routine and its Boolean-materialization wrapper. Signed/unsigned greater-than
still swaps operands; signed sign-bit handling and high-to-low comparison are
unchanged. Range-narrowed nonnegative signed values use the same unsigned rule
as the materializing path. Transfers to both successors still use existing
parallel edge copies, stack layout and relocations. No source operations move
across effects and no condition is recomputed after an intervening operation.

Four new rstest cases in `tests/sm83.rs` exercise32/64-bit operands, all five
predicates, equal/unequal/sign-bit/all-one inputs, and both direct and edge-escaped
Booleans. Both original and optimized IR execute:280 ROMs with independent Rust
predicate results. Existing comparison fixtures also retain shared results,
barriers, mixed conditions and narrowed8/16-bit values. Debug/Release real-CIL
rewrite fixtures retain their exact observed-access traces.

The initial compiler-library/comparison/rewrite selection passed229/229 in34.440s,
exit0,207 explicitly unselected (`/tmp/koh-fused-compare.log`). Marked Release
windows improve from68900 to55588 T-cycles for100 counted stores (689 to555.88,
19.32%), and83528 to75504 for32 initialized/copied bytes (2610.25 to2359.5,
9.61%). These are the same marked source windows, not compilation-time or
whole-game measurements. The original190/740-dot ceilings remain open.

This changes instruction selection, not the typed IR, public API or ABI. The
eliminated temporary has no other IR consumer; externally used Booleans remain
stored. Size checks and source/method provenance remain active. No additional
legacy disposition is closed by this optimization.

Full compiler-library/SM83 validation, including the new predicates and escaped
Boolean cases, passed440/440 in127.552s, exit0, zero skipped.
Log: `/tmp/koh-fused-compare-final.log`. Stable Rust formatting passed.

Real-C# int128/wide arithmetic and Portable PDB document/range/execution checks
passed12/12 in105.147s, exit0,510 explicitly unselected
(`/tmp/koh-fused-compare-integration.log`). These source-range checks are not
full interactive debugger or cross-platform acceptance. Fresh discovery1934
workspace/1757 compiler/zero ignored. Stable fmt and diff checks passed.
