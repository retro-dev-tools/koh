# Macro unique suffix investigation (historical findings)

The findings below describe the former C# implementation. Current regression
checks run the Rust ELF pipeline against independent RGBDS; no historical
checkout is fetched or built.

The sample comment in `samples/asm/gb-2048/src/macros.inc` reports a historical
`WAIT_VBLANK` macro using `\@` that corrupted subsequent macro definitions. The
omitted failing macro is unavailable in that file. A reconstructed ordinary
`.wait\@:` / `jr z,.wait\@` loop did **not** reproduce that failure against the
pinned historical C# assembler. The comment remains historical evidence, rather than proof
of a current defect.

The checked C# assembler sources matched `bf0a5fc`; the initial investigation
worktree HEAD was `45aed7d7321aeb521c11b98ef8f8576053431229`. After removing
the C# implementation, the same cases were rerun against the external checkout
pinned to `bf0a5fc1792b6022328af080cd50f7b4d7e143c5`, with identical outcomes. The C# verifier was freshly
built with .NET SDK 10.0.115: exit 0, zero warnings and zero errors. Both RGBDS
tools were pinned to 1.0.1.

| Fixture | C# assemble/link | RGBDS assemble/link | Linked section bytes |
| --- | --- | --- | --- |
| WAIT definition followed by actual sample macros | Pass | Pass | Equal |
| Repeated WAIT calls followed by every sample macro | Pass | Pass | Equal |
| WAIT inside REPT inside another macro, invoked twice | Pass | Pass | Equal |
| Existing `Macro_WithAtLabelOperand_ExpandsCorrectly` fixture | Pass | Pass | Equal |
| Existing unique-label fixture without a global anchor | Pass | Rejects | Not compared |

The final RGBDS rejection was `FATAL: Unqualified local label .label_u1 in main
scope`. This is a language behavior difference; it did not corrupt following
macro definitions. The exact sample include and reconstructed sources are saved
with each run. RGBDS companions change only legacy `name: MACRO` declarations to
`MACRO name`; RGBDS 1.0.1 rejects the former syntax. Comparisons use linked
section bytes, including resolved CALL and JR relocations, rather than object
placeholders.

Run from the repository root with compatible .NET and RGBDS tools on `PATH`:

```sh
cargo build --release --locked -p koh-asm -p koh-link
python3 scripts/reproduce-macro-unique.py --output /tmp/koh-macro-unique
```

The script reuses current Release Rust binaries; override them with `--rust`
and `--linker`. It writes reconstructed source companions and `results.json`.
Four cases compare complete raw images from native ELF linking with RGBDS.
The no-global-anchor case requires three native NOP bytes and the known RGBDS
rejection. Unexpected exits or byte differences fail. The pinned historical
helper and KOBJ dump/link interfaces have been removed.

`rust/koh-asm/tests/macro_unique.rs` independently checks expected native bytes
for the repeated WAIT/sample macros and nested repetition cases. These checks
cover the reconstructed inputs. They do not establish the exact historical
failure, all macro expansion cases, or physical VBlank behavior.
