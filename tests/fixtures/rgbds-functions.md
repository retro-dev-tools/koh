# RGBDS function oracle

Verified with `rgbasm v1.0.1` and `rgblink v1.0.1`. From the repository root,
after building `src/Asm/Koh.Asm` in Release, run:

```sh
mkdir -p /tmp/koh-rgbds-functions
rgbasm -o /tmp/koh-rgbds-functions/reference.o tests/fixtures/rgbds-functions.asm
rgbasm -o /tmp/koh-rgbds-functions/symbols.o tests/fixtures/rgbds-functions-symbols.asm
dotnet src/Asm/Koh.Asm/bin/Release/net10.0/koh-asm.dll tests/fixtures/rgbds-functions.asm -f rgbds -o /tmp/koh-rgbds-functions/koh.o
rgblink -o /tmp/koh-rgbds-functions/reference.gb /tmp/koh-rgbds-functions/reference.o /tmp/koh-rgbds-functions/symbols.o
rgblink -o /tmp/koh-rgbds-functions/koh.gb /tmp/koh-rgbds-functions/koh.o /tmp/koh-rgbds-functions/symbols.o
cmp /tmp/koh-rgbds-functions/reference.gb /tmp/koh-rgbds-functions/koh.gb
od -An -tx1 -j256 -N8 /tmp/koh-rgbds-functions/koh.gb
```

Each command must exit zero. Both ROMs must match byte for byte; the eight
bytes at `$100` must be `60 ff 02 61 3e ff 61 03`. The external symbol is at
`$60FF` in bank 2, so the fixture distinguishes address bytes from the bank
number and checks the carry in `HIGH(ext + 1)`. It also covers an instruction
operand, nested HIGH/LOW, and arithmetic on BANK(symbol).

`RgbdsExpressionTests.FunctionCalls` checks the corresponding unresolved
patches without requiring RGBDS to be installed. BANK(section), BANK(@), and
other RGBDS functions are outside this regression's coverage.
