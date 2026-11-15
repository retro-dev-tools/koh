# Container dependency removal

The inspected baseline had one container definition,
`tools/benchmarks/Dockerfile`. Its only launcher was `benchmark.sh`; the image
ran `run-benchmark.sh`, which called `generate-reports.py`. No CI workflow, root
build target or test invoked those four files. Repository searches covered
hidden workflow/config files as well as source, scripts, tools and documentation.

The runner had been changed to native Rust assembler/linker executable names,
so it was not accurate to call it a C# linker benchmark. It still could not make
the advertised Koh-versus-RGBDS comparison: both supported-capability arrays
were empty, while required assembler flags were `-P`/`-Q` and the linker required
`-l`. Thus both Koh phases were unconditionally classified unsupported on this
baseline. It could time RGBDS alone, but supplied no equivalent Koh workload or
compiler measurements. Porting its 535-line runner and 416-line report generator
would preserve an obsolete comparison, rather than validate the new compiler.

PR #55 deletes the four-file container pipeline. The following workflows remain:

| Consumer | Preserved behavior |
| --- | --- |
| `tools/benchmarks/Koh.Benchmarks` | Host-native assembler/bridge and emulator BenchmarkDotNet cases |
| `build.proj` benchmark target | Runs the existing benchmark project |
| CI benchmark job | Builds the project and checks Phase2/Phase3/Native discovery |
| Rust workflow reference job | Independent pinned RGBDS/ELF verification under `tools/verification` |

The benchmark README documents host execution, conditions to record and limits
on managed allocation measurements. No new benchmark framework, container
runtime, compiler dependency or performance claim is introduced. This cleanup
removes no compiler-owned tests. Historical generated-output ignore entries stay
to prevent existing local caches/reports from becoming accidental tracked files.

After deletion, a repository-wide text search found no container build/run or
compose invocation. An inventory of all existing tracked paths found no
Dockerfile or compose definition. Remaining references describe this historical
removal; unrelated sprite composition and npm's incidental package metadata are
not container dependencies. The maintained benchmark project and its CI/root
consumers are unchanged. The aggregate checkpoint's completed CI run
[43693156641](https://github.com/retro-dev-tools/koh/actions/runs/43693156641)
confirms benchmark build/discovery; its Rust reference job also completed.
No new performance measurements are claimed by deletion or test-suite results.
