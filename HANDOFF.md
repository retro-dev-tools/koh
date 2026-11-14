# Rust assembler migration handoff

## Scope and branch

Deliver one PR against `master`; do not merge. The latest user instruction
requires removing the C# assembler and CLI in this PR, superseding retention.
`src/Asm/Koh.Assembler`, `src/Asm/Koh.Asm` and their two test projects are removed.
The C# compiler, linker, emulator, debugger and managed language-server host remain.

Branch: `feat/rust-assembler`, based on `bf0a5fc`. The original checkout and its
pre-existing battle screenshot edit remain untouched. The screenshot SHA-256 is
`fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454`.
The previous SDL session is separate; see `docs/sdl3.md`.

## Architecture

- Cargo workspace: native CLI/library in `rust/koh-asm`.
- `model.rs`: canonical, format-independent symbols, complete relocation trees,
  typed references, patch widths/semantics, separate execution PC and provenance.
- `draft.rs`/`lower.rs`: private construction and validated name binding.
- `object.rs`: loss-checked KOBJ v4 bridge and RGBDS RGB9 revision13 adapters.
- `analysis.rs`: editor metadata; `service.rs`: versioned JSON-lines transport.
  Canonical assembly controls diagnostics; actual exports resolve project imports.
- `AssemblyReport` preserves structured warnings, including alongside errors.
- `Koh.Native`: managed DTO/process facade, not another parser/binder/assembler.
  LSP, boot builds, tests, installers and benchmarks use the native implementation.
- Text and binary overlays remain separate; UTF-16 metadata maps editor positions.
- Differential C# reference lives only in an external clean checkout at exact
  `bf0a5fc1792b6022328af080cd50f7b4d7e143c5`; scripts reject drift.

See `docs/rust-assembler.md`, `docs/rust-assembler-objects.md`, and
`docs/rust-assembler-compatibility.md` for scope and format decisions.
Do not introduce speculative KOBJ v5 or discard rich expressions to fit v4.

## Completed local validation

Actual completed logs and exit codes were read:

- `/tmp/koh-native-final-compat-tests.log`: EXIT0,125 Rust tests with RGBDS1.0.1.
- `/tmp/koh-native-warning-lsp-tests.log`: EXIT0,205 native-backed LSP tests.
- `/tmp/koh-native-final-tree-build.log`: EXIT0,zero warnings/errors.
- `/tmp/koh-native-final-tree-fast-tests.log`: EXIT0,586 passed,2 native-window skips.
- `/tmp/koh-native-removal-ci-tests.log`:812 compiler tests passed. Its initial
  linker fixture failure was corrected using an oversized object directly; all
  linker tests passed in the final fast run. Compiler source remained unchanged.
- `/tmp/koh-native-final-tree-verification.log`: EXIT0,7 workflows, independent
  semantics and9 negatives.233 historical raw fixtures:183matches,18rejections,
  32differences; all50 deviations classified and0unknown.
- `/tmp/koh-native-final-2048-runtime.log`: EXIT0,all11 gameplay checks passed.
  The final release output exactly matches that executed65536byte ROM:
  `1e9e94c13e5ad436273b68357031ca981d76f477d07ec9419beed2a3bb75b455`.
- `/tmp/koh-macro-final-committed.log`: EXIT0,all5expected outcomes. The reported
  historical corruption did not reproduce in4valid reconstructed cases; the
  no-global-anchor case is a Koh extension. Do not assert the old bug still exists.
- `/tmp/koh-native-boot-rid-final.log`: EXIT0,zero warnings/errors; DMG256/CGB2304bytes.
- `/tmp/koh-native-final-tree-publish.log`: EXIT0,actual Linux LSP publish.
- `/tmp/koh-native-final-tree-published-rpc.log`: EXIT0,real LSP protocol outside
  the checkout uses the bundled native child without KOH_ASM override.
- Published worker equals fresh producer; all4notice files match source exactly;
  legacy assembler DLLs and dependencies are absent.
- `/tmp/koh-native-benchmark-discovery.log`: EXIT0,nine native methods discovered.

## Limits and remaining gate

Hosted Linux/Windows/macOS checks must report on the PR head; local results do
not prove those hosts. Actual cross-compilation and interactive VS Code UI were
not tested. Cross-RID filename selection has a fixture smoke only.

KOBJ v4 rejects lossy expressions, unsupported constraints and incompatible
names explicitly. Full RGBDS compatibility is not claimed. Bare WARN is a
deliberate Koh extension; RGBDS rejection alone does not establish a Koh bug.

All11 locked Cargo package archives and copied license texts were verified.
Rust standard-library/runtime notices remain a separate release-policy audit;
the dependency manifest does not claim that audit. No merge or release is authorized.

## PR53 review follow-up

Current head9213bd9 has3open review threads and2native CI test failures.
Ownership for independent fixes:
- Encoding: encode.rs and assembler's encoder-call context, immediate/constant
  instruction regressions against RGBDS.
- Resolver: managed native requests plus explicit resolver mappings in engine
  and analysis, nested entrypoint-root include regressions.
- Source: source.rs block-comment scanner and regressions preserving literals/lines.
- Lead: provenance test path spelling on Windows/macOS, regression via symlink,
  integration tests, thread replies/resolution and CI on the new head.

No shared output directory builds concurrently. Each owner uses an isolated
Cargo target; wait for source freeze before integrated managed builds.

Review fixes now committed in077803c; CI path assertion fixed ined987d2.
Final local checks after hooks:148Rust/RGBDStests PASS;207LSP PASS;
`/tmp/koh-review-integrated-build.log` EXIT0zero warnings/errors;
`/tmp/koh-review-final-fast-tests.log` EXIT0,588passed2skipped;
`/tmp/koh-review-final-verification.log` EXIT0,7workflows+semantics+9negatives,
183matches+50classifieddeviations,0unknown. FinalROM still matches executed sample.
Direct triple-quotedDB expressions remain an explicit documented limitation,
not part of the comment-scanner review fix. Push then resolve3threads and
wait for hosted CI on that new head before claiming cross-platform success.

Windows follow-up: dynamic physical includes inserted a raw path key while lookup
normalized separators. Native path normalization is now consistent at insertion
and lookup, without conflating Unix backslash filenames and directory separators.
`/tmp/koh-review-native-path-tests.log`: EXIT0,149Rust/RGBDS tests.
`/tmp/koh-review-native-path-lsp-tests.log`: EXIT0,207LSP tests.
