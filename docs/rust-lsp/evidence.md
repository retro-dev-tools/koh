# Rust LSP migration evidence

## Managed baseline

Source: master `065cf05`, 2026-11-19.

Command: `dotnet run --project tests/Asm/Koh.Lsp.Tests -c Release`.
Pinned SDK `/home/minorum/.local/share/koh-dotnet-10.0.115`, Rust 1.99.0.
Completed process exit: `0`.
Re-read log: `/tmp/koh-lsp-managed-baseline.log`.

```text
Test run summary: Passed!
  total: 207
  failed: 0
  succeeded: 207
  skipped: 0
  duration: 12s 694ms
```

Source inventory: `test-dispositions.md`, 207 methods, all pending migration.

Real managed stdio probe completed with server exit `0`: initialize, didOpen,
hover, definition, semanticTokens/full, shutdown and exit. Response assertions
required capabilities, non-null hover, nonempty definition and semantic token
data, and null shutdown result. Captured responses and notifications:
`/tmp/koh-lsp-managed-stdio-baseline.json`.

This baseline is not Rust implementation evidence. Full editor compatibility,
native replacement coverage, packaging and platform acceptance remain open.

## Native transport increment

Worktree: `/home/minorum/projekty/koh-rust-lsp`, branch `feat/rust-lsp`.
Approved worker-process design; native transport and worker client implemented.
No editor methods or workspace behavior yet; production publishing still uses
the managed server.

`cargo test -p koh-lsp --locked`, with `KOH_ASM` pointing to the baseline
assembler: exit `0`, 7 passed, zero failed/ignored. Completed log re-read:
`/tmp/koh-lsp-transport-green.log`. Covers Unicode/consecutive frames, header
validation, initialize/shutdown/exit, real worker reuse and UTF-16 spans, EOF,
bad JSON/version/response fields and a hung worker's deadline.

RED logs: `/tmp/koh-lsp-transport-red.log` (missing native implementation),
`/tmp/koh-lsp-worker-shape-red.log` (accepted incomplete worker response).
The latter failed with `Ok` response `{"version":1}` before validation was added.

Strict Clippy: `cargo-clippy clippy -p koh-lsp --all-targets --locked -- -D warnings`,
exit `0`; `/tmp/koh-lsp-transport-clippy-strict.log`.
Use explicit Rust 1.99 cargo/clippy/rustfmt paths on this host; cargo subcommand
discovery otherwise selects an unavailable rustup nightly component.

## Configuration and core workspace increment

`cargo build -p koh-asm --locked`: exit `0`, local worker built from this
worktree. Log `/tmp/koh-lsp-local-asm.log`.

`cargo test -p koh-lsp --locked`, without KOH_ASM override: exit `0`, 27 passed,
zero failed/ignored. Completed log `/tmp/koh-lsp-workspace-final.log` was re-read
and its per-binary summaries summed. Strict Clippy with `-D warnings`: exit `0`,
`/tmp/koh-lsp-workspace-final-clippy.log`.

Covers configuration validation and reload, include syntax/graph/discovery,
UTF-16 positions and escaped file URIs, configured project isolation,
root-owned macros in an open include, overlays and close-to-disk restoration,
heuristic edge changes, shared include ownership and per-root include search.
Large unread worker writes also time out on two successive child instances.

RED/GREEN: `/tmp/koh-lsp-incbin-red.log` reported label offset12 rather than2
when unsaved text incorrectly replaced physical INCBIN data. Removed that
binary overlay; the full suite passes. `/tmp/koh-lsp-nested-roots.log` failed
ownership for a shared include with `../` in worker source provenance.
Boundary probe confirmed logical `a/../shared.inc` response paths; normalizing
file identity comparisons fixes ownership without rewriting provenance.
`/tmp/koh-lsp-nested-roots-green.log` confirms the focused fix.

83 managed source cases mapped to current native assertions;124 remain pending.
This is not complete feature parity. Editor requests/publishing still await
implementation; production remains managed. YAML parsing uses serde_yaml_ng
0.10.0 (upstream metadata Rust1.64) and URL handling reuses the workspace's
existing url2.5 dependency. Whole-target Rust1.88 validation remains open.

## Completed workspace parity pass

`cargo test -p koh-lsp --locked`: exit `0`,37 passed, zero failed/ignored;
`/tmp/koh-lsp-task2-final.log` re-read and summaries summed. Strict Clippy
exit `0`; `/tmp/koh-lsp-task2-clippy.log` re-read.

An additional RED/GREEN test (`/tmp/koh-lsp-unrelated-{red,green}.log`) pins the
existing invariant that editing one configured project must not rebuild
unrelated projects. Reuse unaffected project analyses; rebuild all owners of
an edited shared include. Separate tests cover heuristic ownership after close,
overlay text/disk restoration, and missing relative includes.

