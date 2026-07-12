# SM83 Superoptimizer Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `tools/Koh.Superopt` — an offline tool that uses the Koh emulator as an equivalence oracle and the MIR layer for effects to *discover* verified cheaper SM83 rewrites and report them for a human to fold into the peephole by hand.

**Architecture:** A console project references the emulator (oracle) and the compiler (MIR). An `Sm83Oracle` runs short byte sequences on `GameBoySystem` and compares live-out state over randomized inputs. A `Miner` enumerates straight-line, register-only sequences (MIR rejects memory/control ops), buckets them by observed behavior per live-out config, and reports the cheapest member of each bucket as the rewrite for its costlier siblings.

**Tech Stack:** .NET 10 / C# 14, TUnit tests, `Koh.Emulator.Core` (`GameBoySystem`, `StepResult`), `Koh.Compiler` MIR (`MirDecoder`, `MirEffects`, `MirControl`).

## Global Constraints

Follow `CLAUDE.md`/`AGENTS.md` (build/test commands, TUnit conventions, commit format, no
model identifier in commits/code). Task-specific: the emulator dependency lives only in
`tools/Koh.Superopt`, never `Koh.Compiler`.

---

### Task 1: Scaffold the tool and test projects

**Files:**
- Create: `tools/Koh.Superopt/Koh.Superopt.csproj`
- Create: `tools/Koh.Superopt/Program.cs` (temporary stub, replaced in Task 5)
- Create: `tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj`
- Create: `tests/Koh.Superopt.Tests/ScaffoldTests.cs` (deleted in Task 3)
- Modify: `Koh.slnx` (add both projects)
- Modify: `Koh.Ci.slnf` (add both projects)

**Interfaces:**
- Consumes: nothing.
- Produces: two buildable projects; the tool assembly `Koh.Superopt` referenced by the test project.

- [ ] **Step 1: Create the tool csproj.** Exe, `AssemblyName=koh-superopt`, `InvariantGlobalization`;
  references `Koh.Compiler` and `Koh.Emulator.Core`. No `PublishAot` — this tool is not shipped as a
  native binary. Full source: `tools/Koh.Superopt/Koh.Superopt.csproj`.

- [ ] **Step 2: Create a temporary Program stub** printing `"koh-superopt: nothing to do yet"`,
  replaced by the report driver in Task 5.

- [ ] **Step 3: Create the test csproj** (TUnit, `UseTestingPlatformRunner`), referencing the tool,
  `Koh.Compiler`, and `Koh.Emulator.Core`. Full source: `tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj`.

- [ ] **Step 4: Create a scaffold smoke test** (`ScaffoldTests.cs`, asserts `1 + 1 == 2`) to prove the
  project builds and runs before real code lands.

- [ ] **Step 5: Register both projects in `Koh.slnx`** — `tools/Koh.Superopt/Koh.Superopt.csproj` under
  a `/tools/` folder, `tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj` under `/tests/`.

- [ ] **Step 6: Register both projects in `Koh.Ci.slnf`** — add both paths to the `projects` array
  (doubled backslashes, JSON).

- [ ] **Step 7: Build and run the smoke test**

Run: `dotnet build Koh.Ci.slnf`
Expected: build succeeds, 0 warnings.
Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj`
Expected: 1 passed.

- [ ] **Step 8: Commit**

```bash
git add tools/Koh.Superopt tests/Koh.Superopt.Tests Koh.slnx Koh.Ci.slnf
git commit -m "feat(superopt): scaffold tools/Koh.Superopt and its test project"
```

---

### Task 2: The equivalence oracle

**Files:**
- Create: `tools/Koh.Superopt/Sm83State.cs`
- Create: `tools/Koh.Superopt/Sm83Oracle.cs`
- Test: `tests/Koh.Superopt.Tests/Sm83OracleTests.cs`

**Interfaces:**
- Consumes: `Koh.Emulator.Core.GameBoySystem`, `Koh.Emulator.Core.HardwareMode`, `Koh.Emulator.Core.Cartridge.CartridgeFactory`.
- Produces:
  - `readonly record struct Sm83State(byte A, byte F, byte B, byte C, byte D, byte E, byte H, byte L, ushort Sp)`
  - `[Flags] enum Live : byte { None=0, A=1, B=2, C=4, D=8, E=16, H=32, L=64, Flags=128, AllRegs=A|B|C|D|E|H|L, All=AllRegs|Flags }`
  - `sealed class Sm83Oracle` with:
    - `(Sm83State State, ulong TCycles) Run(ReadOnlySpan<byte> code, Sm83State input)`
    - `bool AreEquivalent(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Live live, int seed = 0x5A83)`
    - `static bool SameLive(Sm83State x, Sm83State y, Live live)`

- [ ] **Step 1: Write the failing oracle test.** Covers: `XOR A` equals `LD A,0` when flags are dead,
  differs when flags are live, and `Run` reports T-cycles and final state. Full source:
  `tests/Koh.Superopt.Tests/Sm83OracleTests.cs`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter Sm83OracleTests`
