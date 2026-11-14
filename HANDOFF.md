# Rust linker migration

Base: master 9a2cfa8. Main checkout's modified JRPG screenshot is unrelated and preserved.

## Ownership

- Audit and migration units landed; Rust sources are frozen after shared numeric evaluation, ELF allocation bounds, alignment and JR wrap corrections.
- Parent owns final evidence, current-head CI and the PR description. Implementation and review fixes are committed.
- Independent C# / Rust / RGBDS comparison finished; temporary artifacts live at /tmp/koh-three-linkers. The historical source checkout is clean and is never a production dependency.

## Shared interfaces

- `koh-objects` owns the model, ELF codec and numeric evaluator. `read_elf(&[u8])` and `write_elf(&Object)` are the sole binary object interface.
- `koh-link` owns placement, resolved evaluation, ROM/SYM/KDBG and the compiler's JSON-to-ELF command boundary. The C# bridge contains process invocation and a resolved compiler-model view, without a relocation engine.
- No builds in shared target directory except parent. Agents use CARGO_TARGET_DIR=/tmp/koh-elf-target or /tmp/koh-link-target.

## Verification

Run builds serially per output directory. Capture direct exit status and reread completed logs. Rust: cargo test --workspace --locked. .NET: focused tests, then constrained fast suite. Extension: npm test. Runtime and platform claims require actual evidence.

## Completion

Draft PR #54 is open against master at the user's explicit request. Keep it draft and unmerged. Logical implementation, cleanup and correction commits are separate. The C# linker and retired object formats are deleted; no fallback is retained.

Full .NET run before final review fixes: 1372 passed, 2 existing native-window skips, 0 failed (/tmp/koh-link-all-tests.log). Fast suite after initial review fixes: 561 passed, 2 skips, 0 failed (/tmp/koh-link-final-fast-tests.log). Focused compiler static-array class: 16/16 passed. Final native/object boundary: 10/10 passed. Rust workspace with RGBDS and extension 44-test suite passed. SDK incremental/path/failure checks, both sample targets and publish/type inspection passed. Both assembly and C# games ran in independent mGBA. See docs/rust-linker/validation.md and the test-disposition matrix for scope and actual artifacts. Verify the current PR head's hosted checks before any integration decision; do not mark ready or merge on the user's behalf.