Worker bridge parity also proves warning severity/messages/UTF16 spans for
analyze and assemble, instruction bytes, virtual/dynamic includes, separate
text/binary snapshots, physical/virtual READFILE byte limits and explicit nested
resolution mappings. Logs `/tmp/koh-lsp-{bridge,resolver}-parity.log`.

Current inventory:125 mapped replacements,1 obsolete private C# adapter test,
81 pending editor/classification cases. Obsolete test asserted delegation to a
VirtualFileResolver seam; native workspace has no such adapter. Explicit worker
resolution-policy assertions retain its useful behavior. No feature removal.

Tasks1–2 complete. Native stdio remains lifecycle-only until Task3 connects the
workspace/editor methods. Production publishing is still managed.

## Native navigation and rename increment

`cargo test -p koh-lsp --locked`: exit `0`,49 passed, zero failed/ignored;
`/tmp/koh-lsp-editor-navigation.log` re-read, per-binary summaries summed.
Strict Clippy `-D warnings`: exit `0`,
`/tmp/koh-lsp-editor-navigation-clippy.log` re-read.

Native main now routes messages to the Rust workspace. Real child-process
stdio checks cover initialize/open/diagnostics/hover/shutdown/exit, plus EOF
after shutdown. Direct protocol tests cover unsaved edits/close diagnostics,
hover text, definition ranges, references, prepare-rename and rename edits,
invalid requests and method errors. Full feature acceptance remains open.

Symbol tests cover local scopes, declaration/reference origins, constants and
string constants, macro declarations/calls, exported and owner-local shadowing,
root/include ownership, export directives, declaration exclusion and rename
validation/collisions. Locations are selected by native symbol ID and spans.
Instruction hover text is retained from the managed server; numeric hovers
evaluate through the worker rather than introducing another expression parser.

RED evidence: `/tmp/koh-lsp-symbols-red.log`, `/tmp/koh-lsp-server-red.log`;
real executable initially failed the new stdio capability/hover assertion in
`/tmp/koh-lsp-native-stdio-red.log`, then passed in its green log.
Instruction/numeric hover RED `/tmp/koh-lsp-hover-red.log`; final suite passes.
EOF after shutdown RED `/tmp/koh-lsp-eof-red.log`, GREEN
`/tmp/koh-lsp-eof-green.log`.

Navigation increment dispositions:173 mapped,1 obsolete,33 pending (semantic tokens, inlay
hints, macro signatures and classification completeness). Document symbols and
completion also need native protocol coverage; they had no dedicated managed
test methods. Native publishing, managed removal, Rust1.88 and platform/editor
acceptance remain open. Only implemented methods are advertised so far.

## Complete editor-method implementation

`cargo test -p koh-lsp --locked`: exit `0`,59 passed, zero failed/ignored;
`/tmp/koh-lsp-editor-full.log` re-read and summaries summed. Strict Clippy
exit `0`; `/tmp/koh-lsp-editor-full-clippy.log` re-read.

All original editor methods are implemented and advertised. Native checks now
cover the13 semantic token categories/legend and delta encoding, all baseline
syntax kinds, current worker keyword/kind coverage, multiline Unicode/CRLF
comments, inlay values/ranges/context exclusions, macro arity/active parameters
including nested commas and clamping, outline symbols and completion.
Untitled documents preserve client URIs for diagnostics, definition and rename
without writing files; their memory-only source identity remains stable.

Remaining-method RED: `/tmp/koh-lsp-editor-methods-red.log` (method not found).
Initial GREEN attempt exposed duplicate inlay hints from whitespace; filtering
identifier tokens fixed it. `/tmp/koh-lsp-untitled-{red,green}.log` proves the
extension's existing untitled selector now works. A flaky EOF fixture was split
into acknowledged-request EOF and immediate-exit transport-error checks;
immediate process closure may validly fail on either write or read.

Final legacy inventory:207 rows,206 mapped replacements,1 obsolete private
adapter, zero pending. The navigation increment had173 mappings (earlier notes
mistakenly stated169); the table itself had all173 mappings.
This is implementation/test parity, not packaged VS Code or platform proof.
Tasks1–3 complete; Task4 publishing, retirement and acceptance remain open.

## Native publishing and managed retirement

Root PublishLanguageServer now builds native koh-lsp, koh-asm and koh-link,
using existing RID mapping. PublishDev and release staging retain the linker.
CI and releases use native publishing and run packaged stdio checks; CI also
selects that package for real VS Code acceptance. Managed LSP source/tests,
solution entries and its three otherwise-unused package references are removed.
Other native bridge consumers remain.

