# Loop cost investigation

`tests/sm83.rs::real_loop_cost_baselines` builds ordinary Release Roslyn CIL,
lowers and optimizes it, verifies the program, emits ELF through Rust, links and
executes through the existing emulator's `--cost` mode. Two rstest inputs select
`RewriteContracts.CountedCost` and `CopyCost`.

The counted fixture retains the original100 byte-counter iterations writing
C100 and independently reads back99. The pointer fixture retains the original
32-byte source initialization and two-pointer/decrementing-count copy. Its
observation loop ORs all32 mismatches against expected bytes0..31; zero requires
every byte to match. Both check balanced stack and the existing memory guard.

Only `Sm83LoopCodegenTests.Baseline_CountedConstAddrStoreLoop` (one assertion site)
is PORT complete here. That baseline had no performance assertion. The separate
`CountedConstAddrStoreLoop_MeetsCeiling` and `TwoPointerCopyLoop_MeetsCeiling`
remain **open**, with original190/740-dot ceilings unchanged. These measurements
are not substitutes for passing those requirements.

The measurement includes the native test wrapper, stack initialization, result
observation, return transport and completion sequence. The old test instead
executed its startup and returned beyond its code window. Therefore these totals
are investigative evidence, not equivalent timing windows or proof of an exact
ceiling violation. The extra copy observation loop is particularly material.
No old fall-through behavior is adopted as a runtime specification.

The first exploratory run measured71444 total T-cycles for the counted fixture
(714.440 divided by100), and158888 for a copy variant with a weaker sum-only
observer (4965.250 divided by32). The latter observer was replaced by the exact
per-byte mismatch check before acceptance; it is not the committed observation.
Log: `/tmp/koh-loop-cost.log`. These results motivate isolating the hot-loop
window and investigating repeated frame-slot traffic; they do not justify
raising either ceiling or choosing a legacy register allocator.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test sm83 --locked --success-output immediate \
  -E 'binary(koh_compile) | test(real_loop_cost_baselines) | test(machine_rewrites_preserve)'
```

The timing probes intentionally print measurements without claiming the still-open
ceiling assertions pass. They are discoverable native tests, not ignored tests,
an optional feature or a hidden success route around a failed timing assertion.

Final current observer results: counted71444 T-cycles (714.440 per iteration);
copy166056 (5189.250 per byte), including the overhead described above. Expanded
compiler-library/baseline/rewrite validation passed224/224 in30.101s, exit0,
207 explicitly unselected. Log: `/tmp/koh-loop-cost-final.log`. Formatting and
diff checks passed. No production compiler or emulator changes are included.
