# Rust LSP handoff

Updated 2026-11-19. Goal active; local implementation and review complete.
Worktree /home/minorum/projekty/koh-rust-lsp, branch feat/rust-lsp,
base065cf05. No push/merge/release authorization.

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

Next: obtain authorization to push and run hosted Windows/macOS acceptance.
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