Expected: FAIL — `Sm83Oracle` / `Sm83State` / `Live` do not exist.

- [ ] **Step 3: Write `Sm83State.cs`** — the register/flags/SP record plus the `Live` flags enum (the
  observable state a straight-line, memory-free sequence can read or write). Full source:
  `tools/Koh.Superopt/Sm83State.cs`.

- [ ] **Step 4: Write `Sm83Oracle.cs`.** Bakes a byte sequence into a minimal ROM-only cartridge at the
  code entry, sets register state, single-steps `GameBoySystem` until control leaves the sequence
  (`MaxSteps` guard), and reads state + accumulated `TCyclesRan` back. `AreEquivalent` runs both
  sequences over randomized inputs and compares only the live-out parts via `SameLive`. Concrete random
  testing is sound-for-refutation, not a proof — `// ponytail: random-input filter only; add an
  exhaustive small-window or symbolic check before auto-trusting a mined rule.` Full source:
  `tools/Koh.Superopt/Sm83Oracle.cs`.

- [ ] **Step 5: Run the oracle tests to verify they pass**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter Sm83OracleTests`
Expected: 3 passed.

- [ ] **Step 6: Commit**

```bash
git add tools/Koh.Superopt/Sm83State.cs tools/Koh.Superopt/Sm83Oracle.cs tests/Koh.Superopt.Tests/Sm83OracleTests.cs
git commit -m "feat(superopt): port the emulator equivalence oracle into the tool"
```

---

### Task 3: MIR-validated alphabet and enumerator

**Files:**
- Create: `tools/Koh.Superopt/Sm83Alphabet.cs`
- Create: `tools/Koh.Superopt/Enumerator.cs`
- Test: `tests/Koh.Superopt.Tests/EnumeratorTests.cs`
- Delete: `tests/Koh.Superopt.Tests/ScaffoldTests.cs`

**Interfaces:**
- Consumes: `Koh.Compiler.Backends.Sm83.Mir.MirDecoder`, `MirControl`.
- Produces:
  - `static class Sm83Alphabet` with `IReadOnlyList<byte[]> Ops` — each entry a single straight-line, memory-free, register-only instruction encoding.
  - `static bool IsStraightLineRegisterOnly(ReadOnlySpan<byte> code)` — true iff every decoded instruction has no memory effect and `Control == Fallthrough`.
  - `static class Enumerator` with `IEnumerable<byte[]> Sequences(int maxLength)` — the empty sequence, then all length-1..maxLength concatenations of `Sm83Alphabet.Ops`.

**Design note — alphabet:** kept small so the enumeration and CI tests stay fast; each entry is *validated* against MIR at first use, so it is genuinely straight-line/register-only rather than asserted by hand. `// ponytail: curated alphabet; widen for deeper manual mining.`

- [ ] **Step 1: Write the failing enumerator test.** Covers: every alphabet op is straight-line/register-only;
  a memory op (`LD (HL),A`) and a control op (`JR`) are rejected; `Sequences(2)` has the expected count
  (`1 + n + n*n`) and starts with the empty sequence. Full source: `tests/Koh.Superopt.Tests/EnumeratorTests.cs`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter EnumeratorTests`
Expected: FAIL — `Sm83Alphabet` / `Enumerator` do not exist.

- [ ] **Step 3: Write `Sm83Alphabet.cs`** — a curated straight-line, register-only op set (reg/reg
  moves, accumulator ALU, INC/DEC A, a couple of immediates), each entry checked via `MirDecoder`/
  `MirEffects` so membership is a verified property, not a hand assertion. Full source:
  `tools/Koh.Superopt/Sm83Alphabet.cs`.

- [ ] **Step 4: Write `Enumerator.cs`** — bounded enumeration: the empty sequence, then every
  concatenation of 1..maxLength alphabet ops, flattened to raw bytes. The empty sequence lets the miner
  discover deletions. Full source: `tools/Koh.Superopt/Enumerator.cs`.

- [ ] **Step 5: Delete the scaffold test**

```bash
git rm tests/Koh.Superopt.Tests/ScaffoldTests.cs
```

- [ ] **Step 6: Run the enumerator tests to verify they pass**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter EnumeratorTests`
Expected: 4 passed.

- [ ] **Step 7: Commit**

```bash
git add tools/Koh.Superopt/Sm83Alphabet.cs tools/Koh.Superopt/Enumerator.cs tests/Koh.Superopt.Tests/EnumeratorTests.cs
git commit -m "feat(superopt): MIR-validated alphabet and bounded enumerator"
```

---

### Task 4: The miner (behavior bucketing + rewrite discovery)

**Files:**
- Create: `tools/Koh.Superopt/Miner.cs`
- Test: `tests/Koh.Superopt.Tests/MinerTests.cs`

