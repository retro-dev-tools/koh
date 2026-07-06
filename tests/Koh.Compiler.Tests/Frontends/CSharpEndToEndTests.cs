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
    public async Task For_Loop()
    {
        // sum 1..n with a for loop; n=5 -> 15
        const string src = @"
static byte SumTo(byte n) {
    byte acc = 0;
    for (byte i = 1; i <= n; i = i + 1) { acc = acc + i; }
    return acc;
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 5))).IsEqualTo((byte)15);
    }

    [Test]
    public async Task Break_And_Continue()
    {
        // sum even numbers below n, stop at 100; skips odds via continue, break at >=100
        const string src = @"
static byte F(byte n) {
    byte acc = 0;
    byte i = 0;
    while (i < n) {
        i = i + 1;
        if (i > 6) break;
        if (i == 3) continue;
        acc = acc + i;
    }
    return acc;
}";
        // i=1..: add 1,2,(skip 3),4,5,6, then i=7>6 break => 1+2+4+5+6 = 18
        await Assert.That(RunA(src, gb => W8(gb, 0, 20))).IsEqualTo((byte)18);
    }

    [Test]
    public async Task DoWhile_RunsBodyFirst()
    {
        const string src = @"
static byte Count(byte n) {
    byte c = 0;
    do { c = c + 1; n = n - 1; } while (n > 0);
    return c;
}";
        // n=0: body runs once (c=1), n becomes 255, 255>0 true... careful: n=0 -> n-1 wraps to 255
        // use n=4: c increments to 4
        await Assert.That(RunA(src, gb => W8(gb, 0, 4))).IsEqualTo((byte)4);
    }

    [Test]
    public async Task Switch_WithBreakAndDefault()
    {
        const string src = @"
static byte Pick(byte x) {
    byte r = 0;
    switch (x) {
        case 1: r = 11; break;
        case 2: r = 22; break;
        default: r = 99; break;
    }
    return r;
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 2))).IsEqualTo((byte)22);
        await Assert.That(RunA(src, gb => W8(gb, 0, 9))).IsEqualTo((byte)99);
    }

    [Test]
    public async Task Switch_WithReturns()
    {
        const string src = @"
static byte Classify(byte x) {
    switch (x) {
        case 1: return 10;
        case 2: return 20;
        default: return 99;
    }
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 1))).IsEqualTo((byte)10);
    }

    [Test]
    public async Task ForLoop_WithIncrement()
    {
        const string src = @"
static byte SumTo(byte n) {
    byte acc = 0;
    for (byte i = 0; i < n; i++) acc += i;
    return acc;
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 5))).IsEqualTo((byte)10); // 0+1+2+3+4
    }

    [Test]
    public async Task PostfixIncrement_ReturnsOldValue()
    {
        // x++ returns 5 then x becomes 6; 5 + 6 = 11
        const string src = "static byte F(byte x) { return x++ + x; }";
        await Assert.That(RunA(src, gb => W8(gb, 0, 5))).IsEqualTo((byte)11);
    }

    [Test]
    public async Task LogicalAnd_Or()
    {
        const string andSrc = "static byte F(byte a, byte b) { if (a > 0 && b > 0) return 1; return 0; }";
        await Assert.That(RunA(andSrc, gb => { W8(gb, 0, 5); W8(gb, 1, 3); })).IsEqualTo((byte)1);
        await Assert.That(RunA(andSrc, gb => { W8(gb, 0, 0); W8(gb, 1, 3); })).IsEqualTo((byte)0);

        const string orSrc = "static byte F(byte a) { if (a == 0 || a == 5) return 1; return 0; }";
        await Assert.That(RunA(orSrc, gb => W8(gb, 0, 5))).IsEqualTo((byte)1);
        await Assert.That(RunA(orSrc, gb => W8(gb, 0, 3))).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Ternary()
    {
        const string src = "static byte Max(byte a, byte b) { return a > b ? a : b; }";
        await Assert.That(RunA(src, gb => { W8(gb, 0, 3); W8(gb, 1, 7); })).IsEqualTo((byte)7);
    }

    [Test]
    public async Task Enum_And_Const()
    {
        const string src = @"
enum Dir : byte { Up, Down, Left = 10, Right }
static byte Step(byte d) {
    const byte Bonus = 3;
    if (d == Dir.Right) return Bonus + 100;
    if (d == Dir.Up) return 1;
    return 0;
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 11))).IsEqualTo((byte)103); // Right = 11, +Bonus(3)+100
        await Assert.That(RunA(src, gb => W8(gb, 0, 0))).IsEqualTo((byte)1);   // Up = 0
    }

    [Test]
    public async Task Enum_InSwitch()
    {
        const string src = @"
enum Tile : byte { Empty, Wall, Coin = 5 }
static byte Value(byte t) {
    switch (t) {
        case Tile.Wall: return 1;
        case Tile.Coin: return 50;
        default: return 0;
    }
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 5))).IsEqualTo((byte)50); // Coin
        await Assert.That(RunA(src, gb => W8(gb, 0, 1))).IsEqualTo((byte)1);  // Wall
    }

    [Test]
    public async Task StaticField_MutableCounter()
    {
        const string src = @"
static byte counter;
static byte Main() { counter = 0; Inc(); Inc(); Inc(); return counter; }
static void Inc() { counter++; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)3);
    }

    [Test]
    public async Task StaticField_InitializedAtEntry()
    {
        const string src = @"
static byte score = 10;
static byte Main() { score += 5; return score; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)15);
    }

    [Test]
    public async Task StaticField_ReadonlyRom()
    {
        const string src = @"
static readonly ushort Base = 1000;
static ushort Main() { return Base; }";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)1000);
    }

    [Test]
    public async Task Array_FillAndSum()
    {
        // new byte[n], write a[i]=i*2, sum via a loop over a.Length
        const string src = @"
static byte Sum(byte n) {
    byte[] a = new byte[8];
    for (byte i = 0; i < n; i++) a[i] = (byte)(i * 2);
    byte acc = 0;
    for (byte i = 0; i < a.Length; i++) acc += a[i];
    return acc;
}";
        // n=4 fills a[0..3]=0,2,4,6; rest 0 -> sum over 8 elems = 0+2+4+6 = 12
        await Assert.That(RunA(src, gb => W8(gb, 0, 4))).IsEqualTo((byte)12);
    }

    [Test]
    public async Task Array_Initializer()
    {
        const string src = @"
static byte Get(byte i) {
    byte[] data = { 5, 10, 15, 20 };
    return data[i];
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 2))).IsEqualTo((byte)15);
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
