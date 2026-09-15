# Koh.Compat.Tests — disabled

This project is **disabled**: it is not in `Koh.slnx`, not in `Koh.Ci.slnf`, not in
`Koh.NoCompat.slnf`, has no `build.proj` target, and no CI job. Its sources are
untouched on disk.

## What it is

The external-ROM compatibility suites — Blargg, Mooneye (`mts-*` acceptance), and
`dmg-acid2`. They load real test ROMs and check pass/fail signals: Mooneye uses a
Fibonacci register pattern after a `LD B,B` breakpoint, Blargg writes to serial.

## Why it is off

These ROMs assume the register and I/O state a boot ROM leaves behind. Commit
`5fc842e` removed the emulator's fabricated boot handoff and deliberately did not add a
boot ROM mechanism, so the suite has been broken since — not by a regression in the
emulator, but because nothing initialises the machine any more.

They also cannot verify anything in a fresh checkout: `tests/fixtures/` does not exist
until `scripts/download-test-roms.sh` runs, so every case hits
`Skip.Test("ROM missing")`. A green run proved nothing, which is worse than a red one.

## Re-enabling

Boot ROM support is being added (`docs/superpowers/specs/2026-08-08-boot-rom-design.md`).
Once `Koh.Boot` ships, re-enabling means:

1. Add `tests\Koh.Compat.Tests\Koh.Compat.Tests.csproj` back to `Koh.Ci.slnf` and
   `tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj` to `Koh.slnx`.
2. Add a `ProjectReference` to `src/Koh.Boot`, and call
   `gb.LoadBootRom(BootRom.FromBytes(KohBootRoms.Dmg))` after each
   `new GameBoySystem(...)` — the harnesses currently construct and `RunFrame()` with
   no `PC` set.
3. Restore the `CompatTests` target in `build.proj` and the `compat-tests` CI job,
   including the `scripts/download-test-roms.sh` step. **Run the download script and
   confirm the ROMs are present**, or the suite silently skips again.
4. Register `acceptance/boot_regs-dmgABC.gb`: it asserts exactly the handoff register
   table Koh's boot ROM implements, making it the cheapest external check on that
   contract.
5. Keep `boot_div-*` and `boot_hwio-*` skipped **with the reason recorded**. `DIV` at
   handoff follows from the boot ROM's own cycle count, and Koh's boot ROM is an
   independent implementation rather than a transcription of Nintendo's — so it does
   not burn the same cycles. Those two pass only with a stock dump loaded.
