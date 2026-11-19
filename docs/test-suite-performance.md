# Test-suite performance

The comparison keeps the same tests, Debug/Release C# fixtures, hardware modes,
debug assertions, overflow checks and zero-retry policy. Roslyn tests remain
serialized because their referenced projects share build outputs.

## Baseline

PR62 [run 44605073292](https://github.com/retro-dev-tools/koh/actions/runs/44605073292)
completed 2,480 native tests on both Linux and macOS.
Linux test execution took 1,282 seconds; compiler tests accounted for 1,293
seconds of summed test duration. Other components accounted for 85 seconds.
Summed durations can exceed wall time because non-Roslyn tests run concurrently.

Cold publication and Cargo compilation are separate from these execution times.
The local cold GPUI release build alone took 25m55s; it is not test execution.

## Approach

Measure representative compiler execution cases before changing the test profile.
Optimize only the hot native workspace packages rather than all GPUI dependencies.
Keep Rust development builds unchanged. `just test` builds the release fixture
host before running tests and sets `KOH_TEST_HOST` for compiler fixtures. The
verification phase uses the same release target. Direct Cargo runs retain the
isolated, locked fallback build. Missing explicit host paths fail rather than
silently triggering another build.

Increasing Roslyn parallelism is deferred until fixture build outputs are isolated.
Removing cases or reducing emulator frames would change coverage and is excluded.

## Verification

Eight driver cases cover checked arithmetic and binary64 operations in both C#
configurations and both hardware modes. Each run uses a fresh nextest run ID,
so Roslyn fixtures rebuild once per configuration. Cargo and NuGet dependencies
and the native host were already built; there was no removal of test work.

Run the following once to warm the build, then three times for comparison:

```sh
cargo +stable nextest run -p koh-compile --test driver --locked \
  -E 'test(checked_arithmetic::) | test(float64::roslyn_binary64)'
```

| Test profile | Run 1 | Run 2 | Run 3 | Median |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 59.314s | 60.953s | 57.930s | 59.314s |
| Selected packages at opt-level 2 | 29.922s | 29.143s | 29.082s | 29.143s |

That is a 50.9% reduction in this representative execution workload, not a
whole-CI speedup claim. The first optimized rebuild took 2m08s; the initial
baseline build took 1m11s including dependencies, so these cold build times
are not directly comparable. Both measurements used the same Linux machine,
Rust 1.100.0, SDK 10.0.115, an Intel i7-6820HQ, one Cargo build job and no
incremental compilation.
A separate existing acceptance run was active throughout the comparison.

Only `koh-compile`, `koh-link`, `koh-objects` and `koh-emulator` are optimized
in the test profile. Debug information, assertions and overflow checks retain
their test defaults. Production and ordinary development profiles are unchanged.

The host override also passed a real debugger test with Cargo deliberately
unavailable. A missing override failed with `KOH_TEST_HOST missing`, and the
ordinary no-override Cargo fallback passed the same case.

The affected suites completed with **2,221 passed, zero failed**, exit 0,
in 1,146.801 seconds, using the prebuilt host:

```sh
KOH_TEST_HOST="$PWD/target/release/koh-test-host" cargo +stable nextest run \
  -p koh-compile -p koh-emulator -p koh-link -p koh-objects -p koh-test-host \
  --all-features --locked --no-fail-fast -E 'not test(sdk_dotnet_run)'
```

The single local exclusion republishes the full GPUI desktop. It remains in
the unchanged CI matrix; this result is not a full `just ci` result. Both SDK
incremental-output cases and both source-debugger SDK cases passed. Cold native
SDK compilation still dominates those cases (about 85 seconds each here).

Affected-crate doctests completed successfully (no examples discovered).
Formatting, `git diff --check` and `just --dry-run test` passed. The subsequent
verification-phase host build reused the release artifact without compiling
again (0.23 seconds versus 1m08s for the initial host build).
