# Native engine review and local acceptance

Baseline: `e1336b048a67549ed99a8849ac78479213f570c8`.
Scope: production emulator, debugger, engine host and their launch/package paths.
Verification tools retain their existing C# emulator/boot dependencies.

An independent read-only review covered host commands, shutdown, persistence,
DAP transport/dispatch/inspection/stepping, UI process ownership, SDK launch,
publishing and CI. It found no confirmed critical or important migration
regression in those paths. It did not establish formal CPU/PPU/APU equivalence
or replace runtime/platform acceptance.

The review checked temporary step breakpoints against the actual production
`DapServerHost`, rather than the standalone fixture session. Both implementations
overwrite and later delete a persistent breakpoint at the temporary target.
This inherited behavior is outside the rewrite; it is not a new migration fix.

Local completed evidence:

| Check | Evidence |
| --- | --- |
| Native core | 91/91; `/tmp/koh-engine-core-lint-regression.log` |
| Debugger | 27/27; `/tmp/koh-engine-debugger-lint-tests.log` |
| Exact legacy windowless cases | Three original parameter tuples; `/tmp/koh-engine-windowless-original-matrix.log` |
| Focused strict Clippy | Four native crates, all targets, no dependencies, warnings denied; `/tmp/koh-engine-native-clippy-final.log` |
| Remaining managed build/tests | Zero warnings/errors; 504/504, zero skips; `/tmp/koh-engine-managed-{build,tests}.log` |
| Native package | Successful final root publish; `/tmp/koh-engine-publish-final.log` |
| Real VS Code/default desktop/native DAP | 21 passing, zero pending; `/tmp/koh-vscode-packaged-native-tests.log` |
| Packaged native window | Exact LCD, integer scaling, Start, pause, save, child shutdown and empty shortcuts; `/tmp/koh-engine-native-window-final.log` |

The packaged window's title and gameplay captures were inspected. The package
contains `koh-emulator`, `koh-engine`, native OpenAL and notices, with no managed
engine executable or DLL. SHA-256:

| Artifact | SHA-256 |
| --- | --- |
| koh-emulator | `5f9bd2e0910bccda11144fa041f05b39ced49969261dde0328f3b3764bfdaef6` |
| koh-engine | `e904e800282e1f2c6bcfb90471f5ed8b18b4b47d40b3089433e5f001d3f9620f` |
| libopenal.so | `7162b88c6c1fea862746f688ee0a9267a72033f5ea5b82af74d304ec5061a1dc` |

Full compiler regression and Windows/macOS runtime acceptance remain open.
The first compiler run stopped after 1362 passes on an abbreviated palette
guard diagnostic. The exact original BG/OBJ diagnostics are restored; both
original cases and a focused guard test pass. A full no-fail-fast rerun is active.
The native-window harness now decodes valid filtered PNGs using its existing
Pillow dependency; IHDR, CRC, decoded length and opaque-alpha checks remain.
Neither correction changes compiler assertions or production image encoding.
