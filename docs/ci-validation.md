# CI validation

One workflow, `CI` (`.github/workflows/ci.yml`), validates every PR push, every push to `master` and manual runs.

| Event | Jobs |
| --- | --- |
| PR opened, updated, reopened or marked ready | `changes`, then only the jobs for the areas the cumulative PR diff touches; `ci-passed` |
| Push to `master`, manual run | Every job |
| `tools-v*.*.*` or `ext-v*.*.*` tag | Toolchain or extension release packaging |

`scripts/ci-changes.sh` maps changed paths to areas:

| Area | Paths | Jobs |
| --- | --- | --- |
| rust | `rust/**`, `src/Asm/Koh.{Lsp,Native}/**`, `licenses/**` | `native` (Linux, Windows, macOS) |
| references | `rust/koh-{asm,link,objects}/**` | `references` (RGBDS) |
| dotnet | `src/**`, `tests/**`, `samples/**`, `tools/**`, `rust/**` | `build-and-test`, `benchmark-native` |
| emulator | as dotnet, plus `licenses/**` and the GPUI verify scripts | `emulator-aot-publish` |
| vscode | `editors/vscode/**` | `extension-tests` |
| sample | `samples/asm/gb-2048/**`, `rust/koh-{asm,link,objects}/**` | `build-sample-2048` |

Workflows, `Cargo.*`, `Directory.*`, `Koh.*`, `global.json`, `build.proj` and any path the script does not recognize select every area. Documentation (`docs/**`, `*.md`) and images select none. Test the mapping locally with `git diff --name-only origin/master... | bash scripts/ci-changes.sh`.

`ci-passed` is the single merge gate: it fails when any job failed or was cancelled, and passes when jobs were skipped. New pushes cancel a PR's superseded run. Only `master` writes the Cargo/NuGet caches; PRs restore them. No test runs with retries.

Windows discovery has two fewer assembler tests: `include_provenance_preserves_the_logical_symlink_path` and `physical_include_keys_preserve_native_path_separators` are Unix-specific filesystem cases. They are compile-time platform exclusions, not ignored failures or omitted compiler targets. Source-debugger cases run on every platform; Unix cases additionally create an actual temporary-directory symlink to exercise physical PDB source identity.

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
