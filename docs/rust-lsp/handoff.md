# Rust LSP handoff

Updated 2026-11-19. Goal active; local implementation and review complete.
Worktree /home/minorum/projekty/koh-rust-lsp, branch feat/rust-lsp,
base065cf05. User authorized push and draft PR59; no merge/release authorization.
Integration/retirement commit:0a0978e. Branch pushed; draft PR59 open.

Rust koh-lsp owns all original editor methods, configuration, include ownership,
overlays, diagnostics and stdio lifecycle. It invokes the persistent
koh-asm --serve-json worker. Managed LSP source/tests, solution/filter entries
and three unused package references are retired. Shared Koh.Native remains.

Root PublishLanguageServer stages native koh-lsp, koh-asm and koh-link plus
notices. PublishDev/release retain the linker. CI/release use native publishing
and packaged stdio tests; CI selects the package for actual VS Code acceptance.

Completed logs in evidence.md: native60 checks on Rust1.88; affected Rust
nextest249/249, zero skipped; strict Clippy exit0; retained managed297/297,
zero skipped; solution build0 warnings/errors; packaged stdio4/4; actual
VSCode1.147.0/Xvfb47/47. DAP regression reused an existing separate engine
artifact, not an engine build on this branch. The full Rust workspace build
was stopped at host saturation (exit130); it is not passing proof.

Fresh whole-branch review found four defects, all addressed: linker staging,
config-save forwarding, Windows drive-relative paths and asynchronous spawn
errors. Real editor tests reproduced ENOENT/config reload failures and passed
after fixes. Windows-only path regression requires Windows execution.
YAML scalar coercions were checked against the actual managed parser.

Next: inspect independent Native LSP workflow results on all three platforms.
Run44557708672 Linux passed package/editor acceptance. macOS fixture collisions
and Windows unnormalized lookup keys were exposed; fixes pass native61/61 and
strict Clippy locally, committed/pushed9b4ff28. Rust1.8861/61; affected250/250.
Run44558820496 passes native/package checks on all platforms, but editor setup
fails on Windows/macOS in test-electron2.5.2. Updated existing runner to3.1.0
and CI/release Node22; actual local editor47/47 passes. Push and inspect rerun.
Runner fix pushed d435f86. Run44560211970 Linux/Windows fully pass; macOS actual
LSP editor assertions pass but three existing filesystem resolver tests share
the real home. Fixed test helper isolation with a failing macOS simulation;
actual local editor48/48 passes. Push test-only fix and inspect platform rerun.
Isolation fix pushed39a4ede; run44562541767 fully passes all three platforms.
Final artifact byte audit caught Windows CRLF conversion of the two new YAML
notices. Added -text attributes and packaged hash gate; checkout filter test
passes. Push notice fix and audit the resulting artifacts before closing goal.
Remote fb4c22f advanced the same branch; preserved via merge9381bd7. Combined
code passes native61/61, Rust1.8861/61, affected250/250 and strict Clippy.
Inventory reconciled after unused graph API removal:198 replacements,9 obsolete.
Retain Linux in independent gate; remote cache optimization is preserved.
Full CI44552197980 Linux ran2463 tests:2462 passed,1 SDK graphics test timed
out after360 seconds,5 skipped. All60 LSP cases passed. The SDK test and run
target are unchanged by this migration; its timeout needs separate investigation.
Proposal: investigate SDK graphics headless-run timeout as a separate increment.
The independent LSP gate builds/tests/packages and exercises actual VS Code
without waiting behind unrelated compiler/emulator acceptance.
Do not infer platform success from Linux or static workflow parsing. No release
publication is needed. Existing whole-toolchain licensing questions remain
release blockers; exact added YAML dependency notices are verified.

Verify: cargo build -p koh-asm --locked; cargo test -p koh-lsp --locked.
Affected suite: cargo nextest run -p koh-lsp -p koh-asm -p koh-link -p koh-objects
--all-features --locked --test-threads 2. Use CARGO_BUILD_JOBS=2 on this host.
Root dotnet msbuild build.proj -t:PublishLanguageServer publishes native tools.
Set KOH_LSP_TEST_BINARY for packaged stdio tests; set KOH_TEST_LSP_DIR for
npm test in editors/vscode. Standard extension test config creates/removes
a disposable workspace. Keep builds serial per output directory.
Rust: /usr/lib/rust/1.99.0/bin; .NET: ~/.local/share/koh-dotnet-10.0.115.

Main checkout battle.png and untracked initial migration docs are preserved.
This branch docs/rust-lsp are authoritative. Main HANDOFF.md is unchanged.