After retirement, dotnet build Koh.Ci.slnf -c Release -m:1 exited0:
"0 Warning(s)", "0 Error(s)" (/tmp/koh-lsp-managed-build.log).
Retained managed tests exited0: "total: 297", "succeeded: 297", zero failed or
skipped (/tmp/koh-lsp-managed-tests.log). All seven C# samples built.

Scalar compatibility was checked against the actual managed parser:
numeric project name2048, quoted version'1', uppercase TRUE and numeric
entrypoint are accepted with original spelling. Native regression failed
before typed YAML deserialization, passed after:
/tmp/koh-lsp-config-scalar-oracle.log, /tmp/koh-lsp-scalars-red.log,
/tmp/koh-lsp-scalars-green.log. Final native60/60 and strict Clippy exited0
(/tmp/koh-lsp-final-clippy.log).

Rust1.88 compiler and worker build succeeded. Native60/60 tests and doctest
invocation exited0 in /tmp/koh-lsp-rust188-final.log. An initial separate-target
run could not discover the worker; final run explicitly selected the freshly
Rust1.88-built /tmp/koh-lsp-rust188/debug/koh-asm.

Actual VSCode1.147.0/Xvfb tests exercised ToolchainResolver, LanguageClient and
packaged ELF executables from a path with spaces. All46 extension tests passed
before review fixes (/tmp/koh-lsp-final-vscode.log, VSCODE_EXIT=0), including all
LSP editor methods, Unicode URI, unsaved diagnostics and orderly shutdown.
The DAP regression reused the separate engine worktree's existing native
artifact; this is unchanged extension-contract proof, not this branch's engine
build proof. Client lifetime now keeps logging alive until server process close.

New YAML dependency notices match the exact crate archives and Cargo.lock
checksums. Linux LSP/worker dependency closure has50 external packages, covered
by existing notice catalogs plus rust-lsp's serde_yaml_ng and unsafe-libyaml.
This does not resolve existing whole-toolchain first-party licensing questions.

Whole-branch fresh review found missing linker staging, YAML save forwarding,
Windows drive-relative entrypoints and asynchronous launch-error handling.
All four are addressed. Missing-linker assertion failed before staging fix;
packaged koh-link --version now exits0 (/tmp/koh-lsp-linker-{red,green}.log).
Actual client tests reproduced unhandled ENOENT and unchanged config completions
before the corresponding fixes (/tmp/koh-lsp-review-editor-red.log,
/tmp/koh-lsp-config-reload-red.log). Client now awaits spawn, handles launch
failure and process cleanup, and forwards root koh.yaml saves. The real-editor
suite passes47/47 (/tmp/koh-lsp-reviewed-editor-green.log, VSCODE_EXIT=0).
Windows-only regression rejects a path prefix even without a root; it requires
Windows CI execution and is not represented as a local passing test.

Final root publish exits0 (/tmp/koh-lsp-reviewed-publish.log); packaged stdio
checks pass4/4 (/tmp/koh-lsp-reviewed-package-stdio.log). Final artifact hashes:

| Artifact | SHA-256 |
| --- | --- |
| koh-lsp | e507ed7d48c2f8928fe838bc03fa2126f687cb636d032a1f302a7912e6ab6284 |
| koh-asm | dada8cc5636644f390d6f58b50d61f9b2360552a62df547a771e95ea82e5f65c |
| koh-link | 5e701a29d22ba7d48982b150fd4c7edf988f84fe7d226d801d126ddcb3197c15 |

The final LSP/assembler/linker closure contains52 external packages. Exact
archive checksums and1167 catalog notice references plus the2 new YAML notices
were verified (/tmp/koh-lsp-notice-final.log, exit0).
Workflow YAML and build XML parse successfully; hosted workflow execution is
separate evidence. A broad workspace nextest build was intentionally interrupted
(exit130) when it saturated this15GiB host; no workspace-suite success is claimed.
Affected-package nextest regression exited0 with two build jobs/test threads:
"249 tests run: 249 passed, 0 skipped" (/tmp/koh-lsp-affected-nextest.log).
Final editor rerun after launch-cleanup hardening exited0,47 passing,
VSCODE_EXIT=0 (/tmp/koh-lsp-final-reviewed-vscode.log).
Final strict Clippy exited0 (/tmp/koh-lsp-reviewed-clippy.log). Separate
affected-package doctest invocation exited0 with zero doctests present
(/tmp/koh-lsp-affected-doctests.log). Rust1.88 rerun after path-prefix fix
also passes60/60 (/tmp/koh-lsp-rust188-reviewed.log).
Windows/macOS runtime and hosted workflows have not run; no platform claim,
push, merge or release is implied by local Linux evidence.

Integration/retirement committed0a0978e. Commit hook exited0; no C# formatting
targets remained. No binaries, package caches or machine-specific test config
were committed. Main checkout's unrelated image remains untouched.