**Interfaces:**
- Consumes: `Sm83Oracle`, `Enumerator`, `Sm83State`, `Live`.
- Produces:
  - `readonly record struct Rewrite(byte[] From, byte[] To, Live Live, int BytesSaved, int TCyclesSaved)`
  - `sealed class Miner` with:
    - constructor `Miner(int maxLength = 2)`
    - `IReadOnlyList<Rewrite> Mine(Live live)` — enumerate sequences, bucket by observed live-out behavior over a fixed probe battery, and for each bucket emit a `Rewrite` from every costlier member to the cheapest member; each emitted pair is re-verified with `Sm83Oracle.AreEquivalent` (an independent seed) before inclusion.

**Design note — why bucketing:** grouping sequences by their observed behavior over a fixed probe battery makes discovery linear in the number of sequences (one oracle pass each) instead of quadratic pairwise. The probe battery is a fast, deterministic pre-filter; a full `AreEquivalent` re-check guards every emitted pair against a coincidental bucket collision.

- [ ] **Step 1: Write the failing miner test.** Covers: rediscovers `LD A,0 → XOR A` when flags are
  dead; declines it when flags are live; shrinks `LD A,B; LD A,B` to `LD A,B`; every emitted rewrite is
  a strict improvement (fewer bytes, or same bytes and fewer cycles). Full source:
  `tests/Koh.Superopt.Tests/MinerTests.cs`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter MinerTests`
Expected: FAIL — `Miner` / `Rewrite` do not exist.

- [ ] **Step 3: Write `Miner.cs`.** Bucket key = the concatenated live-out bytes across the probe
  battery (a fixed set of random `Sm83State`s from `Sm83Oracle.RandomState`); two sequences share a
  bucket iff they behave identically on every probe. Within each multi-member bucket, `MinBy`
  (bytes, then cycles) picks the cheapest member; every strictly costlier member becomes a `Rewrite`
  to it once independently re-verified. Full source: `tools/Koh.Superopt/Miner.cs`.

- [ ] **Step 4: Run the miner tests to verify they pass**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter MinerTests`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add tools/Koh.Superopt/Miner.cs tests/Koh.Superopt.Tests/MinerTests.cs
git commit -m "feat(superopt): behavior-bucketing rewrite miner over the emulator oracle"
```

---

### Task 5: The report driver (`Program`)

**Files:**
- Modify: `tools/Koh.Superopt/Program.cs` (replace the Task 1 stub)
- Create: `tools/Koh.Superopt/RewriteFormatting.cs`
- Test: `tests/Koh.Superopt.Tests/RewriteFormattingTests.cs`

**Interfaces:**
- Consumes: `Miner`, `Rewrite`, `Live`.
- Produces:
  - `static class RewriteFormatting` with `static string Describe(Rewrite r)` returning a one-line human-readable summary (hex bytes for From/To, `(removed)` when To is empty, live-out, savings).

- [ ] **Step 1: Write the failing formatting test.** Covers: a rewrite renders hex bytes and the byte
  savings; a deletion (empty `To`) renders `(removed)`. Full source:
  `tests/Koh.Superopt.Tests/RewriteFormattingTests.cs`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter RewriteFormattingTests`
Expected: FAIL — `RewriteFormatting` does not exist.

- [ ] **Step 3: Write `RewriteFormatting.cs`** — hex-encodes `From`/`To` (empty `To` renders
  `(removed)`), then appends the live-out set and bytes/cycles saved. Full source:
  `tools/Koh.Superopt/RewriteFormatting.cs`.

- [ ] **Step 4: Run the formatting test to verify it passes**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter RewriteFormattingTests`
Expected: 2 passed.

- [ ] **Step 5: Replace `Program.cs` with the report driver.** Mines rewrites under two
  peephole-relevant liveness contexts (`Live.All` — always safe; `Live.AllRegs` — flags-dead) and
  prints each, ordered by byte savings then cycle savings. Full source: `tools/Koh.Superopt/Program.cs`.

- [ ] **Step 6: Run the tool and confirm it prints rewrites**

Run: `dotnet run --project tools/Koh.Superopt`
Expected: two report sections; the `Live.AllRegs` section includes a line `3E 00 -> AF` and the `Live.All` section does not.

- [ ] **Step 7: Full build + test gate**

Run: `dotnet build Koh.Ci.slnf`
Expected: 0 warnings.
Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj`
Expected: all passed.

- [ ] **Step 8: Commit**

```bash
git add tools/Koh.Superopt/Program.cs tools/Koh.Superopt/RewriteFormatting.cs tests/Koh.Superopt.Tests/RewriteFormattingTests.cs
git commit -m "feat(superopt): report driver printing mined rewrites by liveness context"
```

---

### Task 6: Update the design note status

**Files:**
- Modify: `docs/superpowers/specs/2026-07-11-sm83-superopt-tool-design.md`

- [ ] **Step 1: Flip the status line and check off delivered slices**

Change `Status: **design approved, implementing.**` to `Status: **landed** (miner in \`tools/Koh.Superopt\`).` and add a short "What landed" note.

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-07-11-sm83-superopt-tool-design.md
git commit -m "docs(superopt): mark the tool design landed"
```
