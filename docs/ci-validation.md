# CI validation

| Event | Checks |
| --- | --- |
| PR opened, reopened or updated, including drafts | Two Linux jobs: Rust checks (fast selection) and .NET fast tests |
| PR opened, reopened or marked ready, including drafts | Full validation |
| Push to `master` | No duplicate validation run; validate the PR head before merge |
| Manual `Full validation` dispatch | Full validation on the selected ref |
| `tools-v*.*.*` or `ext-v*.*.*` tag | Existing toolchain or extension release packaging |

PR checks cover code, build configuration, samples and tooling paths. Documentation-only PRs skip them. GitHub evaluates path filters against the cumulative PR diff, so a documentation commit in a PR that also changes code still runs the checks. New PR pushes cancel superseded PR-check runs. Cargo and NuGet dependencies and Rust build outputs are cached.

Every ordinary code push runs Rust unit/core integration tests, including real Roslyn inputs and the existing emulator, plus the .NET fast suite:

```sh
cargo +stable fmt --all -- --check
cargo +stable nextest run --workspace --all-features --locked --no-fail-fast -E 'not (package(koh-compile) & (binary(driver) | binary(cubes) | binary(sdk)))'
cargo +stable test --doc --workspace --all-features --locked
dotnet build Koh.Fast.slnf --configuration Release -m:1
dotnet test --solution Koh.Fast.slnf --no-build --configuration Release --max-parallel-test-modules 1 -- --maximum-parallel-tests 1
```

Only the expensive compiler `driver`, `cubes` and `sdk` targets are excluded from ordinary pushes. All other workspace targets, including native compiler IR/backend, metadata/reference and malformed-input tests, remain selected. These three targets still run unfiltered in full validation on all three platforms. No test is ignored, removed or assigned retries to obtain a fast green result. The default local nextest command remains unfiltered.

`Koh.Fast.slnf` excludes the legacy C# compiler test project. Those tests remain in full validation until their migration is complete. Fast checks are an iteration signal, not compiler migration acceptance.

Full validation retains Rust tests and native SDK artifact checks on Linux, Windows and macOS; the full .NET suite; SDL3 renderer checks; emulator/UI NativeAOT publishing; extension tests; sample assembly; and benchmark discovery. RGBDS reference checks run the assembler and linker packages, without repeating compiler tests in that job. Tag-triggered release workflows retain their packaging checks.

Before merging or claiming migration acceptance, run full validation on the current PR head. It runs `cargo +stable nextest run --workspace --all-features --locked --no-fail-fast` without a filter and runs doctests separately. Reopening a draft starts it without marking incomplete work ready. Marking a draft ready also starts it. After further pushes, reopen the PR to validate the new head. Once this workflow is on `master`, Actions also exposes **Full validation → Run workflow**; select the PR branch. Manual dispatch is unavailable before the dispatch-enabled workflow reaches the default branch.

The seven existing protected checks retain their names and requirements. The two routine PR checks do not replace that merge gate. Passing results for an older commit do not validate a newer head.

Windows discovery has two fewer assembler tests: `include_provenance_preserves_the_logical_symlink_path` and `physical_include_keys_preserve_native_path_separators` are Unix-specific filesystem cases. They are compile-time platform exclusions, not ignored failures or omitted compiler targets. Source-debugger cases run on every platform; Unix cases additionally create an actual temporary-directory symlink to exercise physical PDB source identity.

The fast command passed1274/1274 locally in111.792s, exit0 (`/tmp/koh-ci-fast-cached.log`). Its three excluded targets contain604 cases on that checkpoint; full validation includes them. The [metadata fixture setup](rust-compiler/fixture-images.md) change reuses immutable Roslyn images within a serialized run; no test timeout, retry or assertion was relaxed. This measures regression setup, not compiler performance. YAML/event/matrix checks and the repository's seven required check names were verified before changing the workflow; branch protection itself was not modified.

## Recorded native compiler checkpoint

[Full validation44193814588](https://github.com/retro-dev-tools/koh/actions/runs/44193814588)
ran on567a192. All three native jobs passed their complete platform-applicable
workspace suites, separate doctests, release native builds, release graphics SDK
run smoke check and native-backed language-server publication.

| Host | Native tests | Skipped | Test phase |
| --- | ---: | ---: | ---: |
| Linux | 1891/1891 | 0 | 1304.198s |
| macOS | 1891/1891 | 0 | 1123.478s |
| Windows | 1889/1889 | 0 | 2206.247s |

The two Windows compile-time exclusions are the Unix assembler cases named above.
The previously failing source-debugger cases and both2048 SDK debugger cases
passed on Windows and macOS. Primary downloaded logs are
`/tmp/koh-full-567a192-{linux,macos,windows}-native.log`.

Both full .NET jobs failed114 legacy C# compiler tests each; downstream benchmark
and emulator AOT jobs were skipped. Their primary logs are
`/tmp/koh-full-567a192-{linux,windows}-dotnet.log`. The overall pipeline is not
green. Final replacement/deletion and release/product gates remain required.
Later mapper/rewrite commits change tests and documentation, not production
compiler code, but their new test coverage still requires current-head full
validation. These durations are regression-run evidence, not compiler benchmarks.
