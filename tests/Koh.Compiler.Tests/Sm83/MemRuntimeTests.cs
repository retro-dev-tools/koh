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
/// End-to-end coverage for <c>Mem.Copy</c>/<c>Mem.Fill</c> (Package A2 of the emulator-accuracy-
/// stabilization plan): both route to <c>MemRuntime.cs</c>'s <c>__mem_copy</c>/<c>__mem_fill</c> — ordinary
/// compiled Koh C# subset source appended to the compilation the same way the softfloat runtime is (see
/// <c>CSharpFrontend.PrepareRoot</c>/<c>UsesMemRuntime</c>), not hand-written assembly. Mirrors the harness
/// pattern in <c>Cube3dTests</c>/<c>CSharpEndToEndTests</c>: real frontend -&gt; IR -&gt; SM83 backend -&gt;
/// linker -&gt; <see cref="GameBoySystem"/>, with the program itself seeding a known pattern into WRAM,
/// performing the copy/fill, and the test reading the result back out of WRAM after running to completion.
///
/// Every test buffer comes from <c>Mem.Alloc</c>, not a hardcoded literal WRAM pointer: literal addresses
/// near <c>Sm83Backend.WramBase</c> (0xC000) collide with the backend's own static per-function frame
/// allocation (locals/params are NESFab-style statically assigned WRAM, not stack-based) — a scratch
/// buffer planted there is silently the storage for some function's own locals. <c>Mem.Alloc</c> instead
/// bumps down from <c>CSharpFrontend.HeapTop</c> (0xDE00), a region reserved for the heap and far from the
/// frame area, so allocations are safe by construction. Each allocation's resulting address is
/// deterministic (<c>HeapTop</c> minus the running total of prior allocation sizes, in call order) —
/// mirrored here by <see cref="AllocAddr"/> — so the test can read the exact bytes back out afterward.
/// </summary>
public class MemRuntimeTests
{
    private static IrModule Frontend(string src)
    {
        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(SourceText.From(src, "mem.cs"), diagnostics);
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

    private static GameBoySystem Load(string src, out int start, out int length)
    {
        var module = Frontend(src);
        IrOptimizer.Optimize(module); // CompilerDriver always optimizes; mirror that default path.
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link([new LinkerInput("mem", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        length = Sm83Backend.CodeBase + model.Sections[0].Data.Length - 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    /// <summary>Run to completion (falling off the end of Main back past the loaded code), returning the
    /// number of T-cycles ("dots") the run took.</summary>
    private static ulong Run(GameBoySystem gb, int start, int length)
    {
        ulong startT = gb.Cpu.TotalTCycles;
        for (int steps = 0; steps < 200_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
        return gb.Cpu.TotalTCycles - startT;
    }

    private static byte[] ReadRange(GameBoySystem gb, int address, int count)
    {
        var result = new byte[count];
        for (int i = 0; i < count; i++)
            result[i] = gb.DebugReadByte((ushort)(address + i));
        return result;
    }

    /// <summary>Mirrors the compiler's own bump-down arena allocator: the address a <c>Mem.Alloc(size)</c>
    /// call in the compiled program receives, given <paramref name="heap"/> tracks the current heap
    /// pointer across successive calls in program order (starting at <see cref="CSharpFrontend.HeapTop"/>).</summary>
    private static int AllocAddr(ref int heap, int size) => heap -= size;

    [Test]
    public async Task Frontend_ProducesVerifiableIr()
    {
        var module = Frontend(
            "static void Main() { byte* a = Mem.Alloc(4); byte* b = Mem.Alloc(4); "
                + "Mem.Copy(a, b, 4); Mem.Fill(a, 1, 4); }"
        );
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Copy_CountZero_IsNoOp()
    {
        const string src =
            "static void Main() { byte* dst = Mem.Alloc(8); byte* src = Mem.Alloc(8); "
            + "ushort i = 0; while (i != 8) { *(dst + i) = 0xAA; *(src + i) = (byte)(i + 1); i++; } "
            + "Mem.Copy(dst, src, 0); }";
        var gb = Load(src, out var s, out var l);
        Run(gb, s, l);
        int heap = CSharpFrontend.HeapTop;
        int dst = AllocAddr(ref heap, 8);
        AllocAddr(ref heap, 8); // src (unused address, order-only)
        await Assert.That(ReadRange(gb, dst, 8)).IsEquivalentTo(Enumerable.Repeat((byte)0xAA, 8));
    }

    [Test]
    public async Task Copy_SingleByte()
    {
        const string src =
            "static void Main() { byte* dst = Mem.Alloc(8); byte* src = Mem.Alloc(8); "
            + "ushort i = 0; while (i != 8) { *(dst + i) = 0xAA; *(src + i) = (byte)(i + 1); i++; } "
            + "Mem.Copy(dst, src, 1); }";
        var gb = Load(src, out var s, out var l);
        Run(gb, s, l);
        int heap = CSharpFrontend.HeapTop;
        int dst = AllocAddr(ref heap, 8);
        AllocAddr(ref heap, 8);
        // Only dst[0] moves to src[0] (=1); the rest of the 8-byte buffer stays the seeded 0xAA.
        await Assert
            .That(ReadRange(gb, dst, 8))
            .IsEquivalentTo(new byte[] { 1, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA });
    }

    [Test]
    public async Task Copy_OddSize()
    {
        const string src =
            "static void Main() { byte* dst = Mem.Alloc(8); byte* src = Mem.Alloc(8); "
            + "ushort i = 0; while (i != 8) { *(dst + i) = 0xAA; *(src + i) = (byte)(i + 1); i++; } "
            + "Mem.Copy(dst, src, 7); }";
        var gb = Load(src, out var s, out var l);
        Run(gb, s, l);
        int heap = CSharpFrontend.HeapTop;
        int dst = AllocAddr(ref heap, 8);
        AllocAddr(ref heap, 8);
        // The first 7 bytes come from src (1..7); the 8th byte is untouched (still 0xAA).
        await Assert
            .That(ReadRange(gb, dst, 8))
            .IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 0xAA });
    }

    [Test]
    public async Task Copy_CrossesA256ByteBoundary()
    {
        // dst and src are adjacent 300-byte arena allocations (non-overlapping). count=300 forces the
        // copy loop's pointer increments and the ushort counter past a low-byte 0xFF -> 0x00 rollover,
        // exercising the high-byte carry the backend's pointer-increment/`count--` codegen must get right.
        const string src =
            "static void Main() { byte* dst = Mem.Alloc(300); byte* src = Mem.Alloc(300); "
            + "ushort i = 0; while (i != 300) { *(src + i) = (byte)i; i++; } "
            + "Mem.Copy(dst, src, 300); }";
        var gb = Load(src, out var s, out var l);
        Run(gb, s, l);
        int heap = CSharpFrontend.HeapTop;
        int dst = AllocAddr(ref heap, 300);
        AllocAddr(ref heap, 300);
        var result = ReadRange(gb, dst, 300);
        for (int i = 0; i < 300; i++)
            await Assert.That(result[i]).IsEqualTo((byte)i);
    }

    [Test]
    public async Task Fill_VariousCounts()
    {
        const string src =
            "static void Main() { byte* dst = Mem.Alloc(16); "
            + "ushort i = 0; while (i != 16) { *(dst + i) = 0x55; i++; } "
            + "Mem.Fill(dst, 0xAA, 0); "
            + "Mem.Fill(dst + 1, 0xBB, 1); "
            + "Mem.Fill(dst + 4, 0xCC, 7); }";
        var gb = Load(src, out var s, out var l);
        Run(gb, s, l);
        int heap = CSharpFrontend.HeapTop;
        int dst = AllocAddr(ref heap, 16);
        var result = ReadRange(gb, dst, 16);
        // Fill(dst, _, 0): no-op, dst[0] stays 0x55.
        await Assert.That(result[0]).IsEqualTo((byte)0x55);
        // Fill(dst+1, 0xBB, 1): only dst[1] becomes 0xBB.
        await Assert.That(result[1]).IsEqualTo((byte)0xBB);
        await Assert.That(result[2]).IsEqualTo((byte)0x55);
        await Assert.That(result[3]).IsEqualTo((byte)0x55);
        // Fill(dst+4, 0xCC, 7): dst[4..10] become 0xCC, dst[11..15] stay 0x55.
        for (int i = 4; i < 11; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0xCC);
        for (int i = 11; i < 16; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task Fill_CrossesA256ByteBoundary()
    {
        const string src =
            "static void Main() { byte* dst = Mem.Alloc(300); Mem.Fill(dst, 0x7E, 300); }";
        var gb = Load(src, out var s, out var l);
        Run(gb, s, l);
        int heap = CSharpFrontend.HeapTop;
        int dst = AllocAddr(ref heap, 300);
        var result = ReadRange(gb, dst, 300);
        for (int i = 0; i < 300; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0x7E);
    }

    [Test]
    public async Task Copy_OverlapDestinationBeforeSource_IsDefined()
    {
        // destination < source: buf[0..9] seeded 0..9, then Mem.Copy(buf, buf+3, 7) — a forward,
        // byte-by-byte walk always reads src[k]=buf[k+3] before any earlier write could reach index
        // k+3 (the write index k never catches up to k+3 while shift=3 stays constant), so this
        // overlap shape is defined per the documented semantics (destination < source).
        const string src =
            "static void Main() { byte* buf = Mem.Alloc(10); "
            + "ushort i = 0; while (i != 10) { *(buf + i) = (byte)i; i++; } "
            + "Mem.Copy(buf, buf + 3, 7); }";
        var gb = Load(src, out var s, out var l);
        Run(gb, s, l);
        int heap = CSharpFrontend.HeapTop;
        int buf = AllocAddr(ref heap, 10);
        // buf[0..6] = original buf[3..9] = 3..9; buf[7..9] untouched = original 7..9.
        await Assert
            .That(ReadRange(gb, buf, 10))
            .IsEquivalentTo(new byte[] { 3, 4, 5, 6, 7, 8, 9, 7, 8, 9 });
    }

    [Test]
    public async Task Copy_CostPerByte_IsWithinLooseCeiling()
    {
        const int count = 64;
        string src =
            "static void Main() { byte* dst = Mem.Alloc(64); byte* src = Mem.Alloc(64); "
            + $"Mem.Copy(dst, src, {count}); }}";
        var gb = Load(src, out var s, out var l);
        var dots = Run(gb, s, l);
        double dotsPerByte = (double)dots / count;
        Console.WriteLine(
            $"Mem.Copy measured cost: {dots} dots for {count} bytes = {dotsPerByte:F1} dots/byte "
                + "(includes fixed Mem.Alloc/call overhead, not pure per-iteration loop cost)"
        );
        // Loose ceiling: today's compiled byte-copy loop (call + compare/branch/load/store/pointer-
        // increment/decrement per byte, no vectorization) measures around 900 dots/byte on SM83 — this
        // only guards against a severe regression (e.g. losing an optimizer pass), not a performance
        // target, hence the generous headroom above the measured baseline. A later wave (the parallel
        // loop-optimizer package) tightens this once it lands; keep this assertion loose until then.
        await Assert.That(dotsPerByte).IsLessThanOrEqualTo(1000.0);
    }
}
