# Native SDK boundary

Ordinary C# projects retain Roslyn and `Koh.GameBoy`. After the managed build,
the SDK runs `koh-compile` on the resulting assembly and implementation reference
paths, then passes its canonical ELF32/SM83 output to `koh-link`. The MSBuild task
contains process/file orchestration only. It references neither `Koh.Compiler`
nor the C# native-linker bridge, and has no fallback route.

## Deliberate integration decisions

The old frontend/backend registry options were removed. This product has one
CIL frontend and one SM83 backend; keeping selection interfaces would add unused
states and preserve compiler internals as SDK configuration. `ProgramName` was
only an intermediate object label, not a cartridge-title contract. The assembly
and explicit ELF path now identify those inputs.

The compiler object persists under `obj/<configuration>/net10.0/<assembly>.o`
by default. `KohObjectPath` overrides it; `KohRomPath` controls the ROM. Both tools,
the managed assembly, references, present Portable PDBs, task and property stamp participate in incremental
invalidation. Changed project-reference bodies must change observable ROM behavior,
even when their public reference-assembly signatures are unchanged.

Managed symbols reach the native compiler through `--pdb` for the root and
adjacent discovery for references. `KohEmitDebugInfo=false` disables PDB discovery
and removes stale KDBG output. PDB paths/presence participate in the property
stamp; PDB timestamps invalidate source metadata. The compiler validates PE/PDB
identity and supplied checksums. Missing symbols remain optional. See the
[source mapping contract](source-debugging.md); actual debugger acceptance is
tracked separately from object/KDBG emission.

`KohCompilerExecutable`/`KOH_COMPILE` and `KohLinkExecutable`/`KOH_LINK` override
native tool discovery. Defaults are beside `Koh.Build.Tasks.dll`. Its host tools
are built from the existing Rust crates into a separate
`target/compiler-msbuild/<configuration>` directory. The task is portable managed
MSBuild code; runtime/self-contained/publish properties from a game do not turn
these build-time tools into target-host binaries. This does not redesign the
repository-local NuGet SDK distribution.

The emulator is built on `dotnet run`, rather than being a dependency of every
ROM build. The run command delegates to the existing emulator project, preserves
the selected configuration and forwards an absolute ROM path plus user arguments.
Relative overrides are resolved against the game project; otherwise the nested
emulator launch searches its own working directory. This removes
unnecessary GUI/audio build coupling without replacing the emulator.

Native processes receive argument lists, not shell command strings. Errors include
tool path, exit status and native diagnostics. Cancellation kills the owned process
tree. Output identities resolve parent links before comparing with input paths;
the Unix regression originally demonstrated a DLL being replaced through an alias.
Compiler/linker failures preserve the prior ROM. Disabling debug generation removes
the owned stale KDBG after successful linking, and re-enabling regenerates it.

## Retained asset pipeline

`GenerateKohTileSheets`, `TileSheetConverter` and `PngReader` stay in the C# build
task project. They are useful authoring tools, not compiler algorithms. Rust SDK
tests supply checksum-valid four-color PNGs, verify exact generated 2bpp bytes and
RGB555 bookends, compile a real consumer, then execute its result in the existing
emulator. A five-color PNG must fail with an authoring diagnostic and preserve
the previous ROM. Unchanged builds must retain generated-source inclusion.

This replaces the two compiler-owned tile-sheet TUnit methods with native test
ownership; it does not claim broader PNG/checksum validation by the existing decoder.

## Reproduction and evidence scope

```sh
cargo nextest run -p koh-compile --test sdk
dotnet build samples/csharp/gb-gfx-demo/GbGfxDemo.csproj -c Release -m:1
dotnet run --project samples/csharp/gb-gfx-demo/GbGfxDemo.csproj -c Release --no-build -- --headless --frames=1200 --screenshot=/tmp/koh-gfx.png --mode3-report=/tmp/koh-mode3.txt
```

