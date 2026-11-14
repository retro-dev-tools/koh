# Native pipeline verification

```sh
python3 scripts/verify-rust-assembler.py
python3 scripts/reproduce-macro-unique.py
```

The first script builds the Rust assembler/linker and execute-only managed
helper serially. It requires RGBDS 1.0.1 and the .NET SDK from global.json.
Use `--no-build --rust PATH --linker PATH --output DIRECTORY` to reuse binaries.

Native source -> ELF -> koh-link raw ROM is compared byte-for-byte with the
independent source -> rgbasm -> rgblink pipeline. Workflows cover execution,
relocations, macros, instruction encoding, independent semantics, the 2048
sample and cross-file imports. Nine malformed inputs must fail independently
with diagnostics. The results JSON records workflow counts and negative exits.
Temporary sample syntax adaptations are inventoried separately.
The sample's default floating cartridge is built separately. Its complete-byte
oracle pins RGBDS's map addresses in temporary native sources because equal-size
floating sections have different valid tie ordering. The report identifies
that constrained comparison; default floating images are not claimed equal.

The helper supports only `execute ROM`: it runs the real Koh emulator and
requires call/return and arithmetic to write 2A 2B at WRAM C000 within 10,000
instructions. Both independently linked execution ROMs are checked.
This is a bounded execution regression, not graphical or hardware proof.

The old pinned C# reference, KOBJ dump/link commands and serialized expected
graphs were retired. The historical corpus/classification rationale and its
current replacement matrix are in
[the compatibility record](../../docs/rust-assembler-compatibility.md).