## Hosted acceptance

User authorized push and opening draft PR59. Remote branch head26bf90c was
verified using git ls-remote. Full CI44552197980 was dispatched on that SHA.
Linux job135658639624 completed with exit100:2463 tests run,2462 passed,
1 timed out,5 skipped. All60 LSP tests passed. The single timeout was
sdk_dotnet_run_launches_the_graphics_rom_headlessly (360.015 seconds).
Completed job log: /tmp/koh-lsp-ci-linux-completed.log, downloaded and re-read.
The test and SDK run target have no diff from base065cf05. Its cause is not
established; investigating/fixing SDK headless execution is a separate proposal.

Because that earlier failure skips later package/editor steps, a dedicated
Native LSP workflow now runs native tests, root publishing, packaged stdio and
actual VS Code acceptance independently on Linux, Windows and macOS. The full
workspace checks remain intact. Independent hosted results are pending.

Independent run44557708672: Linux job135678182248 passed native tests,
root publish, packaged stdio4/4 and actual editor46/46 (one DAP-only test
pending because this standalone gate does not stage the desktop debugger).
Completed log /tmp/koh-lsp-acceptance-linux.log was downloaded and re-read.
macOS135678181550 and Windows135678182167 failed workspace tests. macOS
fixtures could share pid/timestamp directory names and delete each other's
files; fixture names now also include an atomic sequence. Windows exposed
mixed-separator lookups against normalized document/project keys. All three
workspace lookup boundaries now normalize paths. A new logical-path-spelling
test fails before the fix (/tmp/koh-lsp-path-spelling-red.log) and passes after.
Native61/61 and strict Clippy exit0 after fixes
(/tmp/koh-lsp-platform-fixes-{native,clippy}.log). Platform rerun is pending.

Run44558820496 on9b4ff28 passed native and package tests on all three platforms.
Linux also passed actual editor acceptance. Windows/macOS editor setup exposed
the old @vscode/test-electron2.5.2 launcher: Windows shell splitting dropped the
workspace path containing spaces; macOS resolved the removed Electron binary.
The existing test runner is updated to published3.1.0, which resolves the macOS
bundle executable and spawns Windows executables without a shell. CI and
extension release use its supported Node22 runtime. Local actual VSCode47/47
passes with the updated runner (/tmp/koh-lsp-new-runner-vscode.log, exit0).
No custom launcher workaround or product behavior change was added.
Rust1.88 also passes61/61 and affected nextest250/250, zero skipped, after the
path fixes (/tmp/koh-lsp-platform-fixes-{rust188,affected}.log, exit0).

Run44560211970: Linux/Windows passed the complete native/package/editor gate.
macOS passed native/package tests and actual LSP editor assertions, but three
existing resolver filesystem tests shared the runner's real home: changing HOME
did not redirect its os.homedir(). The scratch helper now stubs/restores homedir
for each fixture. A local macOS simulation reproduced this before the fix
(/tmp/koh-lsp-macos-resolver-red.log, exit1); full editor48/48 passes afterward
(/tmp/koh-lsp-macos-resolver-green.log, exit0). Product resolver behavior is
unchanged; the fix is confined to test isolation. Final platform rerun pending.

Run44562541767 on39a4ede passed native, packaged stdio and full standalone
editor suites on all platforms:61/60/61 native cases (Linux/Windows/macOS),
4 packaged cases and47 editor cases each; one desktop-DAP-only case is pending
by design. Completed logs and ZIPs were downloaded for artifact inspection.
That final byte-level audit exposed Windows CRLF conversion of the two new
YAML license texts, violating their manifest hashes. The new notice files now
use -text in .gitattributes, matching the existing exact UI notice policy.
Git's core.autocrlf=true checkout filter now preserves both exact archive hashes
(/tmp/koh-lsp-notice-checkout-green.log, exit0). Native LSP CI verifies the
packaged notice hashes before editor tests. Final artifact rerun pending.

While pushing that fix, the feature branch advanced to fb4c22f. Its native LSP
cleanup is preserved through merge9381bd7: cached UTF-16 lines, shared immutable
project analyses and indexed include-document lookup. The combined branch was
reviewed and independently rechecked: native61/61, Rust1.8861/61, strict Clippy
and affected nextest250/250, zero skipped, all exit0 in
/tmp/koh-lsp-combined-{native,rust188,clippy,affected}.log.
The cleanup removed unused private graph APIs and renamed two tests; dispositions
now explicitly account for198 replacements and9 obsolete private API cases.
No advertised LSP method was removed. Linux remains in the independent gate
because full CI can stop at unrelated SDK/audio failures before package tests.
The remote cache optimization and removal of the redundant release build remain.
