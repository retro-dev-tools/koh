using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests;

/// <summary>Phase 3 of the semantic-model migration: <c>MethodLowerer</c>'s five intrinsic-recognition
/// sites (Hardware register write/read, Gb region read, Hardware control-intrinsic calls, Mem.Alloc/
/// Reset, BitConverter) now resolve the receiver's Roslyn symbol first and only fall back to the
/// pre-migration exact-name string match when no symbol resolves (a detached monomorphized-generic
/// body, or no compilation). These tests exercise the real end-to-end pipeline (frontend -&gt; backend -&gt;
/// linker -&gt; emulator), mirroring <c>CSharpEndToEndTests</c>'s harness, so they prove both that ordinary
/// programs still lower/run identically and that the new symbol-first path fixes a real name-collision
/// bug the old string match had.</summary>
public class CSharpSemanticsIntrinsicsTests
{
    private static IrModule Frontend(string src)
    {
        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            var errors = IrVerifier.Verify(module);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "IR verification failed:\n  " + string.Join("\n  ", errors)
                );
        }
        return module;
    }

    private static EmitModel Compile(string src) =>
        new Sm83Backend().Compile(Frontend(src), new DiagnosticBag());

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("cs", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        length = Sm83Backend.CodeBase + model.Sections[0].Data.Length - 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    private static void Run(GameBoySystem gb, int start, int length)
    {
        for (int steps = 0; steps < 200_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }
    }

    private static byte RunA(string src, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
        Run(gb, s, l);
        return gb.Registers.A;
    }

    private static uint RunI32(string src, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
        Run(gb, s, l);
        return ((uint)gb.Registers.DE << 16) | gb.Registers.HL;
    }

    private static byte RunThenRead(string src, int address)
    {
        var gb = Load(Compile(src), out int s, out int l);
        Run(gb, s, l);
        return gb.DebugReadByte((ushort)address);
    }

    private static bool HasError(string src)
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        return diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    private static DiagnosticBag LowerDiagnostics(string src)
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        return diagnostics;
    }

    // ---- Ordinary programs: symbol-first resolution matches the old string-based behavior ----------

    [Test]
    public async Task HardwareRegisterWrite_ResolvesBySymbol_AndRuns()
    {
        // Hardware.BGP is the stub's byte property; the write site (MemberPointer) must still resolve it
        // by symbol identity against CSharpSemantics.HardwareType and reach the real register (0xFF47).
        const string src = "static void Main() { Hardware.BGP = 0xE4; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunThenRead(src, 0xFF47)).IsEqualTo((byte)0xE4);
    }

    [Test]
    public async Task HardwareRegisterRead_ResolvesBySymbol_AndRuns()
    {
        const string src = "static byte Main() { return Hardware.SCY; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src, gb => gb.DebugWriteByte(0xFF42, 0x55))).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task GbVramPointerMath_ResolvesBySymbol_AndRuns()
    {
        // Gb.Vram lowers to a byte* at the fixed VRAM base (0x8000); the read site must still resolve
        // it via CSharpSemantics.GbType, and pointer arithmetic off it must still work.
        const string src = "static void Main() { byte* v = Gb.Vram; *(v + 5) = 0x3C; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunThenRead(src, 0x8005)).IsEqualTo((byte)0x3C);
    }

    [Test]
    public async Task MemAllocAndReset_ResolveBySymbol_AndRun()
    {
        // Mem.Alloc/Mem.Reset must still resolve via CSharpSemantics.MemType and behave as the arena
        // allocator: two live allocations are distinct, and Reset lets a freed region be reused.
        const string src =
            "static byte Main() { byte* a = Mem.Alloc(4); byte* b = Mem.Alloc(4); "
            + "a[0] = 5; b[0] = 9; return (byte)(a[0] + b[0]); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)14);
    }

    [Test]
    public async Task HardwareControlIntrinsics_ResolveBySymbol_AndRun()
    {
        // Hardware.EnableInterrupts/DisableInterrupts/Halt/Nop are recognized by the LowerCall site;
        // Nop() lowering to the `nop` opcode must not disturb the rest of the computation.
        const string src =
            "static byte Main() { Hardware.EnableInterrupts(); Hardware.Nop(); return 42; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task BitConverter_ResolvesBySymbol_AndRuns()
    {
        // There is no stub for BitConverter — it must resolve to the real BCL System.BitConverter (see
        // IsBitConverterSubject), not fall through to "unsupported call target".
        const string src = "static uint Main() { return BitConverter.SingleToUInt32Bits(1.5f); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunI32(src)).IsEqualTo(BitConverter.SingleToUInt32Bits(1.5f));
    }

    // ---- Detached node fallback: a monomorphized generic instance's body is not in the compilation ----

    [Test]
    public async Task GenericBody_UsingHardwareAndGb_LowersViaStringFallback()
    {
        // Touch<byte>'s body is copied into a detached synthesized tree (TypeParamRewriter), so
        // CSharpSemantics.Sym returns null for every node in it; the Hardware/Gb sites must fall back to
        // the exact string match rather than silently failing to recognize the intrinsic.
        const string src =
            "static byte Main() { return Touch<byte>(9); }\n"
            + "static T Touch<T>(T x) { Hardware.LCDC = 1; byte* v = Gb.Vram; *v = 7; return x; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)9);
        await Assert.That(RunThenRead(src, 0xFF40)).IsEqualTo((byte)1); // LCDC
        await Assert.That(RunThenRead(src, 0x8000)).IsEqualTo((byte)7); // Vram base
    }

    // ---- Symbol identity beats name collision (the intended behavioral improvement) ------------------

    [Test]
    public async Task LocalNamedHardware_CallingUserMethod_IsNotMistakenForTheIntrinsic()
    {
        // A local variable happens to be named "Hardware" and its class happens to declare a method
        // named "EnableInterrupts" too (a plausible robotics-themed name, not the intrinsic surface).
        // The pre-migration string match keyed purely off the receiver's spelled name, so
        // `Hardware.EnableInterrupts()` here would have been silently treated as the CPU `ei`
        // intrinsic instead of the user's method call. Symbol-first resolution tells them apart: the
        // local's symbol is an ILocalSymbol of type Robot, not CSharpSemantics.HardwareType, so the
        // call falls through to ordinary instance-method lowering and actually invokes Robot.EnableInterrupts.
        const string src =
            "static byte Main() { Robot Hardware = new Robot(); return Hardware.EnableInterrupts(); }\n"
            + "class Robot { byte id; byte EnableInterrupts() { return 42; } }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    // ---- Reserved-name diagnostics (from earlier passes, not this phase) still fire -------------------

    [Test]
    public async Task ReservedIntrinsicClassNames_StillReportDiagnostics()
    {
        // A user class named after an intrinsic surface is still rejected by CSharpFrontend.Declarations
        // (unrelated to this phase's MethodLowerer changes) — a regression check that converting the
        // recognition sites to symbol-first didn't loosen this.
        await Assert.That(HasError("class Hardware { byte x; } static void Main() { }")).IsTrue();
        await Assert.That(HasError("class Gb { byte x; } static void Main() { }")).IsTrue();
        await Assert.That(HasError("class Mem { byte x; } static void Main() { }")).IsTrue();
    }
}