The Rust cases exercise Debug/Release, project paths and native executable paths
with spaces, canonical ELF, reference inclusion/body changes, DMG/CGB flags,
tool timestamp invalidation, no-op rebuilds, debug toggling, missing tools,
unsupported IL, output aliases, asset generation and actual CPU/MMIO state.
The parent-link fixture currently uses Unix symlinks; Windows ordinary path
validation is covered, but junction-specific creation/behavior needs hosted proof.

The separate SDK `dotnet run` case executes the actual graphics sample through
the existing headless CLI and checks PNG output and zero mode-3 violations. It
tests run routing; independent exact game-state/pixel assertions remain in the
four `driver::full_graphics_showcase_boots_and_animates` cases.

The local real sample produced a65536-byte ROM via default tool discovery and ran
1200frames, exit0. Its screenshot was inspected and the mode-3 report contains
zero violations. Logs: `/tmp/koh-sdk-native-showcase-build.log`,
`/tmp/koh-sdk-native-showcase-run.log`; artifacts are outside tracked sample assets.
These observations are not performance comparisons or source-debug acceptance.

The post-commit local SDK target passed3/3, zero skipped, in152.483s, exit0
(`/tmp/koh-sdk-postcommit.log`), including timestamp-isolation checks. The full
all-features workspace passed774/774, zero skipped, in1230.794s
(`/tmp/koh-sdk-workspace.log`); separate doctests passed (no cases). These are
local results, not cross-host acceptance. Red evidence includes native-tool
selection ignored by the old SDK (`/tmp/koh-sdk-native-red.log`), stale debug
symbols (`/tmp/koh-sdk-debug-toggle-red.log`), DLL replacement through an output
alias (`/tmp/koh-sdk-alias-red.log`), and relative-ROM launch failure
(`/tmp/koh-sdk-relative-run.log`), all exit100. The isolated relative-path fix
passed (`/tmp/koh-sdk-relative-run-green.log`, exit0).

Portable PDB reading and source KDBG/debugger validation are still outstanding.
Current KDBG emission must not be described as completed source-level debugging.
Other real games, release archives/CI packaging and legacy compiler deletion
remain migration gates in this same draft PR.

The published Linux build-task payload includes both native tools and the existing
complete Rust dependency notice/license bundle. Both tools report1.0.2; the
payload contains neither `Koh.Compiler.dll` nor `Mono.Cecil.dll`. Selecting its
task DLL with default colocated tool discovery builds the real graphics sample;
the managed emulator executes1200frames with a160x144 PNG and zero mode3
violations. Logs: `/tmp/koh-compiler-task-package.log`,
`/tmp/koh-compiler-task-package-smoke.log`, `/tmp/koh-compiler-task-package-run.log`.
Release CI additionally runs the Rust SDK graphics case against its uploaded
release executables on each host. Tagged archive/AOT smoke results remain open.

## Hosted predecessor regressions

Presentation predecessor3a9f3a1 passed native CI on all three hosts, but managed
CI run43728226133 failed six legacy compiler tests on both Linux and Windows:
`Dmg_ScatteredRun_StillDripsAcrossSeveralFrames_DripPathUnchanged`,
`Sample_BootsToTitle_StartsGame_SlidesSpawnTiles`,
`FlushRun_CompiledLoopBodyUsesFusedAddressingNotPerIterationReload`,
`FlushRun_DstAndSrcPointerPhisAreRegisterResident`,
`Sample_CgbPlaythrough_WalksTalksAndCallsBack`, and
`Sample_CaptureAcceptanceScreenshots`. Logs name the61-frame drip budget,
gameplay state mismatches, private IR expectations and dialogue startup timeout.
These are unresolved migration findings, not permission to drop gameplay or
weaken assertions. Their independent contracts must be investigated and ported;
the C# compiler is a behavioral reference, not the new architecture's oracle.
The SDK transition has not established a passing complete managed solution.
