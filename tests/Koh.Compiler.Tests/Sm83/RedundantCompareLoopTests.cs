using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Sm83;

/// <summary>
/// Pins <see cref="RedundantCompareEliminationPass"/>: the frontend lowers an inequality with no direct
/// IL opcode (a pointer/relational <c>!=</c>) as <c>ceq ; ldc.i4.0 ; ceq</c> and then wraps that
/// already-Boolean value in another <c>icmp</c> for the <c>brtrue</c>/<c>brfalse</c> — so a single
/// <c>while (p != end)</c> loop guard lowers to a THREE-deep <c>icmp</c> chain where one suffices. That
/// bloat is per-iteration latency on the accumulator machine, and it is what made
/// <c>Koh.GameBoy.Graphics.MapWriter.FlushRun</c>'s vblank-bounded byte-copy drip fragile: enough of it
/// between loop entry and the first <c>LY</c>-window read that any small upstream layout shift pushed
/// that read past the end of vblank, so the drip copied zero bytes forever (the "layout-perturbed
/// stride-1 loop" regression — see <c>CilGame2048Tests.Sample_RendersScoreLabelAndNumberToTheBackgroundMap</c>).
/// These tests assert the chain is gone (codegen shape) AND that the loop it guards still runs correctly
/// on the emulator (behavior), on the real Roslyn -&gt; <see cref="CilFrontend"/> -&gt;
/// <see cref="IrOptimizer"/> -&gt; <see cref="Sm83Backend"/> pipeline.
/// </summary>
public class RedundantCompareLoopTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            foreach (var p in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                try
                {
                    builder.Add(MetadataReference.CreateFromFile(p));
                }
                catch (IOException) { }
                catch (BadImageFormatException) { }
        builder.Add(
            MetadataReference.CreateFromFile(typeof(Koh.GameBoy.Hardware).Assembly.Location)
        );
        return builder.ToImmutable();
    });

    private static readonly string ScratchDir = Path.Combine(
        Path.GetTempPath(),
        "koh-redundant-compare-loop-tests"
    );

    private const string GlobalUsings = "global using Koh.GameBoy;\n";

    private static string CompileToAssembly(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(GlobalUsings + source);
        var compilation = CSharpCompilation.Create(
            "RedundantCmpAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"rc_{Guid.NewGuid():N}.dll");
        var emit = compilation.Emit(path);
        if (!emit.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emit.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    private static IrModule OptimizedModule(string source)
    {
        var diagnostics = new DiagnosticBag();
        var input = CompilerInput.FromAssembly(
            CompileToAssembly(source),
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        if (diagnostics.Any(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error))
            throw new InvalidOperationException(
                string.Join("; ", diagnostics.Select(d => d.Message))
            );
        IrOptimizer.Optimize(module);
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );
        return module;
    }

    /// <summary>Every comparison whose own operand is itself a comparison — the redundant "compare a
    /// Boolean against a constant" negation chains this pass exists to remove. Zero after the fix.</summary>
    private static int CompareOfCompareCount(IrFunction fn) =>
        fn
            .Blocks.SelectMany(b => b.Instructions)
            .OfType<CompareInstruction>()
            .Count(c => c.Left is CompareInstruction || c.Right is CompareInstruction);

    // ---- Codegen shape: the redundant chain is gone ------------------------------------------------

    // The real, unmodified Koh.GameBoy.Graphics.MapWriter.FlushRun — the vblank drip whose fragility this
    // whole fix is about. The gb-gfx-demo sample paints through Bg/Win, so the CIL frontend lowers
    // FlushRun from Koh.GameBoy.dll on demand exactly as a game build does (mirrors
    // FlushRunLoopResidencyTests' own harness). Its `while (dst != end)` header and `(byte)(LY-144) > 8`
    // guard each lowered — before this fix — to a redundant `icmp` chain (`eq ; eq ; ne` and `ugt ; eq`);
    // this asserts the pass collapses both on the genuine production code, not just a synthetic shape.
    private static readonly string DemoSource = File.ReadAllText(
        Path.Combine(Root(), "samples", "gb-gfx-demo", "Game.cs")
    );

    [Test]
    public async Task RealFlushRun_LoopGuards_HaveNoRedundantCompareChain()
    {
        var module = OptimizedModule(DemoSource);
        var flushRun = module.Functions.Single(f => f.Name.EndsWith("MapWriter.FlushRun"));

        // The whole point of the pass: not one comparison in the optimized function tests another
        // comparison's Boolean result against a constant. Before the fix FlushRun carried three such
        // chained comparisons (two in the `while (dst != end)` header, one in the `if ((byte)(LY-144) > 8)`).
        await Assert.That(CompareOfCompareCount(flushRun)).IsEqualTo(0);

        // The guards survive as single comparisons — the fold collapses each chain, it does not delete the
        // branch. FlushRun's header is a pointer `!=`, lowered to a single `Ne`.
        var compares = flushRun
            .Blocks.SelectMany(b => b.Instructions)
            .OfType<CompareInstruction>()
            .ToList();
        await Assert.That(compares.Any(c => c.Op == IrCompareOp.Ne)).IsTrue();

        // IR stays valid for the backend.
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    // ---- Behavior: the guarded stride-1 copy loop still runs correctly on the emulator ---------------

    private static GameBoySystem Boot(IrModule module, out int entry, out LinkResult link)
    {
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        link = new LinkerType().Link([new LinkerInput("rc", model)]);
        if (!link.Success)
            throw new InvalidOperationException("link failed");
        var rom = link.RomData!;
        entry = 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = 0x100;
        return gb;
    }

    [Test]
    public async Task GuardedStride1CopyLoop_CopiesEveryByte_WhenGuardOpen()
    {
        // Main fills a source buffer, runs the guarded drip with the guard held OPEN (gate value in the
        // "inside vblank" band so `(byte)(gate-144) > 8` is false every iteration), and stores both the
        // returned count and the copied bytes to fixed WRAM the test reads back. This exercises the exact
        // fused post-increment stride-1 loop the compare-chain bloat used to strangle.
        const string src = """
            public static unsafe class Program
            {
                static byte* Dst => (byte*)0xC200;
                static byte* Src => (byte*)0xC100;

                public static ushort Drip(byte* dst, byte* src, ushort n, byte gate)
                {
                    byte* start = dst;
                    byte* end = dst + n;
                    while (dst != end)
                    {
                        if ((byte)(gate - 144) > 8)
                            return (ushort)(dst - start);
                        *dst = *src;
                        dst++;
                        src++;
                    }
                    return n;
                }

                public static void Main()
                {
                    for (byte i = 0; i < 8; i++)
                        *(Src + i) = (byte)(0x40 + i);
                    // gate = 148 -> (byte)(148-144) = 4, not > 8 -> window open the whole loop.
                    ushort copied = Drip(Dst, Src, 8, 148);
                    *(byte*)0xC000 = (byte)copied;
                }
            }
            """;

        var gb = Boot(OptimizedModule(src), out int start, out _);
        for (int i = 0; i < 2_000_000; i++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }

        await Assert.That(gb.DebugReadByte(0xC000)).IsEqualTo((byte)8); // returned the full count
        for (int i = 0; i < 8; i++)
            await Assert.That(gb.DebugReadByte((ushort)(0xC200 + i))).IsEqualTo((byte)(0x40 + i));
    }

    [Test]
    public async Task GuardedStride1CopyLoop_CopiesNothing_WhenGuardClosed()
    {
        // Same loop, guard held CLOSED (gate outside the [144,152] band): the first per-iteration guard
        // read must bail before any store, returning 0 — the drip's whole "never write outside the
        // window" contract, and the property the regression silently broke into "always 0, forever."
        const string src = """
            public static unsafe class Program
            {
                static byte* Dst => (byte*)0xC200;
                static byte* Src => (byte*)0xC100;

                public static ushort Drip(byte* dst, byte* src, ushort n, byte gate)
                {
                    byte* start = dst;
                    byte* end = dst + n;
                    while (dst != end)
                    {
                        if ((byte)(gate - 144) > 8)
                            return (ushort)(dst - start);
                        *dst = *src;
                        dst++;
                        src++;
                    }
                    return n;
                }

                public static void Main()
                {
                    *(Dst) = 0xEE; // sentinel: must stay untouched
                    for (byte i = 0; i < 8; i++)
                        *(Src + i) = (byte)(0x40 + i);
                    ushort copied = Drip(Dst, Src, 8, 200); // 200-144 = 56 > 8 -> closed
                    *(byte*)0xC000 = (byte)copied;
                }
            }
            """;

        var gb = Boot(OptimizedModule(src), out int start, out _);
        for (int i = 0; i < 2_000_000; i++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }

        await Assert.That(gb.DebugReadByte(0xC000)).IsEqualTo((byte)0); // copied nothing
        await Assert.That(gb.DebugReadByte(0xC200)).IsEqualTo((byte)0xEE); // sentinel untouched
    }

    // ---- Two stride-1 loops in one function both compile and run correctly ------------------------

    [Test]
    public async Task TwoSequentialStride1CopyLoops_BothCopyCorrectly()
    {
        // Two independent `while (dst != end)` stride-1 copy loops in one function — the shape MapWriter's
        // FlushRun doc comment warns against ("the ONLY pointer loop in this file … can't collide with a
        // sibling stride-1 loop under the backend register allocator"). The backend's Layer-2 pointer
        // residency admits register pairs per-loop atomically, so a second loop that can't get a pair
        // falls back to the ordinary (correct) address path rather than corrupting the first — and with
        // the compare chains gone, both guards are tight. This proves both loops produce correct bytes.
        const string src = """
            public static unsafe class Program
            {
                public static void Copy(byte* dst, byte* src, ushort n)
                {
                    byte* end = dst + n;
                    while (dst != end) { *dst = *src; dst++; src++; }
                }

                public static void Main()
                {
                    for (byte i = 0; i < 6; i++) *(byte*)(0xC100 + i) = (byte)(0x10 + i);
                    for (byte i = 0; i < 6; i++) *(byte*)(0xC140 + i) = (byte)(0x80 + i);
                    Copy((byte*)0xC200, (byte*)0xC100, 6); // loop A
                    Copy((byte*)0xC220, (byte*)0xC140, 6); // loop B
                }
            }
            """;

        var gb = Boot(OptimizedModule(src), out int start, out _);
        for (int i = 0; i < 2_000_000; i++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }

        for (int i = 0; i < 6; i++)
        {
            await Assert.That(gb.DebugReadByte((ushort)(0xC200 + i))).IsEqualTo((byte)(0x10 + i));
            await Assert.That(gb.DebugReadByte((ushort)(0xC220 + i))).IsEqualTo((byte)(0x80 + i));
        }
    }
}
