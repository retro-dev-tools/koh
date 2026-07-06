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

    private static uint RunI32(string src, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
        Run(gb, s, l);
        return ((uint)gb.Registers.DE << 16) | gb.Registers.HL; // i32: high word DE, low word HL
    }

    private static bool HasError(string src)
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        return diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    private static void W8(GameBoySystem gb, int offset, int value) =>
        gb.DebugWriteByte((ushort)(Sm83Backend.WramBase + offset), (byte)value);

    private static void W16(GameBoySystem gb, int offset, int value)
    {
        gb.DebugWriteByte((ushort)(Sm83Backend.WramBase + offset), (byte)(value & 0xFF));
        gb.DebugWriteByte((ushort)(Sm83Backend.WramBase + offset + 1), (byte)((value >> 8) & 0xFF));
    }

    private static void W32(GameBoySystem gb, int offset, long value)
    {
        for (int i = 0; i < 4; i++)
            gb.DebugWriteByte((ushort)(Sm83Backend.WramBase + offset + i), (byte)((value >> (8 * i)) & 0xFF));
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
    public async Task CharLiteral_IsItsCode()
    {
        // Char literals are byte codes; char arithmetic gives font/tile offsets.
        await Assert.That(RunA("static byte Main() { byte c = 'A'; return c; }")).IsEqualTo((byte)65);
        await Assert.That(RunA("static byte Main() { return (byte)('Z' - 'A'); }")).IsEqualTo((byte)25);
    }

    [Test]
    public async Task StringLiteral_LocalByteArray()
    {
        const string src = @"
static byte Main() {
    byte[] s = ""HELLO"";
    return (byte)(s[0] + s.Length); // 'H'(72) + 5
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)77);
    }

    [Test]
    public async Task StringLiteral_RomTextTable()
    {
        // `static readonly byte[] = "..."` is ROM character data (for HUD/menu text).
        const string src = @"
static byte Main() {
    ushort sum = 0;
    for (byte i = 0; i < Label.Length; i++) sum = (ushort)(sum + Label[i]);
    return (byte)sum;
}
static readonly byte[] Label = ""AB""; // 65 + 66 = 131";
        await Assert.That(RunA(src)).IsEqualTo((byte)131);
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

    [Test]
    public async Task NestedStruct_FieldAccess()
    {
        // A struct field whose type is itself a struct — composite entities.
        const string src = @"
struct Point { byte x; byte y; }
struct Entity { Point pos; Point vel; byte hp; }
static byte Run() {
    Entity e;
    e.pos.x = 5;
    e.vel.x = 9;   // must not alias e.pos
    e.hp = 100;
    return (byte)(e.pos.x + e.vel.x + e.hp); // 5 + 9 + 100
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)114);
    }

    [Test]
    public async Task NestedStruct_InArrayAndCopied()
    {
        // Nested structs compose with struct arrays and whole-struct copy.
        const string src = @"
struct Point { byte x; ushort y; }
struct Mob { Point pos; byte kind; }
static ushort Run() {
    Mob[] mobs = new Mob[4];
    Point p;
    p.x = 3;
    p.y = 1000;
    mobs[2].pos = p;          // copy a Point into a nested field of an array element
    mobs[2].kind = 7;
    return (ushort)(mobs[2].pos.y + mobs[2].pos.x + mobs[2].kind); // 1000 + 3 + 7
}";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)1010);
    }

    [Test]
    public async Task RefStructParam_MutatesCaller()
    {
        // A function can operate on an entity via `ref` — the callee shares the caller's storage.
        const string src = @"
struct Point { byte x; byte y; }
static byte Run() {
    Point p;
    p.x = 5;
    p.y = 10;
    Move(ref p);
    return (byte)(p.x + p.y); // 6 + 12
}
static void Move(ref Point q) { q.x = (byte)(q.x + 1); q.y = (byte)(q.y + 2); }";
        await Assert.That(RunA(src)).IsEqualTo((byte)18);
    }

    [Test]
    public async Task RefStructParam_ArrayElementAndNestedField()
    {
        // `ref` works on a struct-array element and on a nested struct field.
        const string src = @"
struct Point { byte x; byte y; }
struct Entity { Point pos; byte hp; }
static byte Run() {
    Entity[] es = new Entity[4];
    es[2].hp = 50;
    Damage(ref es[2], 20);        // ref array element
    Bump(ref es[2].pos);          // ref nested field
    return (byte)(es[2].hp + es[2].pos.x); // 30 + 9
}
static void Damage(ref Entity e, byte amt) { e.hp = (byte)(e.hp - amt); }
static void Bump(ref Point p) { p.x = (byte)(p.x + 9); }";
        await Assert.That(RunA(src)).IsEqualTo((byte)39);
    }

    [Test]
    public async Task StructParam_ByValueIsDiagnostic()
    {
        // A by-value struct parameter would need a copy the backend can't make; require ref/in/out.
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("struct P { byte x; } static byte Read(P q) { return q.x; }", "game.cs"),
            diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task NestedStruct_CyclicIsDiagnostic()
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("struct A { A self; } static byte Run() { return 0; }", "game.cs"),
            diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task StructArray_ElementFieldsIndependent()
    {
        // Array of structs (the entity-list pattern): each element's fields are addressed by stride,
        // so writes to one element don't disturb another.
        const string src = @"
struct P { byte a; ushort b; }
static ushort Run() {
    P[] items = new P[3];
    items[0].b = 111;
    items[1].b = 2222;
    items[2].b = 333;
    items[1].a = 9;
    return (ushort)(items[1].b + items[1].a); // 2222 + 9
}";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)2231);
    }

    [Test]
    public async Task StructArray_LoopWithVariableIndexAndLength()
    {
        const string src = @"
struct Enemy { byte hp; }
static byte Run() {
    Enemy[] e = new Enemy[5];
    for (byte i = 0; i < e.Length; i++) e[i].hp = (byte)(i + 1);
    byte total = 0;
    for (byte i = 0; i < e.Length; i++) total += e[i].hp;
    return total; // 1+2+3+4+5
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)15);
    }

    [Test]
    public async Task Struct_ValueCopy()
    {
        // Whole-struct assignment copies bytes; the copy is independent of the source.
        const string src = @"
struct P { byte x; ushort y; }
static ushort Run() {
    P a;
    a.x = 7;
    a.y = 1000;
    P b;
    b = a;
    b.x = 9;          // mutating the copy must not touch a
    return (ushort)(b.y + b.x - a.x); // 1000 + 9 - 7
}";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)1002);
    }

    [Test]
    public async Task StructArray_MissingSizeIsDiagnostic()
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("struct S { byte a; } static byte Run() { S[] s; return 0; }", "game.cs"),
            diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
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
    public async Task Int32_AccumulateBeyond16Bits()
    {
        // A running total that overflows 16 bits stays correct in an int.
        const string src = @"
static int Main() {
    int sum = 0;
    for (int i = 0; i < 1000; i++) sum = sum + i;
    return sum;
}";
        await Assert.That(RunI32(src)).IsEqualTo(499500u); // 0+1+...+999
    }

    [Test]
    public async Task Int32_ParamsAndReturnThroughCall()
    {
        const string src = @"
static int Main() { return Add(65000, 5000); }
static int Add(int a, int b) { return a + b; }";
        await Assert.That(RunI32(src)).IsEqualTo(70000u);
    }

    [Test]
    public async Task UInt32_BitwiseAcrossWords()
    {
        const string src = @"
static uint Main() {
    uint a = 0x00FF00FF;
    uint b = 0x0F0F0F0F;
    return a & b;
}";
        await Assert.That(RunI32(src)).IsEqualTo(0x000F000Fu);
    }

    [Test]
    public async Task Int32_SignedComparisonAcrossBoundary()
    {
        const string src = "static byte F(int a, int b) { if (a < b) return 1; return 0; }";
        await Assert.That(RunA(src, gb => { W32(gb, 0, 5); W32(gb, 4, 100000); })).IsEqualTo((byte)1);
        await Assert.That(RunA(src, gb => { W32(gb, 0, 100000); W32(gb, 4, 5); })).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Int32_MultiplyReportedAsDiagnostic()
    {
        // The SM83 backend has no 32-bit multiply/divide/shift; it must diagnose, not crash.
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("static int Main() { int a = 1000; int b = 1000; return a * b; }", "game.cs"),
            diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
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
        // 'long' is unsupported: reported into the bag with a location, not thrown.
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From("static long Bad() { return 0; }", "game.cs"), diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task ParseError_ReportedAsDiagnostic()
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From("static byte F( { return 0 }", "game.cs"), diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task CallArityMismatch_ReportedAsDiagnostic()
    {
        // A call whose argument count differs from the callee's parameter count is a diagnostic,
        // not a crash while binding positional arguments.
        await Assert.That(HasError(
            "static byte Add(byte a, byte b) { return a + b; }\nstatic byte Main() { return Add(1, 2, 3); }")).IsTrue();
        await Assert.That(HasError(
            "static byte Add(byte a, byte b) { return a + b; }\nstatic byte Main() { return Add(1); }")).IsTrue();
    }

    [Test]
    public async Task StringLiteralInScalarContext_ReportedAsDiagnostic()
    {
        // A string literal is only valid as a byte[] initializer; used as a scalar it is a diagnostic,
        // not a FormatException while converting the token value to a number.
        await Assert.That(HasError("static byte Main() { byte x = \"hi\"; return x; }")).IsTrue();
    }

    [Test]
    public async Task MixedSign_UshortPlusSbyte_PromotesToSignedInt()
    {
        // (ushort)0 + (sbyte)-1 must promote to signed i32 (-1 == 0xFFFFFFFF), not stay u16 (65535).
        const string src = "static uint Main() { ushort a = 0; sbyte b = -1; return (uint)(a + b); }";
        await Assert.That(RunI32(src)).IsEqualTo(0xFFFFFFFFu);
    }

    [Test]
    public async Task Ternary_WithoutExpectedType_InfersFromBranches()
    {
        // A ternary with no target type must infer its result type from its branches. Two i32 branches
        // must not be truncated to u16.
        const string src = "static uint Main() { bool c = true; return (uint)(c ? 100000 : 0); }";
        await Assert.That(RunI32(src)).IsEqualTo(100000u);
    }

    [Test]
    public async Task NegativeArrayLength_ReportedAsDiagnostic()
    {
        // A negative static-array size is a diagnostic, not a negative IrType.Array length.
        await Assert.That(HasError("static byte[] Buf = new byte[-1];\nstatic byte Main() { return 0; }")).IsTrue();
    }

    [Test]
    public async Task NegativeLocalArrayLength_ReportedAsDiagnostic()
    {
        // The negative-length guard must also cover local `new T[n]` and struct arrays, not just statics.
        await Assert.That(HasError("static byte Main() { byte[] buf = new byte[-1]; return 0; }")).IsTrue();
    }

    [Test]
    public async Task NegatedLiteral_IsSigned_InComparison()
    {
        // `-5` from the unsigned literal 5 must be a signed -5, not an unsigned 251: `x < -5` compares
        // against -5, and `x > -1` against -1. Without this the negation wraps to a large positive.
        await Assert.That(RunA("static byte Main() { int x = 100; return (byte)(x < -5 ? 1 : 0); }")).IsEqualTo((byte)0);
        await Assert.That(RunA("static byte Main() { byte x = 5; return (byte)(x > -1 ? 1 : 0); }")).IsEqualTo((byte)1);
        // -1000 < -999 is true; the negated literals must both be signed for the ordering to hold.
        await Assert.That(RunA("static byte Main() { int x = -1000; return (byte)(x < -999 ? 1 : 0); }")).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Ternary_InfersFromArithmeticAndCallBranches()
    {
        // InferType must size the result slot from arithmetic/call branches, not just literals — else a
        // wide branch truncates to the default u16. `a + b` = 70000 must survive the ternary.
        const string arith =
            "static byte Main() { uint a = 70000; uint b = 0; bool c = true; return (byte)((c ? a + b : 0u) == 70000u ? 1 : 0); }";
        await Assert.That(RunA(arith)).IsEqualTo((byte)1);

        // A call branch (int return) opposite a byte must not shrink the slot to a byte.
        const string call =
            "static byte Main() { byte v = 1; bool c = true; return (byte)((c ? Big() : v) == 300 ? 1 : 0); }\n"
            + "static ushort Big() { return 300; }";
        await Assert.That(RunA(call)).IsEqualTo((byte)1);
    }

    [Test]
    public async Task MixedSignMultiply_StaysSixteenBit()
    {
        // ushort * short (and ushort * sbyte) only needs the low 16 bits, which don't depend on sign, so
        // it must compile as a 16-bit multiply rather than promoting to an unsupported 32-bit one.
        // 300 * 5 = 1500 (0x05DC); the low byte is 0xDC = 220.
        await Assert.That(RunA(
            "static byte Main() { ushort u = 300; short s = 5; return (byte)((u * s) & 0xFF); }")).IsEqualTo((byte)220);
        await Assert.That(CompilesClean("static ushort F(ushort u, sbyte s) { return (ushort)(u * s); }")).IsTrue();
    }

    [Test]
    public async Task MixedSignDivide_StillRequiresExplicitCast()
    {
        // Divide's result DOES depend on sign, and there is no 32-bit divide, so mixed ushort/short must
        // still be a diagnostic asking for a cast (the multiply relaxation must not leak to divide).
        await Assert.That(HasError("static ushort F(ushort u, short s) { return (ushort)(u / s); }")).IsTrue();
    }

    [Test]
    public async Task SharedInterruptHelper_IsRejected()
    {
        // Bump() is called from both the handler and main-line code; its static WRAM frame would be
        // corrupted if the interrupt fired mid-call, so the backend must reject it.
        const string shared = @"
static byte counter;
static void Bump() { counter++; }
[Interrupt(""VBlank"")]
static void OnVBlank() { Bump(); }
static void Main() { Bump(); Hardware.EnableInterrupts(); }";
        await Assert.That(() => Compile(shared)).Throws<NotSupportedException>();

        // A helper called only from main (not from the handler) is fine.
        const string mainOnly = @"
static byte counter;
static void Bump() { counter++; }
[Interrupt(""VBlank"")]
static void OnVBlank() { counter++; }
static void Main() { Bump(); Hardware.EnableInterrupts(); }";
        await Assert.That(CompilesClean(mainOnly)).IsTrue();
    }

    [Test]
    public async Task BitwiseComplement_Byte()
    {
        // ~x must be implemented (it was referenced in InferType but not lowered, so it fell through
        // to "unsupported unary operator"). ~0x0F over a byte is 0xF0.
        await Assert.That(RunA("static byte Main() { byte x = 0x0F; return (byte)~x; }")).IsEqualTo((byte)0xF0);
        // Constant operands fold: ~0x0F narrowed to a byte is still 0xF0.
        await Assert.That(RunA("static byte Main() { return (byte)~0x0F; }")).IsEqualTo((byte)0xF0);
    }

    [Test]
    public async Task BitwiseComplement_Ushort()
    {
        // The all-ones mask must match the operand width: ~0x00FF over a ushort is 0xFF00, not 0x00FF.
        await Assert.That(RunHL("static ushort Main() { ushort x = 0x00FF; return (ushort)~x; }"))
            .IsEqualTo((ushort)0xFF00);
    }

    [Test]
    public async Task VariableShift_NormalCount()
    {
        // A variable shift count below the type width shifts by exactly that amount.
        await Assert.That(RunHL("static ushort Main() { ushort x = 5; ushort n = 3; return (ushort)(x << n); }"))
            .IsEqualTo((ushort)40);
        await Assert.That(RunHL("static ushort Main() { ushort x = 0x0140; ushort n = 4; return (ushort)(x >> n); }"))
            .IsEqualTo((ushort)0x0014);
    }

    [Test]
    public async Task VariableShift_CountHighByteNotTruncated()
    {
        // The count shares the value's (16-bit) type, so loading only its low byte truncated it: a count
        // of 257 shifted by 1 (0x0101 & 0xFF) instead of saturating to the width. The clamp must shift a
        // count that meets or exceeds the width all the way out (16 bits -> 0), not by its low byte.
        await Assert.That(RunHL("static ushort Main() { ushort x = 1; ushort n = 257; return (ushort)(x << n); }"))
            .IsEqualTo((ushort)0); // would be 2 (1 << 1) if the high byte were dropped
        await Assert.That(RunHL("static ushort Main() { ushort x = 1; ushort n = 256; return (ushort)(x << n); }"))
            .IsEqualTo((ushort)0); // would be 1 (1 << 0) if the high byte were dropped
    }

    [Test]
    public async Task VariableShift_ArithmeticRightSaturates()
    {
        // An arithmetic right shift by at least the width fills with the sign bit; the clamp must preserve
        // that (0x8000 >> 16 -> 0xFFFF), not shift by a truncated low byte.
        await Assert.That(RunHL(
            "static short Main() { short x = -32768; short n = 300; return (short)(x >> n); }"))
            .IsEqualTo(unchecked((ushort)-1));
    }

    [Test]
    public async Task DuplicateFunction_ReportedAsDiagnostic()
    {
        // Two functions with the same name silently overwrote the earlier binding (and emitted two IR
        // functions with the same name); the duplicate must be a diagnostic instead.
        await Assert.That(HasError(
            "static byte F() { return 1; }\nstatic byte F() { return 2; }\nstatic byte Main() { return F(); }")).IsTrue();
    }

    [Test]
    public async Task DuplicateStaticField_ReportedAsDiagnostic()
    {
        // Duplicate static fields (which share one namespace across consts/globals/arrays) emitted two
        // globals with the same name; the duplicate must be a diagnostic.
        await Assert.That(HasError("static byte g;\nstatic byte g;\nstatic byte Main() { return g; }")).IsTrue();
        await Assert.That(HasError("const byte k = 1;\nstatic byte k;\nstatic byte Main() { return k; }")).IsTrue();
    }

    [Test]
    public async Task DuplicateStructAndEnum_ReportedAsDiagnostic()
    {
        await Assert.That(HasError(
            "struct P { byte x; }\nstruct P { byte y; }\nstatic byte Main() { return 0; }")).IsTrue();
        await Assert.That(HasError(
            "enum E { A }\nenum E { B }\nstatic byte Main() { return 0; }")).IsTrue();
    }

    private static bool CompilesClean(string src)
    {
        var diagnostics = new DiagnosticBag();
        new Sm83Backend().Compile(
            new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics), diagnostics);
        return !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }
}
