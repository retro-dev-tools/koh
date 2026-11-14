# Macro unique suffix investigation

The sample comment in `samples/asm/gb-2048/src/macros.inc` reports a historical
`WAIT_VBLANK` macro using `\@` that corrupted subsequent macro definitions. The
omitted failing macro is unavailable in that file. A reconstructed ordinary
`.wait\@:` / `jr z,.wait\@` loop did **not** reproduce that failure against the
current C# assembler. The comment remains historical evidence, rather than proof
of a current defect.

The checked C# assembler sources matched `bf0a5fc`; the investigation worktree
HEAD was `45aed7d7321aeb521c11b98ef8f8576053431229`. The C# verifier was freshly
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
python3 scripts/reproduce-macro-unique.py --build --output /tmp/koh-macro-unique
```

The default (`--no-build`) reuses the existing Release verifier without rebuilding. Override
it with `--dotnet PATH` and `--helper PATH`. The script reads the verifier's
`assemble kobj SOURCE OUTPUT`, `dump OBJECT`, and `link ROM OBJECT` interfaces.
It validates both RGBDS versions, records every command and actual exit status,
and writes `results.json` with tool versions, source revision, helper/sample
hashes and the C# source diff from the checked revision. Unexpected failures or
byte differences produce a nonzero exit. The known no-global-anchor rejection
is explicitly checked rather than silently ignored.

`rust/koh-asm/tests/macro_unique.rs` independently checks expected native bytes
for the repeated WAIT/sample macros and nested repetition cases. These checks
cover the reconstructed inputs. They do not establish the exact historical
failure, all macro expansion cases, or physical VBlank behavior.
