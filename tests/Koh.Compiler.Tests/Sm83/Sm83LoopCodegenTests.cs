using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Sm83;

/// <summary>
/// SM83 backend loop-codegen perf/correctness suite (emulator-accuracy-stabilization Package A, layers
/// 1-3: loop-carried induction/pointer register residency). Mirrors the harness pattern in
/// <c>Samples.Cube3dTests</c>/<c>MemRuntimeTests</c>: real Koh C# frontend -&gt; IR (optimized, matching
/// <c>CompilerDriver</c>'s default path) -&gt; SM83 backend -&gt; linker -&gt; <see cref="GameBoySystem"/>,
/// reading results back out of WRAM and measuring T-cycles ("dots") via <see cref="TickCounter"/> - the
/// repo's first codegen perf-regression harness.
/// </summary>
public class Sm83LoopCodegenTests
{
    // ---- Harness --------------------------------------------------------------------------------

    private static IrModule Frontend(string src, string file)
    {
        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(SourceText.From(src, file), diagnostics);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException(
                string.Join("; ", diagnostics.Select(d => d.Message))
            );
        // CLAUDE.md: new lowering must verify clean IR (IrVerifier is not run inside CompilerDriver).
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );
        return module;
    }

    /// <summary>Compile <paramref name="src"/> through the real pipeline (optimized, matching
    /// <c>CompilerDriver.Compile</c>'s default) and boot it on a fresh <see cref="GameBoySystem"/>
    /// positioned at the ROM entry point, ready to single-step.</summary>
    private static GameBoySystem Load(string src, string file, out int start)
    {
        var module = Frontend(src, file);
        IrOptimizer.Optimize(module);
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link([new LinkerInput("loop", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    /// <summary>Run to completion (falling off the end of Main back past the loaded code, or past the
    /// 32KB fixed-bank ROM window) and return the T-cycle ("dots") cost of the run.</summary>
    private static ulong Run(GameBoySystem gb, int start)
    {
        ulong startT = gb.Cpu.TotalTCycles;
        for (int steps = 0; steps < 5_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                return gb.Cpu.TotalTCycles - startT;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not halt within the step budget");
    }

    private static byte ReadByte(GameBoySystem gb, int address) =>
        gb.DebugReadByte((ushort)address);

    /// <summary>Reusable dots-per-iteration measurement: runs a program to completion, reads back a
    /// result byte for a sanity assertion, and reports total dots and dots/iteration given the known
    /// iteration count. This is the repo's first codegen perf-regression helper (Package A of the
    /// emulator-accuracy-stabilization plan) - see <c>MemRuntimeTests</c> for the sibling Mem-API harness.</summary>
    private sealed class TickCounter
    {
        public required ulong TotalDots { get; init; }
        public required int Iterations { get; init; }
        public required GameBoySystem Gb { get; init; }
        public double DotsPerIteration => (double)TotalDots / Iterations;

        public static TickCounter Measure(string src, string file, int iterations)
        {
            var gb = Load(src, file, out int start);
            ulong dots = Run(gb, start);
            return new TickCounter
            {
                TotalDots = dots,
                Iterations = iterations,
                Gb = gb,
            };
        }
    }

    // ---- Baseline measurements (pre-layer-1, unmodified codegen) --------------------------------
    //
    // These establish the "before" numbers the layer 1/2 perf-regression ceilings are set from (with
    // ~25% headroom), per the plan. Not perf assertions themselves - see the Baseline_* tests' Console
    // output captured via the TestResults report, and the layer-gated ceiling tests further down.

    [Test]
    public async Task Baseline_CountedConstAddrStoreLoop()
    {
        // Layer 1's canonical shape: an i8 loop-carried induction phi (Ult compare against a constant,
        // so the compare's Eq/Ne register-C scratch never fires - see EmitCompare audit) whose body
        // does one gentle add and one store through a *constant* address (no dynamic gep - that is
        // layer 2's pointer-residency shape).
        const string src = """
            public static class Program {
                public static void Main() {
                    for (byte i = 0; i < 100; i++) {
                        *(byte*)0xC100 = i;
                    }
                }
            }
            """;
        var m = TickCounter.Measure(src, "baseline1.cs", 100);
        await Assert.That(ReadByte(m.Gb, 0xC100)).IsEqualTo((byte)99);
        // Measured pre-layer-1: ~177.6 dots/iter. Post-layer-1 (this build): ~149.6 dots/iter (~16%
        // fewer) - see CountedConstAddrStoreLoop_MeetsCeiling below for the gated regression check.
    }

    // ---- Layer 1 perf-regression ceilings (measured post-layer-1, ~25% headroom) -----------------

    [Test]
    public async Task CountedConstAddrStoreLoop_MeetsCeiling()
    {
        const string src = """
            public static class Program {
                public static void Main() {
                    for (byte i = 0; i < 100; i++) {
                        *(byte*)0xC100 = i;
                    }
                }
            }
            """;
        var m = TickCounter.Measure(src, "ceiling1.cs", 100);
        await Assert.That(ReadByte(m.Gb, 0xC100)).IsEqualTo((byte)99);
        // Measured ~149.6 dots/iter after layer 1; ceiling set with ~25% headroom.
        await Assert.That(m.DotsPerIteration).IsLessThanOrEqualTo(190.0);
    }

    // ---- Correctness: the loop shapes the plan calls out by name ----------------------------------

    [Test]
    public async Task InductionValueUsedAfterLoopExit()
    {
        // The induction value itself (not a separate exit-merge phi) is read directly in the block
        // right after the loop - exactly the shape a naive "always read the register" residency would
        // get wrong if the register's live range weren't proven safe end-to-end (see
        // Sm83FunctionAllocation's LiveBlocksOf/IsLoopSafeInstruction).
        const string src = """
            public static class Program {
                public static void Main() {
                    byte i = 0;
                    while (i < 50) {
                        i = (byte)(i + 1);
                    }
                    *(byte*)0xC100 = i;
                }
            }
            """;
        var gb = Load(src, "exit1.cs", out int start);
        Run(gb, start);
        await Assert.That(ReadByte(gb, 0xC100)).IsEqualTo((byte)50);
    }

    [Test]
    public async Task InductionValueUsedAfterLoopExit_WithInterveningClobber_StillCorrect()
    {
        // The negative case: a call between the loop and the use of the induction value. Whether or not
        // this specific candidate gets admitted to residency, correctness must hold either way - if
        // admission's liveness scan is wrong, this either corrupts i (register clobbered by the call)
        // or throws (the call is not in the register-safe whitelist, so admission should simply decline).
        const string src = """
            public static class Program {
                public static byte Touch(byte x) {
                    return (byte)(x + 5);
                }
                public static void Main() {
                    byte i = 0;
                    while (i < 50) {
                        i = (byte)(i + 1);
                    }
                    byte touched = Touch(i);
                    *(byte*)0xC100 = touched;
                }
            }
            """;
        var gb = Load(src, "exit2.cs", out int start);
        Run(gb, start);
        await Assert.That(ReadByte(gb, 0xC100)).IsEqualTo((byte)55);
    }

    [Test]
    public async Task NestedLoops()
    {
        const string src = """
            public static class Program {
                public static void Main() {
                    byte total = 0;
                    for (byte i = 0; i < 10; i++) {
                        for (byte j = 0; j < 10; j++) {
                            total = (byte)(total + 1);
                        }
                    }
                    *(byte*)0xC100 = total;
                }
            }
            """;
        var gb = Load(src, "nested1.cs", out int start);
        Run(gb, start);
        await Assert.That(ReadByte(gb, 0xC100)).IsEqualTo((byte)100);
    }

    [Test]
    public async Task EarlyBreakExitEdge()
    {
        // An extra block (the break check) joins the natural loop body; the induction value is read
        // directly after the loop, and the compare inside the break check is Eq (pulls C from the pool
        // - see the EmitCompare audit) while the loop's own compare is Ult (leaves C available) - a
        // mixed-compare loop, exercising the per-candidate pool selection.
        const string src = """
            public static class Program {
                public static void Main() {
                    byte i = 0;
                    while (i < 100) {
                        if (i == 42) {
                            break;
                        }
                        i = (byte)(i + 1);
                    }
                    *(byte*)0xC100 = i;
                }
            }
            """;
        var gb = Load(src, "break1.cs", out int start);
        Run(gb, start);
        await Assert.That(ReadByte(gb, 0xC100)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task PointerCopyLoop_RegressionSafe()
    {
        // Layer 2's shape (a dynamic byte* induction pointer), not yet residency-optimized by layer 1 -
        // this is a *correctness* regression guard: the pointer phi must not be misidentified as a
        // layer-1 byte induction candidate (it is 16-bit, and its loads/stores go through a dynamic
        // address, not a literal constant - both excluded by admission) and must still compile/run
        // correctly with layer 1 active elsewhere in the same function.
        const string src = """
            public static unsafe class Program {
                public static void Main() {
                    byte* src = (byte*)0xC100;
                    byte* dst = (byte*)0xC200;
                    *src = 0;
                    for (byte i = 0; i < 16; i++) {
                        src[i] = i;
                    }
                    byte count = 16;
                    while (count != 0) {
                        *dst = *src;
                        dst++;
                        src++;
                        count--;
                    }
                }
            }
            """;
        var gb = Load(src, "copy1.cs", out int start);
        Run(gb, start);
        for (int i = 0; i < 16; i++)
            await Assert.That(ReadByte(gb, 0xC200 + i)).IsEqualTo((byte)i);
    }

    // ---- Layer 2: stride-1 pointer residency ---------------------------------------------------

    [Test]
    public async Task TwoPointerCopyLoop_MeetsCeiling()
    {
        // The task's canonical shape: `src` fuses into its one Load (Hl, `ld a,(hl+)`), `dst` fuses into
        // its one Store (De, `ld (de),a` + `inc de`); `count`'s Ne compare means layer 1 may lose C/D/E
        // to layer 2's De claim - see SelectLoopPointerResidents' region comment on why layer 2 goes first.
        const string src = """
            public static unsafe class Program {
                public static void Main() {
                    byte* s = (byte*)0xC100;
                    byte* d = (byte*)0xC200;
                    for (byte i = 0; i < 32; i++) {
                        s[i] = i;
                    }
                    byte count = 32;
                    while (count != 0) {
                        *d = *s;
                        d++;
                        s++;
                        count--;
                    }
                }
            }
            """;
        var m = TickCounter.Measure(src, "ptrcopy1.cs", 32);
        for (int i = 0; i < 32; i++)
            await Assert.That(ReadByte(m.Gb, 0xC200 + i)).IsEqualTo((byte)i);
        // Measured ~590.25 dots/iter (vs. the ~880-900 dots/byte pre-layer-2 baseline for this shape -
        // see MemRuntimeTests' cost test and PointerCopyLoop_RegressionSafe); ceiling set with ~25% headroom.
        await Assert.That(m.DotsPerIteration).IsLessThanOrEqualTo(740.0);
    }

    [Test]
    public async Task SinglePointerFillLoop()
    {
        // Only one pointer candidate (store-fused) - always claims Hl (cheapest, no ambiguity with a
        // sibling load candidate to arbitrate against).
        const string src = """
            public static unsafe class Program {
                public static void Main() {
                    byte* d = (byte*)0xC100;
                    byte count = 20;
                    while (count != 0) {
                        *d = 0xAA;
                        d++;
                        count--;
                    }
                }
            }
            """;
        var gb = Load(src, "fill1.cs", out int start);
        Run(gb, start);
        for (int i = 0; i < 20; i++)
            await Assert.That(ReadByte(gb, 0xC100 + i)).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task PointerUsedAfterLoopExit()
    {
        // `d`'s WRAM home is refreshed every iteration by the (unmodified) storeback - see
        // SelectLoopPointerResidents' region comment on why no exit-edge sync is needed at all: a fresh
        // dereference after the loop is its own new Load instruction, unrelated to the resident register,
        // and just reads the always-current home slot normally.
        const string src = """
            public static unsafe class Program {
                public static void Main() {
                    byte* d = (byte*)0xC100;
                    byte count = 10;
                    while (count != 0) {
                        *d = 0xAA;
                        d++;
                        count--;
                    }
                    *d = 0xFF;
                }
            }
            """;
        var gb = Load(src, "fillexit1.cs", out int start);
        Run(gb, start);
        for (int i = 0; i < 10; i++)
            await Assert.That(ReadByte(gb, 0xC100 + i)).IsEqualTo((byte)0xAA);
        await Assert.That(ReadByte(gb, 0xC100 + 10)).IsEqualTo((byte)0xFF); // d landed at 0xC10A
    }

    [Test]
    public async Task NonQualifyingMiddleInstruction_NegativeAdmission_StillCorrect()
    {
        // A call inside the loop body clobbers everything and is not in IsPointerLoopSafeInstruction's
        // whitelist, so neither pointer is admitted to residency - a correctness regression guard: whether
        // or not admission declines (it should), the copy-with-transform must still execute correctly.
        const string src = """
            public static unsafe class Program {
                public static byte Touch(byte x) {
                    return (byte)(x + 1);
                }
                public static void Main() {
                    byte* s = (byte*)0xC100;
                    byte* d = (byte*)0xC200;
                    for (byte i = 0; i < 16; i++) {
                        s[i] = i;
                    }
                    byte count = 16;
                    while (count != 0) {
                        *d = Touch(*s);
                        d++;
                        s++;
                        count--;
                    }
                }
            }
            """;
        var gb = Load(src, "negadmit1.cs", out int start);
        Run(gb, start);
        for (int i = 0; i < 16; i++)
            await Assert.That(ReadByte(gb, 0xC200 + i)).IsEqualTo((byte)(i + 1));
    }

    [Test]
    public async Task OverlappingRegionCopy_ForwardSemanticsPreserved()
    {
        // dst sits 2 bytes ahead of src (an overlapping region): a plain forward byte-by-byte loop is NOT
        // memmove-safe here - by the time it reads src[2]/src[3], those addresses were already overwritten
        // by the loop's own earlier iterations. Layer 2 must reproduce exactly this (non-obviously-"correct"
        // but source-faithful) forward semantics, not silently fix or break it via reordering/batching.
        const string src = """
            public static unsafe class Program {
                public static void Main() {
                    byte* src = (byte*)0xC100;
                    byte* dst = (byte*)0xC102;
                    src[0] = 10;
                    src[1] = 20;
                    src[2] = 30;
                    src[3] = 40;
                    byte count = 4;
                    while (count != 0) {
                        *dst = *src;
                        dst++;
                        src++;
                        count--;
                    }
                }
            }
            """;
        var gb = Load(src, "overlap1.cs", out int start);
        Run(gb, start);
        // C102=src[0]=10; C103=src[1]=20; C104=src[2], but src[2]==C102 was already overwritten to 10 by
        // the time this iteration reads it; C105=src[3]==C103, already overwritten to 20.
        await Assert.That(ReadByte(gb, 0xC102)).IsEqualTo((byte)10);
        await Assert.That(ReadByte(gb, 0xC103)).IsEqualTo((byte)20);
        await Assert.That(ReadByte(gb, 0xC104)).IsEqualTo((byte)10);
        await Assert.That(ReadByte(gb, 0xC105)).IsEqualTo((byte)20);
    }

    // ---- Layer 1 (i8 induction) multi-loop coverage ------------------------------------------------
    //
    // Layer 1's residency unit is always exactly one (phi, bodyValue) pair sharing one register - there
    // is never more than one candidate per loop, so there is no per-loop register-pool arbitration and
    // no partial-admission window (contrast Layer 2, which can have 2 candidates - src/dst - sharing one
    // loop and one 2-pair pool). A dropped Layer 1 candidate's fallback (WRAM reload/gentle-add/store)
    // touches only A and B, never C/D/E/H/L - disjoint from the residency registers - so it cannot
    // corrupt a sibling the way Layer 2's HL/DE-clobbering ordinary gep path did. These tests confirm
    // that structural argument empirically for the same multi-loop shapes that broke Layer 2.

    [Test]
    public async Task TwoSequentialI8CountedLoops_DistinctCounters()
    {
        const string src = """
            public static class Program {
                public static void Main() {
                    byte total1 = 0;
                    for (byte i = 0; i < 10; i++) {
                        total1 = (byte)(total1 + 1);
                    }
                    *(byte*)0xC100 = total1;
                    byte total2 = 0;
                    for (byte j = 0; j < 20; j++) {
                        total2 = (byte)(total2 + 1);
                    }
                    *(byte*)0xC200 = total2;
                }
            }
            """;
        var gb = Load(src, "twoi8_1.cs", out int start);
        Run(gb, start);
        await Assert.That(ReadByte(gb, 0xC100)).IsEqualTo((byte)10);
        await Assert.That(ReadByte(gb, 0xC200)).IsEqualTo((byte)20);
    }

    [Test]
    public async Task TwoI8CountedLoopsInIfElseBranches()
    {
        const string src = """
            public static class Program {
                public static void Main() {
                    *(byte*)0xC000 = 0;
                    bool which = *(byte*)0xC000 != 0;
                    byte total = 0;
                    if (which) {
                        for (byte i = 0; i < 10; i++) {
                            total = (byte)(total + 1);
                        }
                    } else {
                        for (byte j = 0; j < 20; j++) {
                            total = (byte)(total + 1);
                        }
                    }
                    *(byte*)0xC300 = total;
                }
            }
            """;
        var gb = Load(src, "twoi8_ifelse1.cs", out int start);
        Run(gb, start);
        // which == false -> else arm's 20-iteration loop runs.
        await Assert.That(ReadByte(gb, 0xC300)).IsEqualTo((byte)20);
    }

    // ---- Repro: two textually-distinct stride-1 pointer-walk loops in one function ---------------

    [Test]
    public async Task TwoSequentialFillLoops_DistinctBuffers()
    {
        const string src = """
            public static unsafe class Program {
                public static void Main() {
                    byte* a = (byte*)0xC100;
                    byte countA = 10;
                    while (countA != 0) {
                        *a = 0x11;
                        a++;
                        countA--;
                    }
                    byte* b = (byte*)0xC200;
                    byte countB = 10;
                    while (countB != 0) {
                        *b = 0x22;
                        b++;
                        countB--;
                    }
                }
            }
            """;
        var gb = Load(src, "twofill1.cs", out int start);
        Run(gb, start);
        for (int i = 0; i < 10; i++)
            await Assert.That(ReadByte(gb, 0xC100 + i)).IsEqualTo((byte)0x11);
        for (int i = 0; i < 10; i++)
            await Assert.That(ReadByte(gb, 0xC200 + i)).IsEqualTo((byte)0x22);
    }

    [Test]
    public async Task TwoLoopsInIfElseBranches()
    {
        // Each branch is a two-candidate COPY loop (src load-fused + dst store-fused) - the shape that
        // actually exercises partial admission (a single-candidate fill loop per branch never hits it,
        // and would have passed even before the fix). The branch condition reads WRAM (explicitly
        // zeroed first -> false -> else arm) so the optimizer cannot constant-fold the branch away.
        // WRAM is NOT zero-initialized by this harness (it defaults to 0xFF), so the zero-write is
        // required.
        const string srcA = """
            public static unsafe class Program {
                public static void Main() {
                    *(byte*)0xC000 = 0;
                    bool which = *(byte*)0xC000 != 0;
                    if (which) {
                        byte* sa = (byte*)0xC100;
                        byte* da = (byte*)0xC110;
                        byte countA = 10;
                        while (countA != 0) {
                            *da = *sa;
                            da++;
                            sa++;
                            countA--;
                        }
                    } else {
                        byte* sb = (byte*)0xC200;
                        byte* db = (byte*)0xC210;
                        for (byte i = 0; i < 10; i++) {
                            sb[i] = (byte)(i + 1);
                        }
                        byte countB = 10;
                        while (countB != 0) {
                            *db = *sb;
                            db++;
                            sb++;
                            countB--;
                        }
                    }
                }
            }
            """;
        var gb = Load(srcA, "ifelse1.cs", out int start);
        Run(gb, start);
        // which == false -> else arm runs: sb filled with 1..10, then copied db <- sb.
        for (int i = 0; i < 10; i++)
            await Assert.That(ReadByte(gb, 0xC210 + i)).IsEqualTo((byte)(i + 1));
    }

    [Test]
    public async Task TwoSequentialCopyLoops_DistinctBuffers()
    {
        // The task's canonical Layer 2 shape (src load-fused into Hl, dst store-fused into De) repeated
        // twice with fresh, non-overlapping pointer locals for each copy.
        const string src = """
            public static unsafe class Program {
                public static void Main() {
                    byte* s1 = (byte*)0xC100;
                    byte* d1 = (byte*)0xC200;
                    for (byte i = 0; i < 10; i++) {
                        s1[i] = i;
                    }
                    byte count1 = 10;
                    while (count1 != 0) {
                        *d1 = *s1;
                        d1++;
                        s1++;
                        count1--;
                    }
                    byte* s2 = (byte*)0xC300;
                    byte* d2 = (byte*)0xC400;
                    for (byte i = 0; i < 10; i++) {
                        s2[i] = (byte)(i + 100);
                    }
                    byte count2 = 10;
                    while (count2 != 0) {
                        *d2 = *s2;
                        d2++;
                        s2++;
                        count2--;
                    }
                }
            }
            """;
        var gb = Load(src, "twocopy1.cs", out int start);
        Run(gb, start);
        for (int i = 0; i < 10; i++)
            await Assert.That(ReadByte(gb, 0xC200 + i)).IsEqualTo((byte)i);
        for (int i = 0; i < 10; i++)
            await Assert.That(ReadByte(gb, 0xC400 + i)).IsEqualTo((byte)(i + 100));
    }

    [Test]
    public async Task CopyLoopFollowedByFillLoop()
    {
        const string src = """
            public static unsafe class Program {
                public static void Main() {
                    byte* s = (byte*)0xC100;
                    byte* d = (byte*)0xC200;
                    for (byte i = 0; i < 10; i++) {
                        s[i] = i;
                    }
                    byte count = 10;
                    while (count != 0) {
                        *d = *s;
                        d++;
                        s++;
                        count--;
                    }
                    byte* f = (byte*)0xC300;
                    byte countF = 10;
                    while (countF != 0) {
                        *f = 0x33;
                        f++;
                        countF--;
                    }
                }
            }
            """;
        var gb = Load(src, "copyfill1.cs", out int start);
        Run(gb, start);
        for (int i = 0; i < 10; i++)
            await Assert.That(ReadByte(gb, 0xC200 + i)).IsEqualTo((byte)i);
        for (int i = 0; i < 10; i++)
            await Assert.That(ReadByte(gb, 0xC300 + i)).IsEqualTo((byte)0x33);
    }
}
