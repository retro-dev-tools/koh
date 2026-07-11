# SM83 Superoptimizer Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `tools/Koh.Superopt` — an offline tool that uses the Koh emulator as an equivalence oracle and the MIR layer for effects/cost to (slice 1) *discover* verified cheaper SM83 rewrites and (slice 2) *certify* rewrite rules against emulator ground truth.

**Architecture:** A console project references the emulator (oracle) and the compiler (MIR). An `Sm83Oracle` runs short byte sequences on `GameBoySystem` and compares live-out state over randomized inputs. A `Miner` enumerates straight-line, register-only sequences (MIR rejects memory/control ops), buckets them by observed behavior per live-out config, and reports the cheapest member of each bucket as the rewrite for its costlier siblings. A `RuleVerifier` reuses the oracle to check a list of declared rules.

**Tech Stack:** .NET 10 / C# 14, TUnit tests, `Koh.Emulator.Core` (`GameBoySystem`, `StepResult`), `Koh.Compiler` MIR (`MirDecoder`, `MirEffects`, `MirControl`).

## Global Constraints

- `TargetFramework` net10.0, `Nullable` enable, `LangVersion` 14, `TreatWarningsAsErrors` true — all inherited from root `Directory.Build.props`; do not restate in csproj.
- CI build must stay at **0 warnings** (`dotnet build Koh.Ci.slnf`).
- Tests are **TUnit**: `[Test] async Task Name()` (no `Async` suffix), `Assert.That(x).IsEqualTo(y)` / `.IsEmpty()` / `.IsTrue()`.
- The model identifier the agent runs as must not appear in commits, code, or docs.
- Commit messages: Conventional Commits, scope `superopt` (e.g. `feat(superopt): ...`).
- The emulator dependency lives only in `tools/Koh.Superopt`, never added to `Koh.Compiler`.
- Cost order is **bytes first, then T-cycles** (ROM is the scarce resource).
- Every candidate/seed must be straight-line and memory-free — enforced via `MirEffects`, not assumed.

---

### Task 1: Scaffold the tool and test projects

**Files:**
- Create: `tools/Koh.Superopt/Koh.Superopt.csproj`
- Create: `tools/Koh.Superopt/Program.cs` (temporary stub, replaced in Task 6)
- Create: `tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj`
- Create: `tests/Koh.Superopt.Tests/ScaffoldTests.cs` (deleted in Task 3)
- Modify: `Koh.slnx` (add both projects)
- Modify: `Koh.Ci.slnf` (add both projects)

**Interfaces:**
- Consumes: nothing.
- Produces: two buildable projects; the tool assembly `Koh.Superopt` referenced by the test project.

- [ ] **Step 1: Create the tool csproj**

`tools/Koh.Superopt/Koh.Superopt.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>koh-superopt</AssemblyName>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Koh.Compiler\Koh.Compiler.csproj" />
    <ProjectReference Include="..\..\src\Koh.Emulator.Core\Koh.Emulator.Core.csproj" />
  </ItemGroup>

</Project>
```
Note: no `PublishAot` — this tool is not shipped as a native binary. `MirDecoder`/`MirEffects` come transitively through `Koh.Compiler`.

- [ ] **Step 2: Create a temporary Program stub**

`tools/Koh.Superopt/Program.cs`:
```csharp
// Replaced by the report driver in Task 6.
System.Console.WriteLine("koh-superopt: nothing to do yet");
```

- [ ] **Step 3: Create the test csproj**

`tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UseTestingPlatformRunner>true</UseTestingPlatformRunner>
    <TestingPlatformCommandLineArguments>--ignore-exit-code 8</TestingPlatformCommandLineArguments>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\tools\Koh.Superopt\Koh.Superopt.csproj" />
    <ProjectReference Include="..\..\src\Koh.Compiler\Koh.Compiler.csproj" />
    <ProjectReference Include="..\..\src\Koh.Emulator.Core\Koh.Emulator.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="TUnit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Create a scaffold smoke test**

`tests/Koh.Superopt.Tests/ScaffoldTests.cs`:
```csharp
namespace Koh.Superopt.Tests;

public class ScaffoldTests
{
    [Test]
    public async Task Scaffold_builds_and_runs()
    {
        await Assert.That(1 + 1).IsEqualTo(2);
    }
}
```

- [ ] **Step 5: Register both projects in `Koh.slnx`**

Under `<Folder Name="/src/">` (or a new `/tools/` folder) add:
```xml
    <Project Path="tools/Koh.Superopt/Koh.Superopt.csproj" />
