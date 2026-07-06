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

namespace Koh.Compiler.Tests.Frontends;

/// <summary>Compiles real C# source through the frontend, backend, linker, and emulator.</summary>
public class CSharpEndToEndTests
{
    private static IrModule Frontend(string src) =>
        new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), new DiagnosticBag());

    private static EmitModel Compile(string src) =>
        new Sm83Backend().Compile(Frontend(src), new DiagnosticBag());

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("cs", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = Sm83Backend.CodeBase;
        length = model.Sections[0].Data.Length;
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

    private static ushort RunHL(string src, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
        Run(gb, s, l);
        return gb.Registers.HL;
    }

    private static void W8(GameBoySystem gb, int offset, int value) =>
        gb.DebugWriteByte((ushort)(Sm83Backend.WramBase + offset), (byte)value);

    private static void W16(GameBoySystem gb, int offset, int value)
    {
        gb.DebugWriteByte((ushort)(Sm83Backend.WramBase + offset), (byte)(value & 0xFF));
        gb.DebugWriteByte((ushort)(Sm83Backend.WramBase + offset + 1), (byte)((value >> 8) & 0xFF));
    }

    [Test]
    public async Task Frontend_ProducesVerifiableIr()
    {
        var module = Frontend("static byte Add(byte a, byte b) { return a + b; }");
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Add_Params()
    {
        const string src = "static byte Add(byte a, byte b) { return a + b; }";
        await Assert.That(RunA(src, gb => { W8(gb, 0, 40); W8(gb, 1, 2); })).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Sum_WhileLoop()
    {
        const string src = @"
static ushort Sum(ushort n) {
    ushort acc = 0;
    ushort i = 0;
    while (i < n) { acc = acc + i; i = i + 1; }
    return acc;
}";
        await Assert.That(RunHL(src, gb => W16(gb, 0, 10))).IsEqualTo((ushort)45); // 0+1+...+9
    }

    [Test]
    public async Task Factorial_MulAndCompoundAssign()
    {
        const string src = @"
static byte Fact(byte n) {
    byte r = 1;
    while (n > 1) { r *= n; n -= 1; }
    return r;
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 5))).IsEqualTo((byte)120);
    }

    [Test]
    public async Task Gcd_ModuloLoop()
    {
        const string src = @"
static ushort Gcd(ushort a, ushort b) {
    while (b != 0) { ushort t = b; b = a % b; a = t; }
    return a;
}";
        await Assert.That(RunHL(src, gb => { W16(gb, 0, 48); W16(gb, 2, 36); })).IsEqualTo((ushort)12);
    }

    [Test]
    public async Task Max_IfElse()
    {
        const string src = "static byte Max(byte a, byte b) { if (a > b) return a; else return b; }";
        await Assert.That(RunA(src, gb => { W8(gb, 0, 3); W8(gb, 1, 7); })).IsEqualTo((byte)7);
    }

    [Test]
    public async Task MethodCall_AcrossFunctions()
    {
        const string src = @"
static byte Main() { return Triple(14); }
static byte Triple(byte x) { return x + x + x; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task SignedNegate_Sbyte()
    {
        const string src = "static sbyte Neg(sbyte x) { return -x; }";
        await Assert.That(RunA(src, gb => W8(gb, 0, 5))).IsEqualTo((byte)0xFB); // -5
    }

    [Test]
    public async Task SignedDivide_Short()
    {
        const string src = "static short Div(short a, short b) { return a / b; }";
        // -1000 / 7 = -142 = 0xFF72
        await Assert.That(RunHL(src, gb => { W16(gb, 0, -1000 & 0xFFFF); W16(gb, 2, 7); })).IsEqualTo((ushort)0xFF72);
    }

    [Test]
    public async Task UnsupportedType_Throws()
    {
        bool threw = false;
        try { Frontend("static int Bad() { return 0; }"); }
        catch (CSharpNotSupportedException) { threw = true; }
        await Assert.That(threw).IsTrue();
    }
}
