# CI validation

| Event | Checks |
| --- | --- |
| PR opened, reopened or updated, including drafts | Two Linux jobs: Rust workspace tests and .NET fast tests |
| Non-draft PR opened, reopened or marked ready | Full validation |
| Push to `master` | Full validation |
| Manual `Full validation` dispatch | Full validation on the selected ref |
| `tools-v*.*.*` or `ext-v*.*.*` tag | Existing toolchain or extension release packaging |

PR checks cover code, build configuration, samples and tooling paths. Documentation-only PRs skip them. GitHub evaluates path filters against the cumulative PR diff, so a documentation commit in a PR that also changes code still runs the checks. New PR pushes cancel superseded PR-check runs. Cargo and NuGet dependencies and Rust build outputs are cached.

Every ordinary code push runs the complete Rust suite, including compiler integration tests driven by Roslyn and the existing emulator:

```sh
cargo +stable fmt --all -- --check
cargo +stable nextest run --workspace --all-features --locked
cargo +stable test --doc --workspace --all-features --locked
dotnet build Koh.Fast.slnf --configuration Release -m:1
dotnet test --solution Koh.Fast.slnf --no-build --configuration Release --max-parallel-test-modules 1 -- --maximum-parallel-tests 1
```

`Koh.Fast.slnf` excludes the legacy C# compiler test project. Those tests remain in full validation until their migration is complete; Rust compiler tests are never filtered out of PR checks.

Full validation retains Rust tests and native SDK artifact checks on Linux, Windows and macOS; the full .NET suite; SDL3 renderer checks; emulator/UI NativeAOT publishing; extension tests; sample assembly; and benchmark discovery. RGBDS reference checks run the assembler and linker packages, without repeating compiler tests in that job. Tag-triggered release workflows retain their packaging checks.

Before merging, run full validation on the current PR head. Marking a draft ready starts it automatically. After further pushes, return the PR to draft and mark it ready again, or reopen a non-draft PR. Once this workflow is on `master`, Actions also exposes **Full validation → Run workflow**; select the PR branch. Manual dispatch is unavailable before the dispatch-enabled workflow reaches the default branch.

The seven existing protected checks retain their names and requirements. The two routine PR checks do not replace that merge gate. Passing results for an older commit do not validate a newer head.