```
Under `<Folder Name="/tests/">` add:
```xml
    <Project Path="tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj" />
```

- [ ] **Step 6: Register both projects in `Koh.Ci.slnf`**

Add these two strings to the `projects` array (note doubled backslashes, JSON):
```json
      "tools\\Koh.Superopt\\Koh.Superopt.csproj",
      "tests\\Koh.Superopt.Tests\\Koh.Superopt.Tests.csproj",
```

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
    - `bool AreEquivalent(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Live live, int trials = 64, int seed = 0x5A83)`
    - `static bool SameLive(Sm83State x, Sm83State y, Live live)`

- [ ] **Step 1: Write the failing oracle test**

`tests/Koh.Superopt.Tests/Sm83OracleTests.cs`:
```csharp
using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class Sm83OracleTests
{
    // 3E 00 = LD A,0 (2 bytes, leaves flags untouched); AF = XOR A (1 byte, sets Z, clears NHC).
    private static readonly byte[] LdA0 = [0x3E, 0x00];
    private static readonly byte[] XorA = [0xAF];

    [Test]
    public async Task XorA_equals_LdA0_when_flags_are_dead()
    {
        var oracle = new Sm83Oracle();
        await Assert.That(oracle.AreEquivalent(LdA0, XorA, Live.A)).IsTrue();
    }

    [Test]
    public async Task XorA_differs_from_LdA0_when_flags_are_live()
    {
        var oracle = new Sm83Oracle();
        await Assert.That(oracle.AreEquivalent(LdA0, XorA, Live.A | Live.Flags)).IsFalse();
    }

    [Test]
    public async Task Run_reports_tcycles_and_final_state()
    {
        var oracle = new Sm83Oracle();
        var (state, tcycles) = oracle.Run(XorA, new Sm83State(0x42, 0, 0, 0, 0, 0, 0, 0, 0xFFFE));
        await Assert.That(state.A).IsEqualTo((byte)0);
        await Assert.That(tcycles).IsGreaterThan((ulong)0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter Sm83OracleTests`
Expected: FAIL — `Sm83Oracle` / `Sm83State` / `Live` do not exist.

- [ ] **Step 3: Write `Sm83State.cs`**

```csharp
namespace Koh.Superopt;

/// <summary>The 8-bit registers plus flags and SP — the observable machine state a straight-line,
/// memory-free SM83 sequence can read or write. Enough to define input states and compare outputs for
/// the equivalence oracle. Memory is deliberately out of scope; the enumerator rejects any sequence that
/// touches it (see <see cref="Sm83Alphabet"/>), so the register file is the whole observable state.</summary>
public readonly record struct Sm83State(
    byte A,
    byte F,
    byte B,
    byte C,
    byte D,
    byte E,
    byte H,
    byte L,
    ushort Sp
);

/// <summary>Which parts of <see cref="Sm83State"/> a caller cares about after a sequence runs — the
/// live-out set. A rewrite need only preserve these, so a smaller live-out admits more (and cheaper)
/// equivalents. Flags are compared as a set (the high nibble of F).</summary>
[Flags]
public enum Live : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    C = 1 << 2,
    D = 1 << 3,
    E = 1 << 4,
    H = 1 << 5,
    L = 1 << 6,
    Flags = 1 << 7,
    AllRegs = A | B | C | D | E | H | L,
    All = AllRegs | Flags,
}
```

- [ ] **Step 4: Write `Sm83Oracle.cs`**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Superopt;

/// <summary>
/// A concrete-execution equivalence oracle for short, straight-line, memory-free SM83 sequences, built on
/// the Koh emulator (<see cref="GameBoySystem"/>). It bakes a byte sequence into a minimal ROM-only
/// cartridge at the code entry, sets the register state, single-steps until control leaves the sequence,
/// and reads the machine state back. Two sequences are judged equivalent when they produce identical
/// live-out state across a batch of randomized inputs.
///
/// Concrete random testing is sound-for-refutation (a single disagreeing input proves inequivalence) and
/// a strong acceptance filter. A production tool would follow it with an exhaustive small-window or
/// symbolic check before trusting a mined rule blind — see the design note.
/// </summary>
public sealed class Sm83Oracle
{
    private const ushort CodeBase = 0x0150; // first byte after the cartridge header
    private const int MaxSteps = 64; // guard: the alphabet is straight-line and short

    /// <summary>Run <paramref name="code"/> from <paramref name="input"/>; return the resulting state and
    /// the total T-cycles executed. Stepping stops when the program counter leaves the byte range.</summary>
    public (Sm83State State, ulong TCycles) Run(ReadOnlySpan<byte> code, Sm83State input)
    {
        var rom = new byte[0x8000]; // 32 KiB, zeroed header ⇒ parses as a ROM-only cartridge
        code.CopyTo(rom.AsSpan(CodeBase));
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));

        ref var r = ref gb.Registers;
        (r.A, r.F, r.B, r.C, r.D, r.E, r.H, r.L, r.Sp) = (
            input.A,
            (byte)(input.F & 0xF0),
            input.B,
            input.C,
            input.D,
            input.E,
            input.H,
            input.L,
            input.Sp
        );
        r.Pc = CodeBase;

        var end = CodeBase + code.Length;
        ulong tcycles = 0;
        for (var i = 0; i < MaxSteps && r.Pc >= CodeBase && r.Pc < end; i++)
            tcycles += gb.StepInstruction().TCyclesRan;

        return (new Sm83State(r.A, r.F, r.B, r.C, r.D, r.E, r.H, r.L, r.Sp), tcycles);
    }

    /// <summary>True if <paramref name="a"/> and <paramref name="b"/> yield identical
    /// <paramref name="live"/> state across <paramref name="trials"/> randomized inputs.</summary>
    public bool AreEquivalent(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        Live live,
        int trials = 64,
        int seed = 0x5A83
    )
    {
        var random = new Random(seed);
        for (var t = 0; t < trials; t++)
        {
            var input = RandomState(random);
            if (!SameLive(Run(a, input).State, Run(b, input).State, live))
                return false;
        }
        return true;
    }

    private static Sm83State RandomState(Random random)
    {
        Span<byte> bytes = stackalloc byte[8];
        random.NextBytes(bytes);
        return new Sm83State(
            bytes[0],
            bytes[1],
            bytes[2],
            bytes[3],
            bytes[4],
            bytes[5],
            bytes[6],
            bytes[7],
            0xFFFE
        );
    }

    /// <summary>Compare only the live-out parts of two states; flags compared as the high nibble of F.</summary>
    public static bool SameLive(Sm83State x, Sm83State y, Live live)
    {
        if (live.HasFlag(Live.A) && x.A != y.A)
            return false;
        if (live.HasFlag(Live.B) && x.B != y.B)
            return false;
        if (live.HasFlag(Live.C) && x.C != y.C)
            return false;
        if (live.HasFlag(Live.D) && x.D != y.D)
            return false;
        if (live.HasFlag(Live.E) && x.E != y.E)
            return false;
        if (live.HasFlag(Live.H) && x.H != y.H)
            return false;
        if (live.HasFlag(Live.L) && x.L != y.L)
            return false;
        if (live.HasFlag(Live.Flags) && (x.F & 0xF0) != (y.F & 0xF0))
            return false;
        return true;
    }
}
```

- [ ] **Step 5: Run the oracle tests to verify they pass**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter Sm83OracleTests`
Expected: 3 passed.

- [ ] **Step 6: Commit**

```bash
git add tools/Koh.Superopt/Sm83State.cs tools/Koh.Superopt/Sm83Oracle.cs tests/Koh.Superopt.Tests/Sm83OracleTests.cs
git commit -m "feat(superopt): port the emulator equivalence oracle into the tool"
```

---

### Task 3: MIR-validated alphabet, cost, and enumerator

**Files:**
- Create: `tools/Koh.Superopt/Sm83Alphabet.cs`
- Create: `tools/Koh.Superopt/Enumerator.cs`
- Test: `tests/Koh.Superopt.Tests/EnumeratorTests.cs`
- Delete: `tests/Koh.Superopt.Tests/ScaffoldTests.cs`

**Interfaces:**
- Consumes: `Koh.Compiler.Backends.Sm83.Mir.MirDecoder`, `MirControl`; `Sm83Oracle` (for cost measurement).
- Produces:
  - `static class Sm83Alphabet` with `IReadOnlyList<byte[]> Ops` — each entry a single straight-line, memory-free, register-only instruction encoding.
  - `static bool IsStraightLineRegisterOnly(ReadOnlySpan<byte> code)` — true iff every decoded instruction has no memory effect and `Control == Fallthrough`.
  - `static class Enumerator` with `IEnumerable<byte[]> Sequences(int maxLength)` — the empty sequence, then all length-1..maxLength concatenations of `Sm83Alphabet.Ops`.

**Design note — alphabet:** kept small so the enumeration and CI tests stay fast; each entry is *validated* against MIR at first use, so it is genuinely straight-line/register-only rather than asserted by hand. `// ponytail: curated alphabet; widen for deeper manual mining.`

- [ ] **Step 1: Write the failing enumerator test**

`tests/Koh.Superopt.Tests/EnumeratorTests.cs`:
```csharp
using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class EnumeratorTests
{
    [Test]
    public async Task Every_alphabet_op_is_straight_line_and_register_only()
    {
        foreach (var op in Sm83Alphabet.Ops)
            await Assert.That(Sm83Alphabet.IsStraightLineRegisterOnly(op)).IsTrue();
    }

    [Test]
    public async Task Rejects_a_memory_op()
    {
        // 0x77 = LD (HL),A — a memory write.
        await Assert.That(Sm83Alphabet.IsStraightLineRegisterOnly([0x77])).IsFalse();
    }

    [Test]
    public async Task Rejects_a_control_op()
    {
        // 0x18 = JR r8 — an unconditional jump.
        await Assert.That(Sm83Alphabet.IsStraightLineRegisterOnly([0x18, 0x00])).IsFalse();
    }

    [Test]
    public async Task Sequences_include_empty_and_grow_to_bound()
    {
        var seqs = Enumerator.Sequences(2).ToList();
        var n = Sm83Alphabet.Ops.Count;
        // empty + N singletons + N*N pairs
        await Assert.That(seqs.Count).IsEqualTo(1 + n + n * n);
        await Assert.That(seqs[0].Length).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter EnumeratorTests`
Expected: FAIL — `Sm83Alphabet` / `Enumerator` do not exist.

- [ ] **Step 3: Write `Sm83Alphabet.cs`**

```csharp
using Koh.Compiler.Backends.Sm83.Mir;

namespace Koh.Superopt;

/// <summary>
/// The small, straight-line, register-only instruction alphabet the enumerator searches over. Kept
/// deliberately small so enumeration and CI stay fast; each entry is validated against the MIR decoder
/// (<see cref="IsStraightLineRegisterOnly"/>) so membership is a checked property, not a hand assertion.
/// </summary>
public static class Sm83Alphabet
{
    // A curated straight-line, register-only span: reg/reg moves, accumulator ALU, INC/DEC A, and a
    // couple of immediates. Enough to rediscover canonical peephole wins; widen for deeper manual mining.
    public static IReadOnlyList<byte[]> Ops { get; } =
    [
        [0x00], // NOP
        [0xAF], // XOR A       (A = 0, writes flags)
        [0xB7], // OR A,A      (flags from A, A unchanged)
        [0xA7], // AND A,A     (flags from A, A unchanged)
        [0x3C], // INC A
        [0x3D], // DEC A
        [0x78], // LD A,B
        [0x79], // LD A,C
        [0x47], // LD B,A
        [0x4F], // LD C,A
        [0x7F], // LD A,A
        [0x3E, 0x00], // LD A,0
    ];

    /// <summary>True iff every instruction the region decodes to is memory-free and falls through — the
    /// soundness precondition for the register-state oracle. Uses the shared MIR decoder so the property
    /// is derived from the canonical opcode semantics, not re-encoded here.</summary>
    public static bool IsStraightLineRegisterOnly(ReadOnlySpan<byte> code)
    {
        var program = MirDecoder.Decode(code.ToArray());
        foreach (var instruction in program.Instructions)
        {
            var e = instruction.Effects;
            if (e.MemRead || e.MemWrite || e.Control != MirControl.Fallthrough || e.SideEffect)
                return false;
        }
        return true;
    }
}
```

- [ ] **Step 4: Write `Enumerator.cs`**

```csharp
namespace Koh.Superopt;

/// <summary>Bounded enumeration of candidate sequences: the empty sequence, then every concatenation of
/// 1..maxLength alphabet ops, flattened to raw bytes. The empty sequence lets the miner discover
/// deletions (e.g. a flags-only op removed when flags are dead).</summary>
public static class Enumerator
{
    public static IEnumerable<byte[]> Sequences(int maxLength)
    {
        yield return [];
        var frontier = new List<byte[]> { [] };
        for (var length = 1; length <= maxLength; length++)
        {
            var next = new List<byte[]>();
            foreach (var prefix in frontier)
            foreach (var op in Sm83Alphabet.Ops)
            {
                var seq = new byte[prefix.Length + op.Length];
                prefix.CopyTo(seq, 0);
                op.CopyTo(seq, prefix.Length);
                next.Add(seq);
                yield return seq;
            }
            frontier = next;
        }
    }
}
```

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
    - constructor `Miner(int maxLength = 2, int probeCount = 24, int seed = 0x5A83)`
    - `IReadOnlyList<Rewrite> Mine(Live live)` — enumerate sequences, bucket by observed live-out behavior over a fixed probe battery, and for each bucket emit a `Rewrite` from every costlier member to the cheapest member; each emitted pair is re-verified with `Sm83Oracle.AreEquivalent` before inclusion.

**Design note — why bucketing:** grouping sequences by their observed behavior over a fixed probe battery makes discovery linear in the number of sequences (one oracle pass each) instead of quadratic pairwise. The probe battery is a fast, deterministic pre-filter; a full `AreEquivalent` re-check (64 random trials) guards every emitted pair against a coincidental bucket collision.

- [ ] **Step 1: Write the failing miner test**

`tests/Koh.Superopt.Tests/MinerTests.cs`:
```csharp
using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class MinerTests
{
    private static bool Has(IReadOnlyList<Rewrite> rs, byte[] from, byte[] to) =>
        rs.Any(r => r.From.AsSpan().SequenceEqual(from) && r.To.AsSpan().SequenceEqual(to));

    [Test]
    public async Task Rediscovers_LdA0_to_XorA_when_flags_are_dead()
    {
        var rewrites = new Miner(maxLength: 2).Mine(Live.AllRegs); // flags dead
        await Assert.That(Has(rewrites, [0x3E, 0x00], [0xAF])).IsTrue();
    }

    [Test]
    public async Task Declines_LdA0_to_XorA_when_flags_are_live()
    {
        var rewrites = new Miner(maxLength: 2).Mine(Live.All); // flags live
        await Assert.That(Has(rewrites, [0x3E, 0x00], [0xAF])).IsFalse();
    }

    [Test]
    public async Task Shrinks_double_move_to_single()
    {
        var rewrites = new Miner(maxLength: 2).Mine(Live.All);
        await Assert.That(Has(rewrites, [0x78, 0x78], [0x78])).IsTrue();
    }

    [Test]
    public async Task Every_rewrite_is_a_strict_improvement()
    {
        foreach (var r in new Miner(maxLength: 2).Mine(Live.All))
        {
            var betterBytes = r.To.Length < r.From.Length;
            var sameBytesFewerCycles = r.To.Length == r.From.Length && r.TCyclesSaved > 0;
            await Assert.That(betterBytes || sameBytesFewerCycles).IsTrue();
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter MinerTests`
Expected: FAIL — `Miner` / `Rewrite` do not exist.

- [ ] **Step 3: Write `Miner.cs`**

```csharp
using System.Text;

namespace Koh.Superopt;

/// <summary>One discovered rewrite: <see cref="From"/> is equivalent to the cheaper <see cref="To"/>
/// under <see cref="Live"/>, saving <see cref="BytesSaved"/> bytes then <see cref="TCyclesSaved"/>
/// T-cycles.</summary>
public readonly record struct Rewrite(byte[] From, byte[] To, Live Live, int BytesSaved, int TCyclesSaved);

/// <summary>
/// The discovery miner. Enumerates straight-line, register-only sequences up to a length bound, groups
/// them by the live-out behavior they exhibit over a fixed probe battery, and for each group reports the
/// cheapest member as the rewrite target for its costlier siblings — a bounded superoptimizer whose
/// equivalence oracle is the Koh emulator. Cost is bytes first, then T-cycles.
/// </summary>
public sealed class Miner
{
    private readonly int _maxLength;
    private readonly int _probeCount;
    private readonly int _seed;
    private readonly Sm83Oracle _oracle = new();

    public Miner(int maxLength = 2, int probeCount = 24, int seed = 0x5A83)
    {
        _maxLength = maxLength;
        _probeCount = probeCount;
        _seed = seed;
    }

    public IReadOnlyList<Rewrite> Mine(Live live)
    {
        var probes = Probes();

        // Bucket key = the concatenated live-out bytes across the probe battery. Two sequences share a
        // bucket iff they behave identically on every probe — a fast, deterministic pre-filter.
        var buckets = new Dictionary<string, List<(byte[] Code, int Bytes, ulong Cycles)>>();
        foreach (var code in Enumerator.Sequences(_maxLength))
        {
            var (signature, cycles) = Signature(code, probes, live);
            var entry = (Code: code, Bytes: code.Length, Cycles: cycles);
            if (!buckets.TryGetValue(signature, out var list))
                buckets[signature] = list = [];
            list.Add(entry);
        }

        var rewrites = new List<Rewrite>();
        foreach (var list in buckets.Values)
        {
            if (list.Count < 2)
                continue;
            // Cheapest member: fewest bytes, then fewest cycles.
            var best = list[0];
            foreach (var e in list)
                if (e.Bytes < best.Bytes || (e.Bytes == best.Bytes && e.Cycles < best.Cycles))
                    best = e;

            foreach (var e in list)
            {
                var strictlyCostlier =
                    e.Bytes > best.Bytes || (e.Bytes == best.Bytes && e.Cycles > best.Cycles);
                if (!strictlyCostlier)
                    continue;
                // Re-verify with random trials to reject a coincidental bucket collision.
                if (!_oracle.AreEquivalent(e.Code, best.Code, live))
                    continue;
                rewrites.Add(
                    new Rewrite(
                        e.Code,
                        best.Code,
                        live,
                        e.Bytes - best.Bytes,
                        (int)(e.Cycles - best.Cycles)
                    )
                );
            }
        }
        return rewrites;
    }

    /// <summary>The behavior signature of <paramref name="code"/>: live-out bytes across the probe
    /// battery, plus the (input-independent) T-cycle cost measured on the first probe.</summary>
    private (string Signature, ulong Cycles) Signature(byte[] code, Sm83State[] probes, Live live)
    {
        var sb = new StringBuilder();
        ulong cycles = 0;
        for (var i = 0; i < probes.Length; i++)
        {
            var (state, t) = _oracle.Run(code, probes[i]);
            if (i == 0)
                cycles = t;
            AppendLive(sb, state, live);
        }
        return (sb.ToString(), cycles);
    }

    private static void AppendLive(StringBuilder sb, Sm83State s, Live live)
    {
        if (live.HasFlag(Live.A)) sb.Append((char)s.A);
        if (live.HasFlag(Live.B)) sb.Append((char)s.B);
        if (live.HasFlag(Live.C)) sb.Append((char)s.C);
        if (live.HasFlag(Live.D)) sb.Append((char)s.D);
        if (live.HasFlag(Live.E)) sb.Append((char)s.E);
        if (live.HasFlag(Live.H)) sb.Append((char)s.H);
        if (live.HasFlag(Live.L)) sb.Append((char)s.L);
        if (live.HasFlag(Live.Flags)) sb.Append((char)(s.F & 0xF0));
        sb.Append('|');
    }

    private Sm83State[] Probes()
    {
        var random = new Random(_seed);
        var probes = new Sm83State[_probeCount];
        Span<byte> bytes = stackalloc byte[8];
        for (var i = 0; i < _probeCount; i++)
        {
            random.NextBytes(bytes);
            probes[i] = new Sm83State(
                bytes[0], bytes[1], bytes[2], bytes[3],
                bytes[4], bytes[5], bytes[6], bytes[7],
                0xFFFE
            );
        }
        return probes;
    }
}
```

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

- [ ] **Step 1: Write the failing formatting test**

`tests/Koh.Superopt.Tests/RewriteFormattingTests.cs`:
```csharp
using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class RewriteFormattingTests
{
    [Test]
    public async Task Describes_a_rewrite_with_hex_and_savings()
    {
        var r = new Rewrite([0x3E, 0x00], [0xAF], Live.AllRegs, 1, 4);
        var line = RewriteFormatting.Describe(r);
        await Assert.That(line).Contains("3E 00");
        await Assert.That(line).Contains("AF");
        await Assert.That(line).Contains("-1 byte");
    }

    [Test]
    public async Task Describes_a_deletion_as_removed()
    {
        var r = new Rewrite([0xB7], [], Live.AllRegs, 1, 4);
        await Assert.That(RewriteFormatting.Describe(r)).Contains("(removed)");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter RewriteFormattingTests`
Expected: FAIL — `RewriteFormatting` does not exist.

- [ ] **Step 3: Write `RewriteFormatting.cs`**

```csharp
namespace Koh.Superopt;

/// <summary>One-line human-readable rendering of a mined <see cref="Rewrite"/> for the report.</summary>
public static class RewriteFormatting
{
    public static string Describe(Rewrite r)
    {
        var from = Hex(r.From);
        var to = r.To.Length == 0 ? "(removed)" : Hex(r.To);
        var bytes = $"-{r.BytesSaved} byte{(r.BytesSaved == 1 ? "" : "s")}";
        var cycles = r.TCyclesSaved != 0 ? $", -{r.TCyclesSaved} T" : "";
        return $"{from,-12} -> {to,-12}  [{r.Live}]  {bytes}{cycles}";
    }

    private static string Hex(byte[] bytes) =>
        string.Join(' ', bytes.Select(b => b.ToString("X2"))); // "" for an empty sequence
}
```

- [ ] **Step 4: Run the formatting test to verify it passes**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter RewriteFormattingTests`
Expected: 2 passed.

- [ ] **Step 5: Replace `Program.cs` with the report driver**

```csharp
using Koh.Superopt;

// Mine rewrites under two peephole-relevant liveness contexts and print a report.
// Live.All: rewrites always safe (every register and flag preserved).
// Live.AllRegs: flags-dead rewrites (the common byte-scan peephole precondition).
int maxLength = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 2;

foreach (var live in new[] { Live.All, Live.AllRegs })
{
    Console.WriteLine($"== mined rewrites (maxLength={maxLength}, live-out={live}) ==");
    var rewrites = new Miner(maxLength).Mine(live);
    // Biggest byte win first, then biggest cycle win.
    foreach (var r in rewrites.OrderByDescending(r => r.BytesSaved).ThenByDescending(r => r.TCyclesSaved))
        Console.WriteLine("  " + RewriteFormatting.Describe(r));
    Console.WriteLine($"  ({rewrites.Count} rewrites)");
    Console.WriteLine();
}
```

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

### Task 6: The rule verifier (slice 2)

**Files:**
- Create: `tools/Koh.Superopt/RuleVerifier.cs`
- Test: `tests/Koh.Superopt.Tests/RuleVerifierTests.cs`

**Interfaces:**
- Consumes: `Sm83Oracle`, `Live`.
- Produces:
  - `readonly record struct RewriteRule(string Name, byte[] From, byte[] To, Live Live)`
  - `readonly record struct RuleVerdict(RewriteRule Rule, bool Holds, bool WellFormed)` — `WellFormed` is false when either side is not straight-line/register-only (outside the oracle's soundness domain).
  - `sealed class RuleVerifier` with `IReadOnlyList<RuleVerdict> Verify(IEnumerable<RewriteRule> rules)`.

**Design note:** slice 2 reuses the same oracle to certify declared rules against emulator ground truth — a regression guard that turns a bad or bit-rotted rule into a failing check. A rule whose sides fall outside the register-only domain is reported `WellFormed == false` rather than silently mis-judged.

- [ ] **Step 1: Write the failing verifier test**

`tests/Koh.Superopt.Tests/RuleVerifierTests.cs`:
```csharp
using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class RuleVerifierTests
{
    [Test]
    public async Task Confirms_a_valid_flags_dead_rule()
    {
        var rule = new RewriteRule("ld_a0_to_xor_a", [0x3E, 0x00], [0xAF], Live.AllRegs);
        var verdict = new RuleVerifier().Verify([rule]).Single();
        await Assert.That(verdict.WellFormed).IsTrue();
        await Assert.That(verdict.Holds).IsTrue();
    }

    [Test]
    public async Task Rejects_an_unsound_rule()
    {
        // Same rewrite but claiming flags are preserved — XOR A clobbers them, so it must not hold.
        var rule = new RewriteRule("bad", [0x3E, 0x00], [0xAF], Live.All);
        var verdict = new RuleVerifier().Verify([rule]).Single();
        await Assert.That(verdict.WellFormed).IsTrue();
        await Assert.That(verdict.Holds).IsFalse();
    }

    [Test]
    public async Task Flags_a_rule_outside_the_register_only_domain()
    {
        // 0x77 = LD (HL),A touches memory — outside the oracle's soundness domain.
        var rule = new RewriteRule("mem", [0x77], [0x77], Live.All);
        var verdict = new RuleVerifier().Verify([rule]).Single();
        await Assert.That(verdict.WellFormed).IsFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter RuleVerifierTests`
Expected: FAIL — `RuleVerifier` / `RewriteRule` / `RuleVerdict` do not exist.

- [ ] **Step 3: Write `RuleVerifier.cs`**

```csharp
namespace Koh.Superopt;

/// <summary>A declared rewrite rule: <see cref="From"/> may be replaced by <see cref="To"/> when only
/// <see cref="Live"/> is live-out.</summary>
public readonly record struct RewriteRule(string Name, byte[] From, byte[] To, Live Live);

/// <summary>The verdict for one rule: whether both sides are inside the oracle's register-only domain
/// (<see cref="WellFormed"/>) and, if so, whether the rewrite preserves the declared live-out
/// (<see cref="Holds"/>).</summary>
public readonly record struct RuleVerdict(RewriteRule Rule, bool Holds, bool WellFormed);

/// <summary>
/// Certifies declared rewrite rules against emulator ground truth — a regression guard for the peephole's
/// hand-written rules. A rule outside the straight-line, register-only domain is flagged
/// <see cref="RuleVerdict.WellFormed"/> = false rather than judged unsoundly.
/// </summary>
public sealed class RuleVerifier
{
    private readonly Sm83Oracle _oracle = new();

    public IReadOnlyList<RuleVerdict> Verify(IEnumerable<RewriteRule> rules)
    {
        var verdicts = new List<RuleVerdict>();
        foreach (var rule in rules)
        {
            var wellFormed =
                Sm83Alphabet.IsStraightLineRegisterOnly(rule.From)
                && Sm83Alphabet.IsStraightLineRegisterOnly(rule.To);
            var holds = wellFormed && _oracle.AreEquivalent(rule.From, rule.To, rule.Live);
            verdicts.Add(new RuleVerdict(rule, holds, wellFormed));
        }
        return verdicts;
    }
}
```

- [ ] **Step 4: Run the verifier tests to verify they pass**

Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj --filter RuleVerifierTests`
Expected: 3 passed.

- [ ] **Step 5: Full gate + commit**

Run: `dotnet build Koh.Ci.slnf`
Expected: 0 warnings.
Run: `dotnet test --project tests/Koh.Superopt.Tests/Koh.Superopt.Tests.csproj`
Expected: all passed.
```bash
git add tools/Koh.Superopt/RuleVerifier.cs tests/Koh.Superopt.Tests/RuleVerifierTests.cs
git commit -m "feat(superopt): rule verifier certifying rewrites against emulator ground truth"
```

---

### Task 7: Update the design note status

**Files:**
- Modify: `docs/superpowers/specs/2026-07-11-sm83-superopt-tool-design.md`

- [ ] **Step 1: Flip the status line and check off delivered slices**

Change `Status: **design approved, implementing.**` to `Status: **landed** (miner + verifier in \`tools/Koh.Superopt\`).` and add a short "What landed" note mirroring the two slices.

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-07-11-sm83-superopt-tool-design.md
git commit -m "docs(superopt): mark the tool design landed"
```

---

## Self-Review

**Spec coverage:**
- Home/layering (tool references emulator+compiler, not the reverse) → Task 1. ✓
- Oracle ported + tcycles + memory boundary → Task 2, Task 3 (`IsStraightLineRegisterOnly`). ✓
- Cost bytes-then-cycles from MIR length + measured T-cycles → Task 2 (`Run` returns TCycles), Task 4 (cost compare). ✓
- MIR-driven effects/rejection → Task 3. ✓
- Slice 1 miner + report → Tasks 4, 5. ✓
- Slice 2 verifier → Task 6. ✓
- Regression behaviors (rediscovers XOR A flags-dead; declines flags-live; shrinks double move) → Task 4. ✓
- `ponytail:` random-filter note → present in `Sm83Oracle` summary (Task 2) and `Sm83Alphabet` note (Task 3). ✓
- Non-goals not built. ✓

**Placeholder scan:** no TBD/TODO/"handle edge cases"; every code step shows complete code. ✓

**Type consistency:** `Sm83State`, `Live`, `Sm83Oracle.Run` (returns `(Sm83State State, ulong TCycles)`), `AreEquivalent`, `Sm83Alphabet.Ops`/`IsStraightLineRegisterOnly`, `Enumerator.Sequences`, `Miner.Mine`/`Rewrite`, `RewriteFormatting.Describe`, `RewriteRule`/`RuleVerdict`/`RuleVerifier.Verify` — names and signatures match across tasks. ✓

**Note on measured cycles:** `TCyclesRan` is a `ulong`; `Rewrite.TCyclesSaved` narrows to `int` via an explicit cast — safe because sequences are ≤ a handful of instructions (well under `int` range).
