# Runtime copy contracts instead of private allocation rules

All three remaining private FlushRun tests now have **REPLACE** dispositions,
backed by existing discoverable Rust integration cases. This adds no duplicate
test cases and retires no production capability. The source contains14 lexical
assertion sites,15 assertions after expanding the two-pointer foreach. None of
these three methods measures a numeric cycle or ROM-size ceiling.

| Source method | Sites / expanded | Private expectation | Native replacement |
| --- | ---: | --- | --- |
| `Sm83/FlushRunLoopResidencyTests.FlushRun_DstAndSrcPointerPhisAreRegisterResident` | 5 / 6 | Exactly two pointer phis, distinct HL/DE allocations and register-set membership. | The same actual sample's authored BG/window data and sprites reach hardware correctly; map tests check every byte, both maps, bank preservation and bounded dirty-state drainage. |
| `Sm83/FlushRunLoopResidencyTests.FlushRun_CompiledLoopBodyUsesFusedAddressingNotPerIterationReload` | 6 / 6 | Successful C# backend/link, fused opcodes somewhere in the function and identified backedge, then fused opcodes within that range. | Roslyn DLL → native CLI → canonical ELF → Rust linker → real boot and framebuffer/timeline checks, with bounded startup/progress and no unsafe writes. |
| `Sm83/RedundantCompareLoopTests.RealFlushRun_LoopGuards_HaveNoRedundantCompareChain` | 3 / 3 | No compare-of-compare nodes, a particular pointer predicate and private IR verifier success. | Actual guarded copies progress to all required sample states without mode3 writes; complete map snapshots and clean-frame edits check guards and completion instead of prescribing a compare graph. |

Native owners:

- `driver::full_graphics_showcase_boots_and_animates`, four Debug/Release × DMG/CGB cases.
- `driver::bg_win::scattered_map_commit_drains_and_does_not_replay_clean_state`, four configurations, each executing BG and window ROMs.
- Existing mapper, alias, sequential-copy and byte-loop tests independently retain their own arithmetic/memory requirements; they are not counted again here.

## Why these replacements preserve useful coverage

The original residency tests read `samples/csharp/gb-gfx-demo/Game.cs`. The Rust
Showcase fixture compiles that same file directly, without a rewritten sample
or stand-in copy helper. It includes the real Koh.GameBoy reference and invokes
the production native executable. Its assertions check32 authored tile bytes,
all1024 background indices, window text, OAM, framebuffer output and both palette
themes. Every coherent completed phase through79 must appear in order; a wrong
address, permanently missing byte or stalled copy cannot satisfy those checks.
The DMG execution exercises CPU map copying; CGB also retains its DMA path.

The original methods did not execute a ROM. Opcode presence and a register-map
entry could pass even if emitted addressing or guards were wrong. A byte sequence
was not sufficient evidence of correct register aliases, data movement or safe
PPU timing. The Rust tests instead observe those effects. They do not require
the current private helper, a pointer-phi count or one fixed allocation policy
to survive future game-library/compiler improvements.

The independently useful bounds are the actual hardware-frame contracts:
boot handoff below600 frames, startup below2000, and the80-update timeline within
6000 frames, with zero mode3 writes. These existing budgets are unchanged.
Separate map acceptance preserves one-frame/one-GDMA CGB completion and safe
DMG drainage within61 frames, followed by a clean frame preserving external
hardware-map edits. Complete2048-byte map snapshots cover63 ordered checkpoints
per map ROM; VRAM/OAM/alternate-bank guards remain. These are product deadlines
and correctness bounds, not a claim of a particular private helper's cycles.

The initial audit proposed replacing private code shape with a new size/cycle
budget. The final choice uses the same sample's existing frame budgets and
physical write guards. Inventing an arbitrary function-size limit would freeze
another implementation detail. The separately inventoried190/740-dot microbenchmarks
retain their original numeric requirements: counted190 now passes; copy740 is
still open. No measured requirement is removed to close these private methods.

## Current verification

On88081af, the targeted eight cases passed8/8 in51.556s, exit0,647 explicitly
unselected (`/tmp/koh-flushrun-public-contracts.log`). Four sample ROMs execute
both ordinary and timeline observations; eight map ROMs cover BG/window on both
devices/builds. The broader944-case run is recorded in [byte-loop acceptance](byte-loop-selection.md).
This is local Linux evidence, not physical hardware or final cross-platform proof.
The ledger becomes564/565 complete (563 native replacements and one obsolete
private-parser DELETE decision), one pointer-copy timing method outstanding.
No legacy compiler/test definitions are physically removed yet.

```sh
cargo nextest run -p koh-compile --test driver --locked --no-fail-fast \
  -E 'test(full_graphics_showcase_boots_and_animates) | test(scattered_map_commit)'
```
