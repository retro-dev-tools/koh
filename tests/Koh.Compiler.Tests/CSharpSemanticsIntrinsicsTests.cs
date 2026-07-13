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

/// <summary><c>MethodLowerer</c>'s five intrinsic-recognition sites (Hardware register write/read, Gb
/// region read, Hardware control-intrinsic calls, Mem.Alloc/Reset, BitConverter) recognize the receiver
/// purely by its resolved Roslyn symbol's identity — since Stage-2 P5 there is no string match at all,
/// in ordinary bodies or in monomorphized generic instance bodies (which bind in the constructed
/// instances tree). These tests exercise the real end-to-end pipeline (frontend -&gt; backend -&gt;
/// linker -&gt; emulator), mirroring <c>CSharpEndToEndTests</c>'s harness, so they prove both that
/// ordinary programs lower/run identically and that symbol identity fixes the name-collision bugs the
/// old string match had — including inside a generic body, where the deleted string fallback used to
/// hijack a user value named after an intrinsic surface.</summary>
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

    private static byte RunThenRead(string src, int address, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
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
        // LCD off before running: this checks the pointer math resolves and the store lands at the right
        // address, not whether the write survives PPU mode-3 timing (boot leaves the LCD on, and this
        // program never waits for a safe window before writing VRAM — real hardware, and this emulator
        // faithfully, blocks a VRAM write made during mode 3, so whether it lands is otherwise a
        // coincidence of exactly how many cycles the compiled code takes before the store).
        await Assert
            .That(RunThenRead(src, 0x8005, gb => gb.DebugWriteByte(0xFF40, 0x00)))
            .IsEqualTo((byte)0x3C);
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

    // ---- Generic instance bodies: intrinsics resolve by symbol in the instances tree ------------------

    [Test]
    public async Task GenericBody_UsingHardwareAndGb_LowersViaSymbol()
    {
        // Touch<byte>'s body lives in the constructed instances tree (Stage-2 P2), so CSharpSemantics.Sym
        // resolves every node in it; the Hardware/Gb receivers are recognized by symbol identity against
        // the intrinsic stub types, exactly as in an ordinary body. Since Stage-2 P5 that is the only
        // recognition path — the exact-string match that used to cover the pre-P2 detached body is gone.
        const string src =
            "static byte Main() { return Touch<byte>(9); }\n"
            + "static T Touch<T>(T x) { Hardware.LCDC = 1; byte* v = Gb.Vram; *v = 7; return x; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)9);
        await Assert.That(RunThenRead(src, 0xFF40)).IsEqualTo((byte)1); // LCDC
        await Assert.That(RunThenRead(src, 0x8000)).IsEqualTo((byte)7); // Vram base
    }

    [Test]
    public async Task GenericBody_ParameterNamedHardware_IsNotMistakenForTheIntrinsic()
    {
        // The generic-body counterpart of LocalNamedHardware_CallingUserMethod_IsNotMistakenForTheIntrinsic
        // below, and the behavioral improvement the Stage-2 P5 deletion unlocks: inside Use<byte>'s
        // monomorphized body, `Hardware` is a parameter of user class Robot, and the pre-P5 string
        // fallback — which keyed intrinsic recognition purely off the receiver's spelled name whenever no
        // symbol resolved (as in the pre-P2 detached instance bodies) — would have hijacked
        // `Hardware.EnableInterrupts()` into the CPU `ei` intrinsic. Symbol-only recognition sees the
        // parameter symbol (a Robot, not the Hardware stub type) and lowers the user's own instance call.
        const string src =
            "static byte Main() { Robot r = new Robot(); return Use<byte>(r, 5); }\n"
            + "static byte Use<T>(Robot Hardware, T x) { return Hardware.EnableInterrupts(); }\n"
            + "class Robot { byte id; byte EnableInterrupts() { return 42; } }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
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

    // ---- InterruptVectorOf: symbol identity beats a spelled-but-unrelated [Interrupt(...)] ------------

    [Test]
    public async Task NonAttributeClassNamedInterrupt_StillResolvesToTheRealIntrinsic()
    {
        // Pins the finding's literal repro: a plain (non-Attribute-derived) user class named `Interrupt`.
        // "Interrupt" is not in CollectClasses' reserved-intrinsic-name list, so it's accepted. This does
        // NOT create a real collision: a type that isn't an Attribute subclass is never a legal candidate
        // for `[Interrupt(...)]` at all (C#'s attribute-name binding only considers Attribute-derived
        // types), so Roslyn resolves the application unambiguously to the stub's own InterruptAttribute
        // regardless — exactly like the pre-fix spelled-name match did for this specific case. The vector
        // is correctly wired here both before and after this fix; the fix's effect only shows up when a
        // second, genuinely valid attribute type is also in scope (see the next two tests).
        const string src =
            "class Interrupt { byte id; }\n"
            + "[Interrupt(\"VBlank\")]\n"
            + "static void OnVBlank() { }\n"
            + "static void Main() { Hardware.EnableInterrupts(); }";
        await Assert.That(HasError(src)).IsFalse();

        var fn = Frontend(src).Functions.Single(f => f.Name == "OnVBlank");
        await Assert.That(fn.InterruptVector).IsEqualTo(0x40);
    }

    [Test]
    public async Task AttributeSpelledInterrupt_ButNotTheIntrinsic_IsNotTreatedAsHandler()
    {
        // The genuine collision: a user type that IS a legal, distinct Attribute subclass, named exactly
        // `Interrupt` (matching the spelled attribute name, not just the "Attribute"-suffixed stub name).
        // With two legal attribute-class candidates in scope (this user type and the stub's own
        // InterruptAttribute), Roslyn's own attribute-name binding reports this as genuinely ambiguous
        // (CS1614) and GetSymbolInfo(attr).Symbol comes back null — verified via CandidateReason.Ambiguous
        // with both types listed as CandidateSymbols. Before this fix, InterruptVectorOf ignored all of
        // that and matched the attribute purely by its spelled name, so `[Interrupt("VBlank")]` here would
        // have been silently wired to the real VBlank vector (0x40) despite never cleanly resolving to the
        // intrinsic. IsInterruptAttribute uses plain (non-candidate) Sym, so an ambiguous resolution is
        // correctly treated as "not the intrinsic" — OnVBlank must lower as an ordinary function with no
        // interrupt vector, and no diagnostic fires.
        const string src =
            "class Interrupt : System.Attribute { byte id; public Interrupt(string kind) { } }\n"
            + "[Interrupt(\"VBlank\")]\n"
            + "static void OnVBlank() { }\n"
            + "static void Main() { }";
        var diagnostics = LowerDiagnostics(src);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse();

        var fn = Frontend(src).Functions.Single(f => f.Name == "OnVBlank");
        await Assert.That(fn.InterruptVector).IsNull();
    }

    [Test]
    public async Task AttributeSpelledInterrupt_WithUnrecognizedKind_ProducesNoSpuriousDiagnostic()
    {
        // Same genuine collision as above (a real, distinct Attribute subclass named `Interrupt`, making
        // the application ambiguous and therefore symbol-less), but with an argument ("Bogus") that isn't
        // even a recognized interrupt kind. Before this fix, the spelled-name match still entered the
        // interrupt-kind branch for this unrelated attribute and reported a spurious "unknown interrupt
        // kind" error. Since IsInterruptAttribute now requires the attribute to resolve cleanly to the
        // real Koh intrinsic first, that branch is never entered here at all.
        const string src =
            "class Interrupt : System.Attribute { byte id; public Interrupt(string kind) { } }\n"
            + "[Interrupt(\"Bogus\")]\n"
            + "static void OnBogus() { }\n"
            + "static void Main() { }";
        await Assert.That(HasError(src)).IsFalse();

        var fn = Frontend(src).Functions.Single(f => f.Name == "OnBogus");
        await Assert.That(fn.InterruptVector).IsNull();
    }

    [Test]
    public async Task RealInterruptAttribute_StillResolvesToVBlankVector()
    {
        // The positive case: the genuine Koh `[Interrupt("VBlank")]` (resolving to
        // CSharpSemantics.InterruptAttributeType, the IntrinsicsStub-declared InterruptAttribute) must
        // still be recognized and wired to the VBlank vector (0x40) exactly as before this fix.
        const string src =
            "[Interrupt(\"VBlank\")]\n"
            + "static void OnVBlank() { }\n"
            + "static void Main() { Hardware.EnableInterrupts(); }";
        await Assert.That(HasError(src)).IsFalse();

        var fn = Frontend(src).Functions.Single(f => f.Name == "OnVBlank");
        await Assert.That(fn.InterruptVector).IsEqualTo(0x40);
    }
}
