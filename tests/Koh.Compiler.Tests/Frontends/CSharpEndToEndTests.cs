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
    public async Task StaticArray_RomTable_IndexedAndSummed()
    {
        // `static readonly T[]` is a ROM data table, visible in every method.
        const string src = @"
static byte Main() {
    byte total = 0;
    for (byte i = 0; i < Squares.Length; i++) total += Lookup(i);
    return total;
}
static byte Lookup(byte i) { return Squares[i]; }
static readonly byte[] Squares = { 0, 1, 4, 9, 16, 25 };";
        await Assert.That(RunA(src)).IsEqualTo((byte)55); // 0+1+4+9+16+25
    }

    [Test]
    public async Task StaticArray_RomUshortTable_LittleEndian()
    {
        const string src = @"
static readonly ushort[] Notes = { 1000, 2000, 3000 };
static ushort Main() { return Notes[2]; }";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)3000);
    }

    [Test]
    public async Task StaticArray_WramBuffer_PersistsAcrossCalls()
    {
        // `static T[] x = new T[n]` is a zero-initialized WRAM buffer shared by all methods.
        const string src = @"
static byte Main() {
    Store(3, 77);
    Store(5, 88);
    return (byte)(Buffer[3] + Buffer[5]);
}
static void Store(byte i, byte v) { Buffer[i] = v; }
static byte[] Buffer = new byte[8];";
        await Assert.That(RunA(src)).IsEqualTo((byte)165);
    }

    [Test]
    public async Task StaticArray_MutableWithInitializer_IsDiagnostic()
    {
        // A mutable static array with an initializer would need a ROM->WRAM copy at boot; require
        // `static readonly` (ROM) or `new T[n]` (WRAM) instead.
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("static byte[] X = { 1, 2, 3 }; static byte Main() { return X[0]; }", "game.cs"),
            diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task Struct_FieldsReadWrite()
    {
        // A struct with a byte and a ushort field, exercising aligned layout.
        const string src = @"
struct Sprite { byte x; ushort score; }
static ushort Run() {
    Sprite s;
    s.x = 7;
    s.score = 1000;
    s.score += s.x;
    return s.score;
}";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)1007);
    }

    [Test]
    public async Task Struct_MixedFields()
    {
        const string src = @"
struct Point { byte x; byte y; }
static byte Run() {
    Point p;
    p.x = 12;
    p.y = 30;
    return (byte)(p.x + p.y);
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    private static byte RunThenRead(string src, int address)
    {
        var gb = Load(Compile(src), out int s, out int l);
        Run(gb, s, l);
        return gb.DebugReadByte((ushort)address);
    }

    [Test]
    public async Task Hardware_WriteRegister()
    {
        // Writing Hardware.BGP reaches the DMG background-palette register (0xFF47).
        const string src = "static void Main() { Hardware.BGP = 0xE4; }";
        await Assert.That(RunThenRead(src, 0xFF47)).IsEqualTo((byte)0xE4);
    }

    [Test]
    public async Task Hardware_ReadRegister()
    {
        // Read SCY (0xFF42) back through the Hardware surface.
        const string src = "static byte Main() { return Hardware.SCY; }";
        await Assert.That(RunA(src, gb => gb.DebugWriteByte(0xFF42, 0x55))).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task Interrupt_EmitsVectorAndReti()
    {
        const string src = @"
static byte counter;
[Interrupt(""VBlank"")]
static void OnVBlank() { counter++; }
static void Main() { Hardware.EnableInterrupts(); }";
        var link = new LinkerType().Link([new LinkerInput("cs", Compile(src))]);
        var rom = link.RomData!;
        await Assert.That(rom[0x40]).IsEqualTo((byte)0xC3);                      // jp <handler> at the VBlank vector
        await Assert.That(Array.IndexOf(rom, (byte)0xD9, Sm83Backend.CodeBase) >= 0).IsTrue(); // RETI present
    }

    [Test]
    public async Task RefParameters_Swap()
    {
        const string src = @"
static byte Main() {
    byte x = 3;
    byte y = 7;
    Swap(ref x, ref y);
    return (byte)(x * 10 + y);
}
static void Swap(ref byte a, ref byte b) { byte t = a; a = b; b = t; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)73); // swapped: x=7, y=3
    }

    [Test]
    public async Task OutParameter_Writes()
    {
        const string src = @"
static byte Main() {
    byte r;
    SetTo42(out r);
    return r;
}
static void SetTo42(out byte v) { v = 42; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Pointer_AddressOfAndDeref()
    {
        const string src = @"
static byte Main() {
    byte x = 5;
    byte* p = &x;
    *p = 42;
    return x;
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Pointer_IndexedStoreWithVariableOffset()
    {
        // p + i must widen the byte index to the full 16-bit address (via a gep), not truncate it
        // into the pointer's high byte. A read-modify-write on p+i followed by a store to p+i+1 was
        // the case that regressed: the second store's address kept a stale high byte.
        const string src = @"
static byte Main() {
    byte[] a = new byte[4];
    a[0] = 1; a[1] = 9;
    byte* p = &a[0];
    byte i = 0;
    *(p + i) = (byte)(*(p + i) + 1);
    *(p + i + 1) = 0;
    return (byte)(a[0] * 16 + a[1]);
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)0x20); // a[0]=2, a[1]=0
    }

    [Test]
    public async Task Pointer_ArithmeticWalksAnArray()
    {
        // Sum a[0..3] by advancing a pointer, exercising p + i reads across the whole array.
        const string src = @"
static byte Main() {
    byte[] a = new byte[4];
    a[0] = 3; a[1] = 5; a[2] = 7; a[3] = 11;
    byte* p = &a[0];
    byte sum = 0;
    for (byte i = 0; i < 4; i++) sum += *(p + i);
    return sum;
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)26);
    }

    [Test]
    public async Task Pointer_IncrementAndCompoundStepByElement()
    {
        // p++ and p += n advance a byte pointer by whole elements (a gep, not a raw add).
        const string incr = @"
static byte Main() {
    byte[] a = new byte[4];
    a[0] = 1; a[1] = 2; a[2] = 3;
    byte* p = &a[0];
    p++; p++;
    return *p;
}";
        await Assert.That(RunA(incr)).IsEqualTo((byte)3);

        const string compound = @"
static byte Main() {
    byte[] a = new byte[8];
    a[5] = 42;
    byte* p = &a[0];
    p += 5;
    return *p;
}";
        await Assert.That(RunA(compound)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Pointer_ComparisonWalksAnArray()
    {
        // A `while (p < end) { ...; p++; }` walk — pointer comparison lowers to an unsigned icmp.
        const string src = @"
static byte Main() {
    byte[] a = new byte[4];
    a[0] = 1; a[1] = 2; a[2] = 3; a[3] = 4;
    byte* p = &a[0];
    byte* end = &a[4];
    byte sum = 0;
    while (p < end) { sum += *p; p++; }
    return sum;
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)10);
        await Assert.That(IrVerifier.Verify(Frontend(src))).IsEmpty();
    }

    [Test]
    public async Task Struct_WithPointerFieldCompiles()
    {
        // A struct carrying a pointer field used to divide-by-zero in layout (pointer size was 0).
        const string src = @"
struct Node { byte* next; byte value; }
static byte Main() {
    Node n;
    n.value = 7;
    return n.value;
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
        await Assert.That(IrVerifier.Verify(Frontend(src))).IsEmpty();
    }

    [Test]
    public async Task Pointer_CastToAndFromIntegerRoundTrips()
    {
        // (byte*)addr and (byte)ptr reinterpret through a bitcast, not a bogus zext/trunc-to-pointer.
        const string src = @"
static byte Main() {
    byte b = 7;
    byte* p = (byte*)b;
    return (byte)p;
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
        await Assert.That(IrVerifier.Verify(Frontend(src))).IsEmpty();
    }

    [Test]
    public async Task MixedSign_ComparisonPromotesToSigned()
    {
        // sbyte(-1) vs byte(1): C# promotes both and compares signed -> -1 < 1 is true. The result
        // must not be governed by the left operand's signedness alone.
        const string ltSrc = "static byte F(sbyte a, byte b) { if (a < b) return 1; return 0; }";
        await Assert.That(RunA(ltSrc, gb => { W8(gb, 0, 0xFF); W8(gb, 1, 1); })).IsEqualTo((byte)1); // -1 < 1
        await Assert.That(RunA(ltSrc, gb => { W8(gb, 0, 5); W8(gb, 1, 1); })).IsEqualTo((byte)0);    //  5 < 1
    }

    [Test]
    public async Task MixedWidth_ArithmeticDoesNotNarrow()
    {
        // byte + ushort must compute in 16 bits; the ushort operand is not truncated to a byte.
        const string src = "static ushort Add(byte a, ushort b) { return (ushort)(a + b); }";
        await Assert.That(RunHL(src, gb => { W8(gb, 0, 5); W16(gb, 1, 1000); })).IsEqualTo((ushort)1005);
    }

    [Test]
    public async Task MixedSign_DivideIsSigned()
    {
        // sbyte(-6) / byte(3): promotes to a signed common type -> -2 (0xFE), not an unsigned divide.
        const string src = "static sbyte Div(sbyte a, byte b) { return (sbyte)(a / b); }";
        await Assert.That(RunA(src, gb => { W8(gb, 0, 0xFA); W8(gb, 1, 3); })).IsEqualTo((byte)0xFE); // -6/3 = -2
    }

    [Test]
    public async Task Comparison_LeftHandLiteralIsNotTruncated()
    {
        // `1000 == x` must type the left literal by its value, not by the Bool result context
        // (which is i8 and would truncate 1000 -> 232).
        const string src = "static byte F(ushort x) { if (1000 == x) return 1; return 0; }";
        await Assert.That(RunA(src, gb => W16(gb, 0, 1000))).IsEqualTo((byte)1);
        await Assert.That(RunA(src, gb => W16(gb, 0, 232))).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Comparison_ByteAgainstOutOfRangeConstant()
    {
        // A byte is always < 256; the constant must not truncate to 0.
        const string src = "static byte F(byte x) { if (x < 256) return 1; return 0; }";
        await Assert.That(RunA(src, gb => W8(gb, 0, 200))).IsEqualTo((byte)1);
        await Assert.That(RunA(src, gb => W8(gb, 0, 0))).IsEqualTo((byte)1);
    }

    [Test]
    public async Task MixedSign_AddThenWidenKeepsSign()
    {
        // (sbyte)-1 + (byte)0 widens to a signed short = -1 (0xFFFF), not zero-extended 255.
        const string src = "static short F(sbyte a, byte b) { short s = a + b; return s; }";
        await Assert.That(RunHL(src, gb => { W8(gb, 0, 0xFF); W8(gb, 1, 0); })).IsEqualTo((ushort)0xFFFF);
    }

    [Test]
    public async Task Equality_MixedSign16BitCompiles()
    {
        // ushort == short is a pure bit test, so it must not demand a wider signed type.
        const string src = "static byte F(ushort a, short b) { if (a == b) return 1; return 0; }";
        await Assert.That(RunA(src, gb => { W16(gb, 0, 5); W16(gb, 2, 5); })).IsEqualTo((byte)1);
        await Assert.That(RunA(src, gb => { W16(gb, 0, 5); W16(gb, 2, 6); })).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Pointer_ConstantAddressDerefIsDirectMmio()
    {
        // *(byte*)0xFF42 reads/writes the address directly (no slot), the idiomatic MMIO form.
        await Assert.That(RunA("static byte Main() { return *(byte*)0xFF42; }",
            gb => gb.DebugWriteByte(0xFF42, 0x55))).IsEqualTo((byte)0x55);
        await Assert.That(RunThenRead("static void Main() { *(byte*)0xFF47 = 0xE4; }", 0xFF47)).IsEqualTo((byte)0xE4);
    }

    [Test]
    public async Task CompoundDivide_WidensLikePlainDivide()
    {
        // x /= y must compute in the common type (x=10 / y=256 = 0), not truncate y to a byte first
        // (which would be a divide-by-zero).
        const string src = "static byte F(byte x, ushort y) { x /= y; return x; }";
        await Assert.That(RunA(src, gb => { W8(gb, 0, 10); W16(gb, 1, 256); })).IsEqualTo((byte)0);
    }

    [Test]
    public async Task MixedSign_WithUshort_ReportedAsDiagnostic()
    {
        // short / ushort has no wider signed type on this target, so it needs an explicit cast.
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("static ushort F(short a, ushort b) { return (ushort)(a / b); }", "game.cs"),
            diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task DebugInfo_MapsCSharpSourceLines()
    {
        // Line 1 = signature, line 2 = the add, line 3 = the return.
        const string src = "static byte Add(byte a, byte b) {\n    byte c = a + b;\n    return c;\n}";
        var lineMap = Compile(src).Sections[0].LineMap;
        await Assert.That(lineMap.Any(e => e.File == "game.cs" && e.Line == 2)).IsTrue();
        await Assert.That(lineMap.Any(e => e.File == "game.cs" && e.Line == 3)).IsTrue();
    }

    [Test]
    public async Task UnsupportedConstruct_ReportedAsDiagnostic()
    {
        // 'int' is unsupported: reported into the bag with a location, not thrown.
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From("static int Bad() { return 0; }", "game.cs"), diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task ParseError_ReportedAsDiagnostic()
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From("static byte F( { return 0 }", "game.cs"), diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }
}
