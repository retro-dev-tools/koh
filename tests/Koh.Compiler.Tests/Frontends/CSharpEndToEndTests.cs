using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
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
    private static IrModule Frontend(string src)
    {
        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        // CLAUDE.md: assert IrVerifier.Verify(module).IsEmpty() for new lowering. Verifying here covers
        // every end-to-end test (coroutines, classes, generics, LINQ, …) centrally. Skip when the
        // frontend itself reported an error — that IR is expected-incomplete (HasError/diagnostic tests).
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

    /// <summary>As <see cref="Compile"/>, but runs the IR optimizer first (the driver's default path).</summary>
    private static EmitModel CompileOpt(string src)
    {
        var module = Frontend(src);
        IrOptimizer.Optimize(module);
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    private static byte RunAOpt(string src, Action<GameBoySystem>? args = null)
    {
        var gb = Load(CompileOpt(src), out int s, out int l);
        args?.Invoke(gb);
        Run(gb, s, l);
        return gb.Registers.A;
    }

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

    private static ulong RunI64(string src, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
        Run(gb, s, l);
        ulong result = 0; // i64 is returned little-endian in ReturnScratch memory
        for (int i = 0; i < 8; i++)
            result |= (ulong)gb.DebugReadByte((ushort)(Sm83Backend.ReturnScratch + i)) << (8 * i);
        return result;
    }

    private static void W64(GameBoySystem gb, int offset, long value)
    {
        for (int i = 0; i < 8; i++)
            gb.DebugWriteByte(
                (ushort)(Sm83Backend.WramBase + offset + i),
                (byte)((value >> (8 * i)) & 0xFF)
            );
    }

    private static UInt128 RunI128(string src, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
        Run(gb, s, l);
        UInt128 result = 0; // i128 is returned little-endian in ReturnScratch memory (16 bytes)
        for (int i = 0; i < 16; i++)
            result |= (UInt128)gb.DebugReadByte((ushort)(Sm83Backend.ReturnScratch + i)) << (8 * i);
        return result;
    }

    private static bool HasError(string src)
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        return diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>True when the backend reports an error diagnostic (a target limit hit by legal input,
    /// e.g. a bank overflow) rather than throwing.</summary>
    private static bool BackendHasError(string src)
    {
        var diagnostics = new DiagnosticBag();
        new Sm83Backend().Compile(Frontend(src), diagnostics);
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
            gb.DebugWriteByte(
                (ushort)(Sm83Backend.WramBase + offset + i),
                (byte)((value >> (8 * i)) & 0xFF)
            );
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
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W8(gb, 0, 40);
                        W8(gb, 1, 2);
                    }
                )
            )
            .IsEqualTo((byte)42);
    }

    [Test]
    public async Task Sum_WhileLoop()
    {
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
static ushort Gcd(ushort a, ushort b) {
    while (b != 0) { ushort t = b; b = a % b; a = t; }
    return a;
}";
        await Assert
            .That(
                RunHL(
                    src,
                    gb =>
                    {
                        W16(gb, 0, 48);
                        W16(gb, 2, 36);
                    }
                )
            )
            .IsEqualTo((ushort)12);
    }

    [Test]
    public async Task Max_IfElse()
    {
        const string src =
            "static byte Max(byte a, byte b) { if (a > b) return a; else return b; }";
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W8(gb, 0, 3);
                        W8(gb, 1, 7);
                    }
                )
            )
            .IsEqualTo((byte)7);
    }

    [Test]
    public async Task MethodCall_AcrossFunctions()
    {
        const string src =
            @"
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
        await Assert
            .That(
                RunHL(
                    src,
                    gb =>
                    {
                        W16(gb, 0, -1000 & 0xFFFF);
                        W16(gb, 2, 7);
                    }
                )
            )
            .IsEqualTo((ushort)0xFF72);
    }

    [Test]
    public async Task For_Loop()
    {
        // sum 1..n with a for loop; n=5 -> 15
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string andSrc =
            "static byte F(byte a, byte b) { if (a > 0 && b > 0) return 1; return 0; }";
        await Assert
            .That(
                RunA(
                    andSrc,
                    gb =>
                    {
                        W8(gb, 0, 5);
                        W8(gb, 1, 3);
                    }
                )
            )
            .IsEqualTo((byte)1);
        await Assert
            .That(
                RunA(
                    andSrc,
                    gb =>
                    {
                        W8(gb, 0, 0);
                        W8(gb, 1, 3);
                    }
                )
            )
            .IsEqualTo((byte)0);

        const string orSrc = "static byte F(byte a) { if (a == 0 || a == 5) return 1; return 0; }";
        await Assert.That(RunA(orSrc, gb => W8(gb, 0, 5))).IsEqualTo((byte)1);
        await Assert.That(RunA(orSrc, gb => W8(gb, 0, 3))).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Ternary()
    {
        const string src = "static byte Max(byte a, byte b) { return a > b ? a : b; }";
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W8(gb, 0, 3);
                        W8(gb, 1, 7);
                    }
                )
            )
            .IsEqualTo((byte)7);
    }

    [Test]
    public async Task Enum_And_Const()
    {
        const string src =
            @"
enum Dir : byte { Up, Down, Left = 10, Right }
static byte Step(byte d) {
    const byte Bonus = 3;
    if (d == Dir.Right) return Bonus + 100;
    if (d == Dir.Up) return 1;
    return 0;
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 11))).IsEqualTo((byte)103); // Right = 11, +Bonus(3)+100
        await Assert.That(RunA(src, gb => W8(gb, 0, 0))).IsEqualTo((byte)1); // Up = 0
    }

    [Test]
    public async Task Const_EnumMemberInDataTable()
    {
        // A qualified enum member is a compile-time constant usable in const/ROM-table initializers.
        const string src =
            @"
enum Pal : byte { White = 3, Black = 7 }
static readonly byte[] T = { Pal.White, Pal.Black, (byte)(Pal.White + Pal.Black) };
static byte Main() { return T[2]; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)10); // 3 + 7
    }

    [Test]
    public async Task ConstEval_BadConstantsAreDiagnostics()
    {
        // Malformed constant expressions must be reported, not crash the compiler (they run in the
        // collection passes, outside the per-method try/catch, and used to throw raw exceptions).
        await Assert
            .That(HasError("const byte X = 1 / 0; static byte Main() { return 0; }"))
            .IsTrue();
        await Assert
            .That(HasError("const byte X = 5 % 0; static byte Main() { return 0; }"))
            .IsTrue();
        await Assert
            .That(HasError("const byte X = \"hi\"; static byte Main() { return 0; }"))
            .IsTrue();
        await Assert
            .That(HasError("const byte X = 300; static byte Main() { return 0; }"))
            .IsTrue();
        await Assert
            .That(
                HasError("static readonly ushort[] T = { 70000 }; static byte Main() { return 0; }")
            )
            .IsTrue();
        await Assert
            .That(HasError("static byte[] B = new byte[70000]; static byte Main() { return 0; }"))
            .IsTrue();
    }

    [Test]
    public async Task Class_MalformedDeclarationsAreDiagnostics()
    {
        // A class with no instance fields aliases all instances; a duplicate instance method binds the
        // wrong overload; a ref/out/in instance-method parameter would silently pass by value.
        await Assert
            .That(
                HasError(
                    "class Empty { static byte n; } static byte Main() { Empty e = new Empty(); return 0; }"
                )
            )
            .IsTrue();
        await Assert
            .That(
                HasError(
                    "class C { byte v; byte Get() { return v; } byte Get(byte x) { return x; } } static byte Main() { return 0; }"
                )
            )
            .IsTrue();
        await Assert
            .That(
                HasError(
                    "class C { byte v; void Set(ref byte x) { x = v; } } static byte Main() { return 0; }"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Enum_InSwitch()
    {
        const string src =
            @"
enum Tile : byte { Empty, Wall, Coin = 5 }
static byte Value(byte t) {
    switch (t) {
        case Tile.Wall: return 1;
        case Tile.Coin: return 50;
        default: return 0;
    }
}";
        await Assert.That(RunA(src, gb => W8(gb, 0, 5))).IsEqualTo((byte)50); // Coin
        await Assert.That(RunA(src, gb => W8(gb, 0, 1))).IsEqualTo((byte)1); // Wall
    }

    [Test]
    public async Task StaticField_MutableCounter()
    {
        const string src =
            @"
static byte counter;
static byte Main() { counter = 0; Inc(); Inc(); Inc(); return counter; }
static void Inc() { counter++; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)3);
    }

    [Test]
    public async Task StaticField_InitializedAtEntry()
    {
        const string src =
            @"
static byte score = 10;
static byte Main() { score += 5; return score; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)15);
    }

    [Test]
    public async Task StaticField_ReadonlyRom()
    {
        const string src =
            @"
static readonly ushort Base = 1000;
static ushort Main() { return Base; }";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)1000);
    }

    [Test]
    public async Task Array_FillAndSum()
    {
        // new byte[n], write a[i]=i*2, sum via a loop over a.Length
        const string src =
            @"
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
        const string src =
            @"
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
        await Assert
            .That(RunA("static byte Main() { byte c = 'A'; return c; }"))
            .IsEqualTo((byte)65);
        await Assert
            .That(RunA("static byte Main() { return (byte)('Z' - 'A'); }"))
            .IsEqualTo((byte)25);
    }

    [Test]
    public async Task StringLiteral_LocalByteArray()
    {
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
static readonly ushort[] Notes = { 1000, 2000, 3000 };
static ushort Main() { return Notes[2]; }";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)3000);
    }

    [Test]
    public async Task StaticArray_WramBuffer_PersistsAcrossCalls()
    {
        // `static T[] x = new T[n]` is a zero-initialized WRAM buffer shared by all methods.
        const string src =
            @"
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
            SourceText.From(
                "static byte[] X = { 1, 2, 3 }; static byte Main() { return X[0]; }",
                "game.cs"
            ),
            diagnostics
        );
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task Struct_FieldsReadWrite()
    {
        // A struct with a byte and a ushort field, exercising aligned layout.
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
            SourceText.From(
                "struct P { byte x; } static byte Read(P q) { return q.x; }",
                "game.cs"
            ),
            diagnostics
        );
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task NestedStruct_CyclicIsDiagnostic()
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("struct A { A self; } static byte Run() { return 0; }", "game.cs"),
            diagnostics
        );
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task StructArray_ElementFieldsIndependent()
    {
        // Array of structs (the entity-list pattern): each element's fields are addressed by stride,
        // so writes to one element don't disturb another.
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
            SourceText.From(
                "struct S { byte a; } static byte Run() { S[] s; return 0; }",
                "game.cs"
            ),
            diagnostics
        );
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
    public async Task StaticClass_QualifiedAndSiblingCalls()
    {
        // A program written as top-level static classes: sibling calls resolve unqualified within a
        // class (Twice), cross-class calls are qualified (Helper.Ten), and Main-in-a-class is the entry.
        const string src =
            "static class M { static byte Main() { return (byte)(Twice(3) + Helper.Ten()); } "
            + "static byte Twice(byte x) { return (byte)(x + x); } } "
            + "static class Helper { static byte Ten() { return 10; } }";
        await Assert.That(RunA(src)).IsEqualTo((byte)16);
    }

    [Test]
    public async Task StaticClass_PerClassStaticFieldsDoNotCollide()
    {
        // Two static classes each declare a static field named `n`; they must be independent, and each
        // method resolves its own class's field (qualified as A.n / B.n), not a shared global.
        const string src =
            "static class P { static byte Main() { A.Bump(); B.Bump(); B.Bump(); return (byte)(A.Get() + B.Get()); } } "
            + "static class A { static byte n; static void Bump() { n = (byte)(n + 7); } static byte Get() { return n; } } "
            + "static class B { static byte n; static void Bump() { n = (byte)(n + 1); } static byte Get() { return n; } }";
        await Assert.That(RunA(src)).IsEqualTo((byte)9); // A.n = 7, B.n = 2
    }

    [Test]
    public async Task StaticClass_ConstReferencedBySiblingInitializer()
    {
        // A static class's const is keyed Class.name; a sibling's initializer references it by simple
        // name at collection time — both a chained const and an array size must resolve in that scope.
        // Regression: the const-fold lookup was unqualified and missed the class's own const.
        const string src =
            "static class Cfg { "
            + "const byte Width = 4; "
            + "const byte Cells = (byte)(Width * Width); "
            + "static byte[] grid = new byte[Cells]; "
            + "static byte Main() { grid[15] = 9; return (byte)(grid[15] + Cells); } }";
        await Assert.That(RunA(src)).IsEqualTo((byte)25); // 9 + 16
    }

    [Test]
    public async Task ReservedIntrinsicNames_RejectUserClass()
    {
        // A user class named after an intrinsic surface would have its member access hijacked; reject it.
        await Assert
            .That(HasError("static class Gb { static byte Vram() { return 0; } }"))
            .IsTrue();
        await Assert
            .That(HasError("static class Hardware { static byte LY() { return 0; } }"))
            .IsTrue();
    }

    [Test]
    public async Task MultipleMain_IsReported()
    {
        // Two Main entry points across static classes are ambiguous; report rather than pick one silently.
        await Assert
            .That(
                HasError(
                    "static class A { static void Main() {} } static class B { static void Main() {} }"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task InstanceMethodNamedMain_IsNotAnEntry()
    {
        // A reference type's instance method named Main (its function is Widget.Main, taking an implicit
        // `this`) must not be mistaken for the program entry alongside the real top-level Main — doing so
        // would spuriously report two entries. Regression: entry detection matched on the simple name.
        const string src =
            "class Widget { byte v; byte Main() { return 99; } }\n"
            + "static byte Main() { return 7; }";
        await Assert.That(CompilesClean(src)).IsTrue();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
    }

    [Test]
    public async Task Namespace_BlockScopedIsFlattened()
    {
        // A block-scoped namespace is unwrapped like a file-scoped one, lifting its members to the top.
        const string src =
            "static class App { static byte Main() { return Lib.Answer(); } }\n"
            + "namespace Koh.GameBoy { static class Lib { static byte Answer() { return 42; } } }";
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Namespace_FileScopedIsFlattened()
    {
        // Framework-style source in a file-scoped namespace: the namespace is dropped, so its static
        // classes are program-level and callable from a global-namespace Main.
        const string src =
            "static class App { static byte Main() { return Lib.Answer(); } }\n"
            + "namespace Koh.GameBoy;\n"
            + "static class Lib { static byte Answer() { return 42; } }";
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task StaticClass_StaticFieldIsState()
    {
        // A static field in a static class is program-scope WRAM state, shared across its methods.
        const string src =
            "static class Counter { static byte Main() { Bump(); Bump(); Bump(); return n; } "
            + "static void Bump() { n = (byte)(n + 1); } static byte n; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)3);
    }

    [Test]
    public async Task Gb_RegionBaseIsConstantPointer()
    {
        // Gb.Vram lowers to a byte* at the VRAM base (0x8000); writes through it land there.
        // LCD off first so the PPU isn't locking VRAM when the store runs (as the game does).
        const string src =
            "static void Main() { Hardware.LCDC = 0x00; byte* v = Gb.Vram; *(v + 5) = 0x3C; }";
        await Assert.That(RunThenRead(src, 0x8005)).IsEqualTo((byte)0x3C);
    }

    [Test]
    public async Task Gb_TileMapRegionBase()
    {
        // Gb.TileMap points at the background tilemap (0x9800).
        const string src =
            "static void Main() { Hardware.LCDC = 0x00; byte* m = Gb.TileMap; *m = 0x12; }";
        await Assert.That(RunThenRead(src, 0x9800)).IsEqualTo((byte)0x12);
    }

    [Test]
    public async Task Gb_RegionNameDoesNotCollideWithUserGlobal()
    {
        // A Gb region lowers to a fixed-address global; its emitted name is qualified (Gb.Vram) so it
        // can't clash with a user global of the same simple name. Regression: the region was named
        // "Vram", producing two globals named "Vram" and invalid link output.
        const string src =
            "static byte Main() { Hardware.LCDC = 0x00; Vram = 5; byte* v = Gb.Vram; *v = 9; return Vram; }\n"
            + "static byte Vram;";
        var names = Frontend(src).Globals.Select(g => g.Name).ToList();
        await Assert.That(names.Distinct().Count()).IsEqualTo(names.Count); // no duplicate global symbols
        await Assert.That(RunA(src)).IsEqualTo((byte)5); // the user global is independent of the region
    }

    [Test]
    public async Task StackAlloc_BufferRoundTrips()
    {
        // stackalloc reserves a frame buffer reachable as a byte*; write two cells and sum them.
        const string src =
            "static byte Main() { byte* a = stackalloc byte[4]; *(a + 0) = 7; *(a + 3) = 30; "
            + "return (byte)(*(a + 0) + *(a + 3)); }";
        await Assert.That(RunA(src)).IsEqualTo((byte)37);
    }

    [Test]
    public async Task Interrupt_EmitsVectorAndReti()
    {
        const string src =
            @"
static byte counter;
[Interrupt(""VBlank"")]
static void OnVBlank() { counter++; }
static void Main() { Hardware.EnableInterrupts(); }";
        var link = new LinkerType().Link([new LinkerInput("cs", Compile(src))]);
        var rom = link.RomData!;
        await Assert.That(rom[0x40]).IsEqualTo((byte)0xC3); // jp <handler> at the VBlank vector
        await Assert.That(Array.IndexOf(rom, (byte)0xD9, Sm83Backend.CodeBase) >= 0).IsTrue(); // RETI present
    }

    [Test]
    public async Task RefParameters_Swap()
    {
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string incr =
            @"
static byte Main() {
    byte[] a = new byte[4];
    a[0] = 1; a[1] = 2; a[2] = 3;
    byte* p = &a[0];
    p++; p++;
    return *p;
}";
        await Assert.That(RunA(incr)).IsEqualTo((byte)3);

        const string compound =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        const string src =
            @"
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
        await Assert
            .That(
                RunA(
                    ltSrc,
                    gb =>
                    {
                        W8(gb, 0, 0xFF);
                        W8(gb, 1, 1);
                    }
                )
            )
            .IsEqualTo((byte)1); // -1 < 1
        await Assert
            .That(
                RunA(
                    ltSrc,
                    gb =>
                    {
                        W8(gb, 0, 5);
                        W8(gb, 1, 1);
                    }
                )
            )
            .IsEqualTo((byte)0); //  5 < 1
    }

    [Test]
    public async Task MixedWidth_ArithmeticDoesNotNarrow()
    {
        // byte + ushort must compute in 16 bits; the ushort operand is not truncated to a byte.
        const string src = "static ushort Add(byte a, ushort b) { return (ushort)(a + b); }";
        await Assert
            .That(
                RunHL(
                    src,
                    gb =>
                    {
                        W8(gb, 0, 5);
                        W16(gb, 1, 1000);
                    }
                )
            )
            .IsEqualTo((ushort)1005);
    }

    [Test]
    public async Task MixedSign_DivideIsSigned()
    {
        // sbyte(-6) / byte(3): promotes to a signed common type -> -2 (0xFE), not an unsigned divide.
        const string src = "static sbyte Div(sbyte a, byte b) { return (sbyte)(a / b); }";
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W8(gb, 0, 0xFA);
                        W8(gb, 1, 3);
                    }
                )
            )
            .IsEqualTo((byte)0xFE); // -6/3 = -2
    }

    [Test]
    public async Task Int32_AccumulateBeyond16Bits()
    {
        // A running total that overflows 16 bits stays correct in an int.
        const string src =
            @"
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
        const string src =
            @"
static int Main() { return Add(65000, 5000); }
static int Add(int a, int b) { return a + b; }";
        await Assert.That(RunI32(src)).IsEqualTo(70000u);
    }

    [Test]
    public async Task UInt32_BitwiseAcrossWords()
    {
        const string src =
            @"
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
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W32(gb, 0, 5);
                        W32(gb, 4, 100000);
                    }
                )
            )
            .IsEqualTo((byte)1);
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W32(gb, 0, 100000);
                        W32(gb, 4, 5);
                    }
                )
            )
            .IsEqualTo((byte)0);
    }

    [Test]
    public async Task Int32_MultiplyComputes()
    {
        // The SM83 backend lowers 32-bit multiply via the generic width-N runtime routine.
        await Assert
            .That(RunI32("static int Main() { int a = 1000; int b = 1000; return a * b; }"))
            .IsEqualTo(1000000u);
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
        await Assert
            .That(
                RunHL(
                    src,
                    gb =>
                    {
                        W8(gb, 0, 0xFF);
                        W8(gb, 1, 0);
                    }
                )
            )
            .IsEqualTo((ushort)0xFFFF);
    }

    [Test]
    public async Task Equality_MixedSign16BitCompiles()
    {
        // ushort == short is a pure bit test, so it must not demand a wider signed type.
        const string src = "static byte F(ushort a, short b) { if (a == b) return 1; return 0; }";
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W16(gb, 0, 5);
                        W16(gb, 2, 5);
                    }
                )
            )
            .IsEqualTo((byte)1);
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W16(gb, 0, 5);
                        W16(gb, 2, 6);
                    }
                )
            )
            .IsEqualTo((byte)0);
    }

    [Test]
    public async Task Pointer_ConstantAddressDerefIsDirectMmio()
    {
        // *(byte*)0xFF42 reads/writes the address directly (no slot), the idiomatic MMIO form.
        await Assert
            .That(
                RunA(
                    "static byte Main() { return *(byte*)0xFF42; }",
                    gb => gb.DebugWriteByte(0xFF42, 0x55)
                )
            )
            .IsEqualTo((byte)0x55);
        await Assert
            .That(RunThenRead("static void Main() { *(byte*)0xFF47 = 0xE4; }", 0xFF47))
            .IsEqualTo((byte)0xE4);
    }

    [Test]
    public async Task CompoundDivide_WidensLikePlainDivide()
    {
        // x /= y must compute in the common type (x=10 / y=256 = 0), not truncate y to a byte first
        // (which would be a divide-by-zero).
        const string src = "static byte F(byte x, ushort y) { x /= y; return x; }";
        await Assert
            .That(
                RunA(
                    src,
                    gb =>
                    {
                        W8(gb, 0, 10);
                        W16(gb, 1, 256);
                    }
                )
            )
            .IsEqualTo((byte)0);
    }

    [Test]
    public async Task MixedSign_WithUshort_PromotesToSignedInt()
    {
        // short / ushort promotes to signed int (17-bit range fits i32), which the backend now divides;
        // -100 / 7 == -14, and (ushort)(-14) == 0xFFF2.
        await Assert
            .That(
                RunHL(
                    "static ushort F(short a, ushort b) { return (ushort)(a / b); }",
                    gb =>
                    {
                        W16(gb, 0, -100 & 0xFFFF);
                        W16(gb, 2, 7);
                    }
                )
            )
            .IsEqualTo((ushort)0xFFF2);
    }

    [Test]
    public async Task DebugInfo_MapsCSharpSourceLines()
    {
        // Line 1 = signature, line 2 = the add, line 3 = the return.
        const string src =
            "static byte Add(byte a, byte b) {\n    byte c = a + b;\n    return c;\n}";
        var lineMap = Compile(src).Sections[0].LineMap;
        await Assert.That(lineMap.Any(e => e.File == "game.cs" && e.Line == 2)).IsTrue();
        await Assert.That(lineMap.Any(e => e.File == "game.cs" && e.Line == 3)).IsTrue();
    }

    [Test]
    public async Task UnsupportedConstruct_ReportedAsDiagnostic()
    {
        // 'float' is unsupported: reported into the bag with a location, not thrown.
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("static float Bad() { return 0; }", "game.cs"),
            diagnostics
        );
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task ParseError_ReportedAsDiagnostic()
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("static byte F( { return 0 }", "game.cs"),
            diagnostics
        );
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task CallArityMismatch_ReportedAsDiagnostic()
    {
        // A call whose argument count differs from the callee's parameter count is a diagnostic,
        // not a crash while binding positional arguments.
        await Assert
            .That(
                HasError(
                    "static byte Add(byte a, byte b) { return a + b; }\nstatic byte Main() { return Add(1, 2, 3); }"
                )
            )
            .IsTrue();
        await Assert
            .That(
                HasError(
                    "static byte Add(byte a, byte b) { return a + b; }\nstatic byte Main() { return Add(1); }"
                )
            )
            .IsTrue();
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
        const string src =
            "static uint Main() { ushort a = 0; sbyte b = -1; return (uint)(a + b); }";
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
        await Assert
            .That(HasError("static byte[] Buf = new byte[-1];\nstatic byte Main() { return 0; }"))
            .IsTrue();
    }

    [Test]
    public async Task NegativeLocalArrayLength_ReportedAsDiagnostic()
    {
        // The negative-length guard must also cover local `new T[n]` and struct arrays, not just statics.
        await Assert
            .That(HasError("static byte Main() { byte[] buf = new byte[-1]; return 0; }"))
            .IsTrue();
    }

    [Test]
    public async Task NegatedLiteral_IsSigned_InComparison()
    {
        // `-5` from the unsigned literal 5 must be a signed -5, not an unsigned 251: `x < -5` compares
        // against -5, and `x > -1` against -1. Without this the negation wraps to a large positive.
        await Assert
            .That(RunA("static byte Main() { int x = 100; return (byte)(x < -5 ? 1 : 0); }"))
            .IsEqualTo((byte)0);
        await Assert
            .That(RunA("static byte Main() { byte x = 5; return (byte)(x > -1 ? 1 : 0); }"))
            .IsEqualTo((byte)1);
        // -1000 < -999 is true; the negated literals must both be signed for the ordering to hold.
        await Assert
            .That(RunA("static byte Main() { int x = -1000; return (byte)(x < -999 ? 1 : 0); }"))
            .IsEqualTo((byte)1);
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
    public async Task MixedSignMultiply_PromotesToInt()
    {
        // ushort * short promotes to signed int (matching C#); the low 16 bits are unchanged.
        // 300 * 5 = 1500 (0x05DC); the low byte is 0xDC = 220.
        await Assert
            .That(
                RunA(
                    "static byte Main() { ushort u = 300; short s = 5; return (byte)((u * s) & 0xFF); }"
                )
            )
            .IsEqualTo((byte)220);
        await Assert
            .That(CompilesClean("static ushort F(ushort u, sbyte s) { return (ushort)(u * s); }"))
            .IsTrue();
    }

    [Test]
    public async Task MixedSignDivide_PromotesToInt()
    {
        // ushort / short now promotes to signed int and divides (it used to require an explicit cast).
        // 300 / -7 == -42 (truncated toward zero); (short)(-42) == 0xFFD6.
        await Assert
            .That(
                RunHL(
                    "static short F(ushort u, short s) { return (short)(u / s); }",
                    gb =>
                    {
                        W16(gb, 0, 300);
                        W16(gb, 2, -7 & 0xFFFF);
                    }
                )
            )
            .IsEqualTo((ushort)0xFFD6);
    }

    [Test]
    public async Task SharedInterruptHelper_IsRejected()
    {
        // Bump() is called from both the handler and main-line code; its static WRAM frame would be
        // corrupted if the interrupt fired mid-call, so the backend must reject it.
        const string shared =
            @"
static byte counter;
static void Bump() { counter++; }
[Interrupt(""VBlank"")]
static void OnVBlank() { Bump(); }
static void Main() { Bump(); Hardware.EnableInterrupts(); }";
        await Assert.That(() => Compile(shared)).Throws<NotSupportedException>();

        // A helper called only from main (not from the handler) is fine.
        const string mainOnly =
            @"
static byte counter;
static void Bump() { counter++; }
[Interrupt(""VBlank"")]
static void OnVBlank() { counter++; }
static void Main() { Bump(); Hardware.EnableInterrupts(); }";
        await Assert.That(CompilesClean(mainOnly)).IsTrue();
    }

    [Test]
    public async Task RecursiveInterruptHandler_IsRejected()
    {
        // A recursive handler would hit the memory-return epilogue (plain RET) instead of RETI with a
        // balanced stack, so it must be a diagnostic rather than a silently broken handler.
        const string src =
            @"
[Interrupt(""VBlank"")]
static void H() { H(); }
static void Main() { Hardware.EnableInterrupts(); }";
        await Assert.That(BackendHasError(src)).IsTrue();
    }

    [Test]
    public async Task BitwiseComplement_Byte()
    {
        // ~x must be implemented (it was referenced in InferType but not lowered, so it fell through
        // to "unsupported unary operator"). ~0x0F over a byte is 0xF0.
        await Assert
            .That(RunA("static byte Main() { byte x = 0x0F; return (byte)~x; }"))
            .IsEqualTo((byte)0xF0);
        // Constant operands fold: ~0x0F narrowed to a byte is still 0xF0.
        await Assert.That(RunA("static byte Main() { return (byte)~0x0F; }")).IsEqualTo((byte)0xF0);
    }

    [Test]
    public async Task BitwiseComplement_Ushort()
    {
        // The all-ones mask must match the operand width: ~0x00FF over a ushort is 0xFF00, not 0x00FF.
        await Assert
            .That(RunHL("static ushort Main() { ushort x = 0x00FF; return (ushort)~x; }"))
            .IsEqualTo((ushort)0xFF00);
    }

    [Test]
    public async Task VariableShift_NormalCount()
    {
        // A variable shift count below the type width shifts by exactly that amount.
        await Assert
            .That(
                RunHL(
                    "static ushort Main() { ushort x = 5; ushort n = 3; return (ushort)(x << n); }"
                )
            )
            .IsEqualTo((ushort)40);
        await Assert
            .That(
                RunHL(
                    "static ushort Main() { ushort x = 0x0140; ushort n = 4; return (ushort)(x >> n); }"
                )
            )
            .IsEqualTo((ushort)0x0014);
    }

    [Test]
    public async Task VariableShift_CountHighByteNotTruncated()
    {
        // The count shares the value's (16-bit) type, so loading only its low byte truncated it: a count
        // of 257 shifted by 1 (0x0101 & 0xFF) instead of saturating to the width. The clamp must shift a
        // count that meets or exceeds the width all the way out (16 bits -> 0), not by its low byte.
        await Assert
            .That(
                RunHL(
                    "static ushort Main() { ushort x = 1; ushort n = 257; return (ushort)(x << n); }"
                )
            )
            .IsEqualTo((ushort)0); // would be 2 (1 << 1) if the high byte were dropped
        await Assert
            .That(
                RunHL(
                    "static ushort Main() { ushort x = 1; ushort n = 256; return (ushort)(x << n); }"
                )
            )
            .IsEqualTo((ushort)0); // would be 1 (1 << 0) if the high byte were dropped
    }

    [Test]
    public async Task VariableShift_ArithmeticRightSaturates()
    {
        // An arithmetic right shift by at least the width fills with the sign bit; the clamp must preserve
        // that (0x8000 >> 16 -> 0xFFFF), not shift by a truncated low byte.
        await Assert
            .That(
                RunHL(
                    "static short Main() { short x = -32768; short n = 300; return (short)(x >> n); }"
                )
            )
            .IsEqualTo(unchecked((ushort)-1));
    }

    [Test]
    public async Task DuplicateFunction_ReportedAsDiagnostic()
    {
        // Two functions with the same name silently overwrote the earlier binding (and emitted two IR
        // functions with the same name); the duplicate must be a diagnostic instead.
        await Assert
            .That(
                HasError(
                    "static byte F() { return 1; }\nstatic byte F() { return 2; }\nstatic byte Main() { return F(); }"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task DuplicateStaticField_ReportedAsDiagnostic()
    {
        // Duplicate static fields (which share one namespace across consts/globals/arrays) emitted two
        // globals with the same name; the duplicate must be a diagnostic.
        await Assert
            .That(HasError("static byte g;\nstatic byte g;\nstatic byte Main() { return g; }"))
            .IsTrue();
        await Assert
            .That(HasError("const byte k = 1;\nstatic byte k;\nstatic byte Main() { return k; }"))
            .IsTrue();
    }

    [Test]
    public async Task DuplicateStructAndEnum_ReportedAsDiagnostic()
    {
        await Assert
            .That(
                HasError(
                    "struct P { byte x; }\nstruct P { byte y; }\nstatic byte Main() { return 0; }"
                )
            )
            .IsTrue();
        await Assert
            .That(HasError("enum E { A }\nenum E { B }\nstatic byte Main() { return 0; }"))
            .IsTrue();
    }

    [Test]
    public async Task Int32_Negate()
    {
        // i32 negation lowers to a 32-bit Sub(0, x), which the backend supports (add/sub have no width
        // cap); only sbyte/short negation was covered before. -100000 == 0xFFFE7960.
        await Assert
            .That(RunI32("static int Main() { int x = 100000; return -x; }"))
            .IsEqualTo(0xFFFE7960u);
    }

    [Test]
    public async Task HardwareNop_Emits()
    {
        // Hardware.Nop() maps to the `nop` intrinsic; it was mapped in the frontend but never exercised
        // end-to-end (only ei/di/halt were).
        await Assert
            .That(RunA("static byte Main() { Hardware.Nop(); return 42; }"))
            .IsEqualTo((byte)42);
    }

    [Test]
    public async Task UnknownInterruptKind_ReportedAsDiagnostic()
    {
        // A typo'd interrupt kind mapped to no vector and was silently treated as an ordinary function;
        // it must now be a diagnostic.
        await Assert
            .That(HasError("[Interrupt(\"Vblnk\")]\nstatic void OnX() { }\nstatic void Main() { }"))
            .IsTrue();
        // A recognized kind still compiles clean.
        await Assert
            .That(
                CompilesClean(
                    "[Interrupt(\"VBlank\")]\nstatic void OnVBlank() { }\nstatic void Main() { Hardware.EnableInterrupts(); }"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Int32_Multiply()
    {
        const string src = "static int Mul(int a, int b) { return a * b; }";
        await Assert
            .That(
                RunI32(
                    src,
                    gb =>
                    {
                        W32(gb, 0, 100000);
                        W32(gb, 4, 3);
                    }
                )
            )
            .IsEqualTo(300000u);
        // Low 32 bits only: 0x10000 * 0x10000 = 0x1_0000_0000 -> 0.
        await Assert
            .That(
                RunI32(
                    src,
                    gb =>
                    {
                        W32(gb, 0, 0x10000);
                        W32(gb, 4, 0x10000);
                    }
                )
            )
            .IsEqualTo(0u);
    }

    [Test]
    public async Task Int32_UnsignedDivideAndRemainder()
    {
        await Assert
            .That(
                RunI32(
                    "static uint D(uint a, uint b) { return a / b; }",
                    gb =>
                    {
                        W32(gb, 0, 1000000);
                        W32(gb, 4, 7);
                    }
                )
            )
            .IsEqualTo(142857u);
        await Assert
            .That(
                RunI32(
                    "static uint R(uint a, uint b) { return a % b; }",
                    gb =>
                    {
                        W32(gb, 0, 1000000);
                        W32(gb, 4, 7);
                    }
                )
            )
            .IsEqualTo(1u);
        // A divisor larger than the dividend: quotient 0, remainder = dividend.
        await Assert
            .That(
                RunI32(
                    "static uint D(uint a, uint b) { return a / b; }",
                    gb =>
                    {
                        W32(gb, 0, 5);
                        W32(gb, 4, 100);
                    }
                )
            )
            .IsEqualTo(0u);
        await Assert
            .That(
                RunI32(
                    "static uint R(uint a, uint b) { return a % b; }",
                    gb =>
                    {
                        W32(gb, 0, 5);
                        W32(gb, 4, 100);
                    }
                )
            )
            .IsEqualTo(5u);
    }

    [Test]
    public async Task Int32_SignedDivideAndRemainder()
    {
        await Assert
            .That(
                RunI32(
                    "static int D(int a, int b) { return a / b; }",
                    gb =>
                    {
                        W32(gb, 0, -1000000);
                        W32(gb, 4, 7);
                    }
                )
            )
            .IsEqualTo(unchecked((uint)(-142857)));
        // C# truncated remainder takes the dividend's sign: -1000000 % 7 == -1.
        await Assert
            .That(
                RunI32(
                    "static int R(int a, int b) { return a % b; }",
                    gb =>
                    {
                        W32(gb, 0, -1000000);
                        W32(gb, 4, 7);
                    }
                )
            )
            .IsEqualTo(unchecked((uint)(-1)));
        await Assert
            .That(
                RunI32(
                    "static int D(int a, int b) { return a / b; }",
                    gb =>
                    {
                        W32(gb, 0, -1000000);
                        W32(gb, 4, -7);
                    }
                )
            )
            .IsEqualTo(142857u);
    }

    [Test]
    public async Task Int32_ShiftLeft()
    {
        await Assert
            .That(
                RunI32(
                    "static uint S(uint a, int n) { return a << n; }",
                    gb =>
                    {
                        W32(gb, 0, 1);
                        W32(gb, 4, 20);
                    }
                )
            )
            .IsEqualTo(0x00100000u);
        // Constant shift.
        await Assert
            .That(RunI32("static uint S(uint a) { return a << 24; }", gb => W32(gb, 0, 0xFF)))
            .IsEqualTo(0xFF000000u);
    }

    [Test]
    public async Task Int32_ShiftRightLogicalAndArithmetic()
    {
        await Assert
            .That(
                RunI32(
                    "static uint L(uint a, int n) { return a >> n; }",
                    gb =>
                    {
                        W32(gb, 0, 0x80000000);
                        W32(gb, 4, 4);
                    }
                )
            )
            .IsEqualTo(0x08000000u);
        // Arithmetic right shift of a negative int fills with the sign bit.
        await Assert
            .That(
                RunI32(
                    "static int A(int a, int n) { return a >> n; }",
                    gb =>
                    {
                        W32(gb, 0, -16);
                        W32(gb, 4, 2);
                    }
                )
            )
            .IsEqualTo(unchecked((uint)(-4)));
    }

    [Test]
    public async Task Int64_AddAndReturn()
    {
        // i64 add/sub run through the width-agnostic byte chains; i64 is returned via memory scratch.
        await Assert
            .That(
                RunI64(
                    "static long Add(long a, long b) { return a + b; }",
                    gb =>
                    {
                        W64(gb, 0, 0x1_0000_0000L);
                        W64(gb, 8, 0x2_0000_0002L);
                    }
                )
            )
            .IsEqualTo(0x3_0000_0002UL);
        await Assert
            .That(RunI64("static ulong Big() { ulong x = 0x0102030405060708; return x; }"))
            .IsEqualTo(0x0102030405060708UL);
    }

    [Test]
    public async Task Int64_MultiplyDivideRemainder()
    {
        // The generic width-N runtime routines serve N=8 unchanged.
        await Assert
            .That(
                RunI64(
                    "static long Mul(long a, long b) { return a * b; }",
                    gb =>
                    {
                        W64(gb, 0, 1_000_000L);
                        W64(gb, 8, 1_000_000L);
                    }
                )
            )
            .IsEqualTo(1_000_000_000_000UL);
        await Assert
            .That(
                RunI64(
                    "static ulong Div(ulong a, ulong b) { return a / b; }",
                    gb =>
                    {
                        W64(gb, 0, 1_000_000_000_000L);
                        W64(gb, 8, 7);
                    }
                )
            )
            .IsEqualTo(142_857_142_857UL);
        await Assert
            .That(
                RunI64(
                    "static long Rem(long a, long b) { return a % b; }",
                    gb =>
                    {
                        W64(gb, 0, -1_000_000_000_000L);
                        W64(gb, 8, 7);
                    }
                )
            )
            .IsEqualTo(unchecked((ulong)(-1L)));
    }

    [Test]
    public async Task Int64_ShiftAndCompare()
    {
        await Assert
            .That(
                RunI64(
                    "static ulong Shl(ulong a, int n) { return a << n; }",
                    gb =>
                    {
                        W64(gb, 0, 1);
                        W32(gb, 8, 40);
                    }
                )
            )
            .IsEqualTo(1UL << 40);
        await Assert
            .That(
                RunI64(
                    "static long Sar(long a, int n) { return a >> n; }",
                    gb =>
                    {
                        W64(gb, 0, -1_000_000_000_000L);
                        W32(gb, 8, 8);
                    }
                )
            )
            .IsEqualTo(unchecked((ulong)(-1_000_000_000_000L >> 8)));
        // 64-bit ordering: 0x1_0000_0000 > 0xFFFF_FFFF must hold across the 32-bit boundary.
        await Assert
            .That(
                RunA(
                    "static byte Main() { long a = 0x100000000; long b = 0xFFFFFFFF; return (byte)(a > b ? 1 : 0); }"
                )
            )
            .IsEqualTo((byte)1);
    }

    [Test]
    public async Task Recursion_Factorial()
    {
        // Direct recursion: each invocation saves/restores its shared static frame on the software stack.
        // Main must be first — the harness boots at the first emitted function.
        const string src =
            @"
static ushort Main() { return Fact(6); }
static ushort Fact(ushort n) {
    if (n <= 1) return 1;
    return (ushort)(n * Fact((ushort)(n - 1)));
}";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)720);
    }

    [Test]
    public async Task Recursion_DeepBeyondHardwareStack()
    {
        // A recursive program relocates the CALL stack into WRAM, so recursion can go far deeper than
        // the ~60 levels the 127-byte HRAM stack allowed (which used to overflow into the I/O registers
        // and crash). 500 levels: sum 1..500 = 125250, low byte 66.
        const string src =
            @"
static byte Main() { return Sum(500); }
static byte Sum(ushort n) {
    if (n == 0) return 0;
    byte x = (byte)n;
    return (byte)(Sum((ushort)(n - 1)) + x);
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)66);
    }

    [Test]
    public async Task Recursion_Fibonacci()
    {
        // Tree recursion (two self-calls per frame) stresses the save/restore ordering.
        const string src =
            @"
static ushort Main() { return Fib(10); }
static ushort Fib(ushort n) {
    if (n < 2) return n;
    return (ushort)(Fib((ushort)(n - 1)) + Fib((ushort)(n - 2)));
}";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)55);
    }

    [Test]
    public async Task Recursion_Mutual()
    {
        // Mutual recursion: IsEven/IsOdd call each other, so both are in the cycle.
        const string src =
            @"
static byte Main() { return IsEven(10); }
static byte IsEven(byte n) { if (n == 0) return 1; return IsOdd((byte)(n - 1)); }
static byte IsOdd(byte n) { if (n == 0) return 0; return IsEven((byte)(n - 1)); }";
        await Assert.That(RunA(src)).IsEqualTo((byte)1);
        await Assert
            .That(
                RunA(
                    "static byte Main() { return IsEven(7); }\n"
                        + "static byte IsEven(byte n) { if (n == 0) return 1; return IsOdd((byte)(n - 1)); }\n"
                        + "static byte IsOdd(byte n) { if (n == 0) return 0; return IsEven((byte)(n - 1)); }"
                )
            )
            .IsEqualTo((byte)0);
    }

    [Test]
    public async Task RomBanking_ReadsBankedData()
    {
        // F0 (8KB) fills the ROM0 data window and F1 (16KB) fills ROM bank 1, so Mark lands in bank 2
        // (physical 0x8000, reachable only through the 0x4000 window after a bank switch). The cartridge
        // becomes MBC1; writing the bank number to 0x2000 selects it. Reading Mark without switching
        // would see bank 1 (zero), so a correct 0xAB proves the bank switch and MBC header both work.
        const string src =
            @"
static byte Main() {
    *(byte*)0x2000 = 2;
    return Mark[0];
}
static readonly byte[] F0 = new byte[8192];
static readonly byte[] F1 = new byte[16384];
static readonly byte[] Mark = { 0xAB, 0xCD };";
        await Assert.That(RunA(src)).IsEqualTo((byte)0xAB);
    }

    [Test]
    public async Task RomBanking_HeaderIsMbc1WhenBanked()
    {
        const string src =
            @"
static byte Main() { return Mark[0]; }
static readonly byte[] F0 = new byte[8192];
static readonly byte[] F1 = new byte[16384];
static readonly byte[] Mark = { 0xAB };";
        var link = new LinkerType().Link([new LinkerInput("cs", Compile(src))]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        await Assert.That(rom[0x0147]).IsEqualTo((byte)0x01); // MBC1
        await Assert.That(rom[0x0148]).IsEqualTo((byte)0x01); // 64KB (4 banks)
        // A ROM-only program (no banking) keeps cartridge type 0.
        var link2 = new LinkerType().Link([
            new LinkerInput("cs", Compile("static byte Main() { return 7; }")),
        ]);
        await Assert.That((link2.RomData ?? [])[0x0147]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task CodeBanking_OverflowFunctionsRunFromBank1()
    {
        // Generate more code than the ~7.8KB ROM0 code window holds, so trailing functions (and the
        // runtime) move into ROM bank 1. Main (in ROM0) calls Target, which lands in bank 1; bank 1 is
        // mapped by default and never switched, so the direct call reaches it. A correct 123 proves the
        // banked function executed and returned across the ROM0/bank-1 boundary.
        var sb = new System.Text.StringBuilder();
        sb.Append("static byte Main() { return Target(); }\n");
        for (int i = 0; i < 320; i++)
            sb.Append(
                $"static byte P{i}() {{ byte a = 1; byte b = 2; byte c = 3; byte d = 4; "
                    + $"return (byte)(a + b + c + d + {i % 200}); }}\n"
            );
        sb.Append("static byte Target() { return 123; }\n");
        string src = sb.ToString();

        // The cartridge must have banked (MBC1 header) for Target to be in bank 1.
        var model = Compile(src);
        var link = new LinkerType().Link([new LinkerInput("cs", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        await Assert.That(rom[0x0147]).IsEqualTo((byte)0x01); // MBC1 => banking activated
        await Assert.That(model.Sections.Any(s => s.Name == "CODEX")).IsTrue();

        // Run allowing PC across the ROM0 code window and the bank-1 window (0x4000-0x7FFF).
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = Sm83Backend.CodeBase;
        for (int steps = 0; steps < 500_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < Sm83Backend.CodeBase || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
        await Assert.That(gb.Registers.A).IsEqualTo((byte)123);
    }

    [Test]
    public async Task CodeBanking_MultiBankFarCalls()
    {
        // Enough code to need two or more overflow banks, which forces the far-call-thunk model: the
        // entry stays in ROM0 and every other function is banked. Main -> A -> B -> ... -> L, each in a
        // switchable bank reached through its ROM0 thunk (which maps the bank, calls, and restores it),
        // returning 77 back up the chain across bank boundaries.
        var sb = new System.Text.StringBuilder();
        sb.Append("static byte Main() { return A0(); }\n");
        const int fns = 12;
        for (int i = 0; i < fns; i++)
        {
            sb.Append($"static byte A{i}() {{ byte x = 1;\n");
            for (int j = 0; j < 200; j++)
                sb.Append($"x = (byte)(x + {j % 7});\n"); // padding to bulk up
            sb.Append(i + 1 < fns ? $"return A{i + 1}();\n}}\n" : "return 77;\n}\n");
        }
        string src = sb.ToString();

        var model = Compile(src);
        // Two or more banked code sections => the multi-bank far-call path ran.
        await Assert
            .That(model.Sections.Count(s => s.Name.StartsWith("CODEX")))
            .IsGreaterThanOrEqualTo(2);
        var link = new LinkerType().Link([new LinkerInput("cs", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        await Assert.That(rom[0x0147]).IsEqualTo((byte)0x01); // MBC1

        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = Sm83Backend.CodeBase;
        for (int steps = 0; steps < 2_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < Sm83Backend.CodeBase || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
        await Assert.That(gb.Registers.A).IsEqualTo((byte)77);
    }

    [Test]
    public async Task CodeBanking_RecursiveBankedFunction()
    {
        // A recursive function that is also banked exercises every convention at once: args via
        // ArgScratch, frame save/restore on the software stack, the far-call thunk, and ReturnScratch.
        // Padding forces the multi-bank model; Fact (banked, recursive) computes 5! = 120.
        var sb = new System.Text.StringBuilder();
        sb.Append("static byte Main() { return Fact(5); }\n");
        sb.Append(
            "static byte Fact(byte n) { if (n <= 1) return 1; return (byte)(n * Fact((byte)(n - 1))); }\n"
        );
        for (int i = 0; i < 12; i++)
        {
            sb.Append($"static byte P{i}() {{ byte x = 1;\n");
            for (int j = 0; j < 200; j++)
                sb.Append($"x = (byte)(x + {j % 7});\n");
            sb.Append("return x;\n}\n");
        }
        var model = Compile(sb.ToString());
        await Assert
            .That(model.Sections.Count(s => s.Name.StartsWith("CODEX")))
            .IsGreaterThanOrEqualTo(2);
        var link = new LinkerType().Link([new LinkerInput("cs", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = Sm83Backend.CodeBase;
        for (int steps = 0; steps < 2_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < Sm83Backend.CodeBase || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
        await Assert.That(gb.Registers.A).IsEqualTo((byte)120);
    }

    [Test]
    public async Task CodeAndDataBanking_BothOverflow_IsRejected()
    {
        // Code and data banking are mutually exclusive (the code bank must stay mapped), so a program
        // that overflows both must be a clean diagnostic, not a miscompile or an uncaught throw.
        var sb = new System.Text.StringBuilder();
        sb.Append("static byte Main() { return Target(); }\n");
        for (int i = 0; i < 320; i++)
            sb.Append(
                $"static byte P{i}() {{ byte a = 1; byte b = 2; byte c = 3; byte d = 4; "
                    + $"return (byte)(a + b + c + d + {i % 200}); }}\n"
            );
        sb.Append("static byte Target() { return 123; }\n");
        sb.Append("static readonly byte[] F0 = new byte[8192];\n");
        sb.Append("static readonly byte[] F1 = new byte[16384];\n");
        await Assert.That(BackendHasError(sb.ToString())).IsTrue();
    }

    [Test]
    public async Task Int128_MultiplyBeyond64Bits()
    {
        // The generic width-N routines serve N=16 unchanged: 2^32 * 2^32 = 2^64, a product no 64-bit
        // type can hold, returned in the 16-byte ReturnScratch.
        await Assert
            .That(
                RunI128(
                    "static UInt128 Mul(ulong a, ulong b) { return (UInt128)a * (UInt128)b; }",
                    gb =>
                    {
                        W64(gb, 0, 0x100000000L);
                        W64(gb, 8, 0x100000000L);
                    }
                )
            )
            .IsEqualTo((UInt128)1 << 64);
    }

    [Test]
    public async Task Int128_ConstantHighBytesAreZeroExtended()
    {
        // An i128 constant operand is a 64-bit long; its bytes 8..15 must be its sign extension, not a
        // repeat of the low bytes (a raw `value >> (8*k)` masks the shift count to 63, so byte 8 would
        // keep the operand's byte). `y & 0xFF` must clear every byte above the lowest.
        await Assert
            .That(
                RunI128(
                    "static UInt128 Main() { UInt128 x = 0x123456789ABCDEF0; UInt128 y = x + (x << 64); return y & 0xFF; }"
                )
            )
            .IsEqualTo((UInt128)0xF0);
    }

    [Test]
    public async Task Int128_AddShiftDivide()
    {
        await Assert
            .That(
                RunI128(
                    "static UInt128 Add(ulong a, ulong b) { return (UInt128)a + (UInt128)b; }",
                    gb =>
                    {
                        W64(gb, 0, unchecked((long)0xFFFFFFFFFFFFFFFF));
                        W64(gb, 8, 1);
                    }
                )
            )
            .IsEqualTo((UInt128)1 << 64); // 2^64-1 + 1 = 2^64
        await Assert
            .That(
                RunI128(
                    "static UInt128 Shl(ulong a, int n) { return (UInt128)a << n; }",
                    gb =>
                    {
                        W64(gb, 0, 1);
                        W32(gb, 8, 100);
                    }
                )
            )
            .IsEqualTo((UInt128)1 << 100);
        await Assert
            .That(
                RunI128(
                    "static UInt128 Div(ulong a, ulong b) { return ((UInt128)a * (UInt128)a) / (UInt128)b; }",
                    gb =>
                    {
                        W64(gb, 0, 1_000_000_000L);
                        W64(gb, 8, 7);
                    }
                )
            )
            .IsEqualTo(((UInt128)1_000_000_000 * 1_000_000_000) / 7);
    }

    [Test]
    public async Task PointerIndexing_DerefsThroughOffset()
    {
        // p[i] is *(p + i): a raw pointer can be indexed like an array.
        await Assert
            .That(
                RunA(
                    "static byte Main() { byte* a = Mem.Alloc(8); a[0] = 3; a[3] = 40; return (byte)(a[0] + a[3]); }"
                )
            )
            .IsEqualTo((byte)43);
    }

    [Test]
    public async Task Arena_AllocatesDistinctBlocks()
    {
        // Mem.Alloc bumps a heap pointer; two allocations are distinct, writable, and independent.
        await Assert
            .That(
                RunA(
                    "static byte Main() { byte* a = Mem.Alloc(4); byte* b = Mem.Alloc(4); "
                        + "a[0] = 11; b[0] = 22; return (byte)(a[0] + b[0] + (a == b ? 100 : 0)); }"
                )
            )
            .IsEqualTo((byte)33);
    }

    [Test]
    public async Task Arena_ResetReclaimsEverything()
    {
        // Mem.Reset() frees the whole arena at once, so the next allocation reuses the same address.
        await Assert
            .That(
                RunA(
                    "static byte Main() { byte* a = Mem.Alloc(4); a[0] = 5; Mem.Reset(); byte* b = Mem.Alloc(4); "
                        + "return (byte)(a == b ? 42 : 0); }"
                )
            )
            .IsEqualTo((byte)42);
    }

    [Test]
    public async Task Arena_MemIsReservedClassName_IsDiagnostic()
    {
        // A user class named `Mem` would have its calls hijacked by the allocator lowering, so it is
        // reported rather than silently mis-compiled.
        await Assert
            .That(HasError("class Mem { byte x; } static byte Main() { return 0; }"))
            .IsTrue();
    }

    [Test]
    public async Task Class_NewAndFields()
    {
        // A class is heap-allocated (new bump-allocates and zeroes it); fields are accessed through the
        // instance pointer.
        await Assert
            .That(
                RunA(
                    "static byte Main() { Point p = new Point(); p.x = 10; p.y = 20; return (byte)(p.x + p.y); }\n"
                        + "class Point { byte x; byte y; }"
                )
            )
            .IsEqualTo((byte)30);
    }

    [Test]
    public async Task Class_NewZeroesReusedMemory()
    {
        // `new` must zero the instance even when the arena hands back dirty memory. Write a field, reset
        // the arena, re-allocate the same address: the zeroing loop must clear the stale value.
        const string src =
            @"
static byte Main() {
    Box a = new Box();
    a.v = 99;
    Mem.Reset();
    Box b = new Box();
    return b.v;
}
class Box { byte v; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Class_InstanceMethods()
    {
        // Instance methods receive an implicit `this`; a method body reads/writes fields bare and can
        // call other instance methods.
        const string src =
            @"
static byte Main() { Counter c = new Counter(); c.Add(10); c.Bump(); return c.Get(); }
class Counter {
    byte n;
    void Add(byte v) { n = (byte)(n + v); }
    void Bump() { Add(1); }
    byte Get() { return n; }
}";
        await Assert.That(RunA(src)).IsEqualTo((byte)11);
    }

    [Test]
    public async Task Class_MethodComputesOverFields()
    {
        // A method computing from several fields, called on two independent instances.
        const string src =
            @"
static byte Main() {
    Rect a = new Rect(); a.w = 3; a.h = 4;
    Rect b = new Rect(); b.w = 5; b.h = 6;
    return (byte)(a.Area() + b.Area());
}
class Rect { byte w; byte h; byte Area() { return (byte)(w * h); } }";
        await Assert.That(RunA(src)).IsEqualTo((byte)42); // 12 + 30
    }

    [Test]
    public async Task Class_IndependentInstances()
    {
        // Two instances are distinct heap objects with independent fields.
        await Assert
            .That(
                RunA(
                    "static byte Main() { Point a = new Point(); Point b = new Point(); a.x = 5; b.x = 9; return (byte)(a.x * 10 + b.x); }\n"
                        + "class Point { byte x; byte y; }"
                )
            )
            .IsEqualTo((byte)59);
    }

    [Test]
    public async Task Class_TypedParameterAndReturn()
    {
        // A class type can name a parameter and a return: the instance is passed and returned as its
        // heap pointer, and the callee resolves .field/.method on it.
        await Assert
            .That(
                RunA(
                    "static byte Main() { Box b = new Box(); b.v = 8; return Get(b); }\n"
                        + "static byte Get(Box x) { return x.v; }\n"
                        + "class Box { byte v; }"
                )
            )
            .IsEqualTo((byte)8);
        await Assert
            .That(
                RunA(
                    "static byte Main() { Box b = Make(); return b.v; }\n"
                        + "static Box Make() { Box x = new Box(); x.v = 5; return x; }\n"
                        + "class Box { byte v; }"
                )
            )
            .IsEqualTo((byte)5);
    }

    [Test]
    public async Task Class_SelfReferentialLinkedList()
    {
        // A class field of the same (or another) class type is a heap pointer, so a linked list can be
        // built and walked; class assignment (a.next = b, cur = cur.next) copies the reference.
        const string src =
            @"
static byte Main() {
    Node a = new Node(); a.v = 1;
    Node b = new Node(); b.v = 2;
    Node c = new Node(); c.v = 3;
    a.next = b; b.next = c;
    byte total = 0;
    Node cur = a;
    while (Live(cur)) { total = (byte)(total + cur.v); cur = cur.next; }
    return total;
}
static byte Live(byte* p) { return (byte)(p == (byte*)0 ? 0 : 1); }
class Node { byte v; Node next; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)6); // 1 + 2 + 3
    }

    [Test]
    public async Task Class_InstanceUsedAsPointerValue()
    {
        // A class instance can be used as a value (returned or passed as byte*), not only for
        // .field/.method access: `return this;` and passing a class local to a byte* parameter.
        await Assert
            .That(
                RunA(
                    "static byte Main() { Box b = new Box(); b.v = 9; byte* p = b.Ptr(); return *(byte*)p; }\n"
                        + "class Box { byte v; byte* Ptr() { return this; } }"
                )
            )
            .IsEqualTo((byte)9);
        await Assert
            .That(
                RunA(
                    "static byte Main() { Box b = new Box(); b.v = 7; return Read(b); }\n"
                        + "static byte Read(byte* p) { return *(byte*)p; }\n"
                        + "class Box { byte v; }"
                )
            )
            .IsEqualTo((byte)7);
    }

    [Test]
    public async Task Generics_Monomorphized()
    {
        // A generic method is specialized per concrete type argument (Max$byte, Max$ushort).
        await Assert
            .That(
                RunA(
                    "static byte Main() { return (byte)Max<byte>(3, 7); }\n"
                        + "static T Max<T>(T a, T b) { if (a > b) return a; return b; }"
                )
            )
            .IsEqualTo((byte)7);
        await Assert
            .That(
                RunHL(
                    "static ushort Main() { return Max<ushort>(300, 100); }\n"
                        + "static T Max<T>(T a, T b) { if (a > b) return a; return b; }"
                )
            )
            .IsEqualTo((ushort)300);
    }

    [Test]
    public async Task Generics_TransitiveInstantiation()
    {
        // A specialized body may name further generic instances (Double<byte> uses Id<byte>); the
        // work-list instantiates them transitively.
        const string src =
            @"
static byte Main() { return (byte)Double<byte>(20); }
static T Id<T>(T x) { return x; }
static T Double<T>(T x) { return (T)(Id<T>(x) + Id<T>(x)); }";
        await Assert.That(RunA(src)).IsEqualTo((byte)40);
    }

    [Test]
    public async Task Generics_SameNameDifferentArity()
    {
        // Two generic methods share a name but differ in type-parameter count. They are distinct
        // templates keyed by (name, arity); an invocation's type-argument count selects the right one.
        const string src =
            @"
static byte Main() { return (byte)(Pick<byte>(5) + Pick<byte, ushort>(7, 9)); }
static T Pick<T>(T a) { return a; }
static T Pick<T, U>(T a, U b) { return a; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)12); // 5 + 7
    }

    [Test]
    public async Task Generics_OverloadedByValueArity_IsDiagnostic()
    {
        // Two generic methods with the same name AND type-parameter count would mangle to the same
        // specialized name; that is reported instead of silently mis-specializing to the first.
        const string src =
            @"
static byte Main() { return (byte)Max<byte>(1, 2); }
static T Max<T>(T a, T b) { return a; }
static T Max<T>(T a, T b, T c) { return a; }";
        await Assert.That(HasError(src)).IsTrue();
    }

    [Test]
    public async Task Generics_ShadowedTypeParameter_IsDiagnostic()
    {
        // Monomorphization substitutes type-parameter names by identifier text, so a local named like a
        // type parameter would be rewritten to the concrete type. That shadowing is reported.
        await Assert
            .That(
                HasError(
                    "static byte Main() { return (byte)Id<byte>(5); }\n"
                        + "static T Id<T>(T x) { byte T = 0; return (T)(x + T); }"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Generics_InStaticClassResolveSiblingsAndCrossClass()
    {
        // A generic method declared inside a static class: a qualified call (A.Id<byte>), a same-named
        // generic in another class (B.Id), and a sibling generic call inside a generic body (B.Twice
        // calls its own Id) must all resolve per declaring class. Regression: monomorphized instances
        // lost their class, so siblings failed to resolve and cross-class names collided.
        const string src =
            "static class M { static byte Main() { return (byte)(A.Id<byte>(5) + B.Twice<byte>(10)); } }\n"
            + "static class A { static T Id<T>(T x) { return x; } }\n"
            + "static class B { static T Twice<T>(T x) { return (T)(Id<T>(x) + Id<T>(x)); } "
            + "static T Id<T>(T x) { return x; } }";
        await Assert.That(RunA(src)).IsEqualTo((byte)25); // 5 + (10 + 10)
    }

    [Test]
    public async Task Linq_ReductionsOverArray()
    {
        const string data = "\nstatic readonly byte[] Data = { 3, 1, 4, 1, 5, 9, 2, 6 };";
        await Assert
            .That(RunA("static byte Main() { return (byte)Data.Sum(); }" + data))
            .IsEqualTo((byte)31);
        await Assert
            .That(RunA("static byte Main() { return (byte)Data.Where(x => x > 3).Sum(); }" + data))
            .IsEqualTo((byte)24); // 4+5+9+6
        await Assert
            .That(
                RunA(
                    "static byte Main() { return (byte)Data.Select(x => (byte)(x * 2)).Sum(); }"
                        + data
                )
            )
            .IsEqualTo((byte)62);
        await Assert
            .That(RunA("static byte Main() { return (byte)Data.Count(x => x > 3); }" + data))
            .IsEqualTo((byte)4);
        await Assert
            .That(RunA("static byte Main() { return Data.Max(); }" + data))
            .IsEqualTo((byte)9);
        await Assert
            .That(RunA("static byte Main() { return Data.Min(); }" + data))
            .IsEqualTo((byte)1);
    }

    [Test]
    public async Task Sum_AccumulatesWiderThanElement()
    {
        // A byte sum whose total exceeds 255 must not wrap at the element width — the accumulator widens
        // to int, matching C#. Regression: the accumulator was sized to the source element type.
        const string src =
            "static readonly byte[] D = { 200, 200, 200 };\n"
            + "static ushort Main() { return (ushort)D.Sum(); }";
        await Assert.That(RunHL(src)).IsEqualTo((ushort)600);
    }

    [Test]
    public async Task Const_UnsignedShiftIsLogical()
    {
        // A ulong constant with bit 63 set must shift/divide unsigned (logical), not arithmetically.
        const string src =
            "const ulong Mask = 0xFF00000000000000UL >> 8;\n"
            + "static ulong Main() { return Mask; }";
        await Assert.That(RunI64(src)).IsEqualTo(0x00FF000000000000UL);
    }

    [Test]
    public async Task Recursion_EntryFunctionIsItselfRecursive()
    {
        // The entry (main) recurses: the one-time stack setup must run only at boot, not on every
        // recursive re-entry (which would reset SP/SoftSp and destroy the return chain). A recursive
        // function returns via ReturnScratch, so read the result there.
        const string src =
            "static byte n;\n" + "static byte Main() { n++; if (n < 5) return Main(); return n; }";
        var gb = Load(Compile(src), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte((ushort)Sm83Backend.ReturnScratch)).IsEqualTo((byte)5);
    }

    [Test]
    public async Task ClassStaticField_IsDiagnostic()
    {
        // A static field inside a user class is stored by no collection pass; reject it rather than let
        // it silently vanish and surface as a misleading later error.
        await Assert
            .That(
                HasError(
                    "class C { byte v; static int s; byte M() { return v; } }\n"
                        + "static byte Main() { C c = new C(); return c.M(); }"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task InterruptAndMainBothWide_IsRejected()
    {
        // Wide (i32+) arithmetic routes through fixed runtime scratch shared by all functions; a handler
        // doing it can corrupt a main-line wide op it preempts, so the pair is rejected.
        const string wide =
            @"
static int g;
[Interrupt(""VBlank"")]
static void OnVBlank() { g = g * g; }
static int Main() { int x = 3; return x * x; }";
        await Assert.That(() => Compile(wide)).Throws<NotSupportedException>();

        // A handler doing only narrow (<=16-bit) work alongside wide main-line is fine.
        const string narrow =
            @"
static byte c;
[Interrupt(""VBlank"")]
static void OnVBlank() { c++; }
static int Main() { int x = 3; return x * x; }";
        await Assert.That(CompilesClean(narrow)).IsTrue();
    }

    [Test]
    public async Task Linq_MaxMinAfterPipeline_IsDiagnostic()
    {
        // Max/Min seed the accumulator with element 0 and skip the pipeline for it, so a filtered or
        // projected element 0 would corrupt the result — reject the combination rather than miscompile.
        const string data = "\nstatic readonly byte[] Data = { 3, 1, 4, 1, 5 };";
        await Assert
            .That(HasError("static byte Main() { return Data.Where(x => x > 3).Max(); }" + data))
            .IsTrue();
        await Assert
            .That(
                HasError(
                    "static byte Main() { return Data.Select(x => (byte)(x + 1)).Min(); }" + data
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Linq_AnyAllAndChain()
    {
        const string data = "\nstatic readonly byte[] Data = { 3, 1, 4, 1, 5, 9, 2, 6 };";
        await Assert
            .That(
                RunA("static byte Main() { return (byte)(Data.Any(x => x > 8) ? 1 : 0); }" + data)
            )
            .IsEqualTo((byte)1);
        await Assert
            .That(
                RunA("static byte Main() { return (byte)(Data.All(x => x > 0) ? 1 : 0); }" + data)
            )
            .IsEqualTo((byte)1);
        await Assert
            .That(
                RunA("static byte Main() { return (byte)(Data.All(x => x > 3) ? 1 : 0); }" + data)
            )
            .IsEqualTo((byte)0);
        // Where + Select + Sum chain: even values doubled, summed => (4+2+6)*2 = 24
        await Assert
            .That(
                RunA(
                    "static byte Main() { return (byte)Data.Where(x => (byte)(x % 2) == 0).Select(x => (byte)(x * 2)).Sum(); }"
                        + data
                )
            )
            .IsEqualTo((byte)24);
    }

    [Test]
    public async Task Coroutine_YieldReturnStateMachine()
    {
        // A `yield return` iterator becomes a cooperative-coroutine state machine (MoveNext advances
        // one step per call, suspending between yields). The caller drives it to sum 10+20+30 = 60.
        const string src =
            @"
static byte Main() {
    Gen__Iter g = Gen();
    byte sum = 0;
    while (g.MoveNext() != 0) { sum = (byte)(sum + g.Current()); }
    return sum;
}
static IEnumerable<byte> Gen() { yield return 10; yield return 20; yield return 30; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)60);
    }

    [Test]
    public async Task Coroutine_InsideStaticClass()
    {
        // An iterator declared inside a static class is lowered to a state machine like a top-level one.
        // Regression: TransformIterators scanned only the wrapper's direct members, so a static-class
        // `yield` was left unlowered and rejected as unsupported.
        const string src =
            @"
static class P {
    static byte Main() {
        Gen__Iter g = Seq.Gen();
        byte sum = 0;
        while (g.MoveNext() != 0) { sum = (byte)(sum + g.Current()); }
        return sum;
    }
}
static class Seq { static IEnumerable<byte> Gen() { yield return 10; yield return 20; yield return 30; } }";
        await Assert.That(RunA(src)).IsEqualTo((byte)60);
    }

    [Test]
    public async Task Coroutine_CountedForLoop()
    {
        // A single counted for-loop iterator lowers to a resumable state machine (state 0 runs the
        // initializer, later re-entries run the increment). Sum of i*i for i in 0..3 = 0+1+4+9 = 14.
        const string src =
            @"
static byte Main() {
    Sq__Iter g = Sq();
    byte sum = 0;
    while (g.MoveNext() != 0) { sum = (byte)(sum + g.Current()); }
    return sum;
}
static IEnumerable<byte> Sq() { for (byte i = 0; i < 4; i++) yield return (byte)(i * i); }";
        await Assert.That(RunA(src)).IsEqualTo((byte)14);
    }

    [Test]
    public async Task Coroutine_ParameterizedRange()
    {
        // An iterator that references its parameter: the argument is captured into the state object by
        // the factory, so the loop bound survives across MoveNext calls. Range(5) yields 0..4 = 10.
        const string src =
            @"
static byte Main() {
    Range__Iter g = Range(5);
    byte sum = 0;
    while (g.MoveNext() != 0) { sum = (byte)(sum + g.Current()); }
    return sum;
}
static IEnumerable<byte> Range(byte n) { for (byte i = 0; i < n; i++) yield return i; }";
        await Assert.That(RunA(src)).IsEqualTo((byte)10);
    }

    [Test]
    public async Task Coroutine_FlatYieldReferencesParameter()
    {
        // Straight-line yields that read the parameter also capture it. Two(7) yields 7 then 8 = 15.
        const string src =
            @"
static byte Main() {
    Two__Iter g = Two(7);
    byte sum = 0;
    while (g.MoveNext() != 0) { sum = (byte)(sum + g.Current()); }
    return sum;
}
static IEnumerable<byte> Two(byte a) { yield return a; yield return (byte)(a + 1); }";
        await Assert.That(RunA(src)).IsEqualTo((byte)15);
    }

    [Test]
    public async Task Coroutine_ReservedParameterName_IsNotMiscompiled()
    {
        // A parameter named like a synthesized state field (__state/__current/__it) would alias it and
        // corrupt iteration. Such an iterator is left untransformed and reported, not miscompiled.
        await Assert
            .That(
                HasError(
                    "static byte Main() { return 0; }\n"
                        + "static IEnumerable<byte> G(byte __state) { yield return __state; }"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Bcl_BitOperationsPopCount()
    {
        // The verbatim software fallback from System.Numerics.BitOperations.PopCount(uint) — real BCL
        // source, now compilable since 32-bit multiply/shift work.
        const string popcount =
            @"
static uint PopCount(uint value) {
    const uint c1 = 0x55555555u;
    const uint c2 = 0x33333333u;
    const uint c3 = 0x0F0F0F0Fu;
    const uint c4 = 0x01010101u;
    value -= (value >> 1) & c1;
    value = (value & c2) + ((value >> 2) & c2);
    value = (uint)((((value + (value >> 4)) & c3) * c4) >> 24);
    return value;
}";
        await Assert
            .That(RunI32("static uint Main() { return PopCount(0xFF); }" + popcount))
            .IsEqualTo(8u);
        await Assert
            .That(RunI32("static uint Main() { return PopCount(0xFFFFFFFF); }" + popcount))
            .IsEqualTo(32u);
        await Assert
            .That(RunI32("static uint Main() { return PopCount(0xDEADBEEF); }" + popcount))
            .IsEqualTo((uint)System.Numerics.BitOperations.PopCount(0xDEADBEEF));
    }

    [Test]
    public async Task Bcl_XoshiroRotateLeft()
    {
        // System.Random's xoshiro core relies on 64-bit rotate-left; verify it against the BCL.
        // inline, constant shifts
        await Assert
            .That(
                RunI64(
                    "static ulong Main() { ulong x = 0x0123456789ABCDEF; return (x << 40) | (x >> 24); }"
                )
            )
            .IsEqualTo(System.Numerics.BitOperations.RotateLeft(0x0123456789ABCDEFUL, 40));
        // inline, variable shift
        await Assert
            .That(
                RunI64(
                    "static ulong Main() { ulong x = 0x0123456789ABCDEF; int k = 40; return (x << k) | (x >> (64 - k)); }"
                )
            )
            .IsEqualTo(System.Numerics.BitOperations.RotateLeft(0x0123456789ABCDEFUL, 40));
        // i64 return captured from a call
        await Assert
            .That(
                RunI64(
                    "static ulong Main() { return Id(0x0123456789ABCDEF); }\nstatic ulong Id(ulong x) { return x; }"
                )
            )
            .IsEqualTo(0x0123456789ABCDEFUL);
        // computed i64 return from a call with an i64 + i32 arg (Main first: the harness boots at CodeBase)
        const string src =
            @"
static ulong Main() { return RotateLeft(0x0123456789ABCDEF, 40); }
static ulong RotateLeft(ulong x, int k) { return (x << k) | (x >> (64 - k)); }";
        await Assert
            .That(RunI64(src))
            .IsEqualTo(System.Numerics.BitOperations.RotateLeft(0x0123456789ABCDEFUL, 40));
    }

    [Test]
    public async Task Bcl_BitOperationsLog2()
    {
        // The de Bruijn software fallback from System.Numerics.BitOperations.Log2(uint), with the
        // exact BCL table and magic constant. MemoryMarshal/Unsafe become a plain static ROM array,
        // and the BCL's (nint) index cast becomes a (ushort) cast for the 16-bit address space; the
        // arithmetic (fold-right, 32-bit multiply, shift, table index) is verbatim BCL.
        const string log2 =
            @"
static readonly byte[] Log2DeBruijn = {
    0, 9, 1, 10, 13, 21, 2, 29, 11, 14, 16, 18, 22, 25, 3, 30,
    8, 12, 20, 28, 15, 17, 24, 7, 19, 27, 23, 6, 26, 5, 4, 31 };
static uint Log2(uint value) {
    value |= value >> 1;
    value |= value >> 2;
    value |= value >> 4;
    value |= value >> 8;
    value |= value >> 16;
    return Log2DeBruijn[(ushort)((value * 0x07C4ACDDu) >> 27)];
}";
        await Assert
            .That(RunI32("static uint Main() { return Log2(1); }" + log2))
            .IsEqualTo((uint)System.Numerics.BitOperations.Log2(1));
        await Assert
            .That(RunI32("static uint Main() { return Log2(0xFF); }" + log2))
            .IsEqualTo((uint)System.Numerics.BitOperations.Log2(0xFF));
        await Assert
            .That(RunI32("static uint Main() { return Log2(0x80000000u); }" + log2))
            .IsEqualTo((uint)System.Numerics.BitOperations.Log2(0x80000000u));
        await Assert
            .That(RunI32("static uint Main() { return Log2(0xDEADBEEFu); }" + log2))
            .IsEqualTo((uint)System.Numerics.BitOperations.Log2(0xDEADBEEF));
    }

    private static bool CompilesClean(string src)
    {
        var diagnostics = new DiagnosticBag();
        new Sm83Backend().Compile(
            new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics),
            diagnostics
        );
        return !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    // ---- Optimizer: behavior is preserved end-to-end on the emulator --------

    [Test]
    public async Task Optimized_FoldsConstantArithmeticToCorrectValue()
    {
        // 20*2 + 2 = 42, folded to a single constant, still returned correctly from the real ROM.
        await Assert
            .That(RunAOpt("static byte Main() { return (byte)(20 * 2 + 2); }"))
            .IsEqualTo((byte)42);
    }

    [Test]
    public async Task Optimized_WrapsFoldedConstantToWidth()
    {
        // 100+100+100 = 300; as a byte that is 44. Folding must wrap exactly like the backend would.
        await Assert
            .That(RunAOpt("static byte Main() { return (byte)(100 + 100 + 100); }"))
            .IsEqualTo((byte)44);
    }

    [Test]
    public async Task Optimized_PreservesRuntimeParameterComputation()
    {
        // Non-constant code the optimizer must leave alone: a + b with runtime inputs.
        const string src = "static byte Add(byte a, byte b) { return a + b; }";
        await Assert
            .That(
                RunAOpt(
                    src,
                    gb =>
                    {
                        W8(gb, 0, 40);
                        W8(gb, 1, 2);
                    }
                )
            )
            .IsEqualTo((byte)42);
    }

    [Test]
    public async Task Optimized_FoldsConstantComparisonDrivingABranch()
    {
        // The comparison folds to a constant; the branch must still select the right arm.
        await Assert
            .That(RunAOpt("static byte Main() { if (5 > 3) { return 7; } return 9; }"))
            .IsEqualTo((byte)7);
    }

    [Test]
    public async Task Optimized_RomIsSmallerWhenConstantsFold()
    {
        // The optimizer must actually fire on a real compile: a constant-heavy function shrinks.
        const string src = "static byte Main() { return (byte)(1 + 2 + 3 + 4 + 5 + 6 + 7 + 8); }";
        var unoptimized = Compile(src).Sections[0].Data.Length;
        var optimized = CompileOpt(src).Sections[0].Data.Length;
        await Assert.That(optimized).IsLessThan(unoptimized);
    }

    [Test]
    public async Task Optimized_ForwardsScalarLocalsThroughStoreLoad()
    {
        // A chain of scalar-local copies should forward to the parameter and still compute correctly.
        const string src =
            "static byte F(byte n) { byte x = n; byte y = x; return (byte)(y + y); }";
        await Assert.That(RunAOpt(src, gb => W8(gb, 0, 21))).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Optimized_ScalarLocalForwardingShrinksRom()
    {
        // Store->load forwarding + dead-store/DCE must remove the alloca traffic for scalar locals.
        const string src =
            "static byte F(byte n) { byte x = n; byte y = x; byte z = y; return (byte)(z + z + z); }";
        var unoptimized = Compile(src).Sections[0].Data.Length;
        var optimized = CompileOpt(src).Sections[0].Data.Length;
        await Assert.That(optimized).IsLessThan(unoptimized);
        await Assert.That(RunAOpt(src, gb => W8(gb, 0, 10))).IsEqualTo((byte)30);
    }

    [Test]
    public async Task Optimized_EliminatesDeadBranch()
    {
        // The condition folds to a constant, so simplify-cfg drops the dead arm; the result is 9.
        await Assert
            .That(RunAOpt("static byte Main() { if (2 > 5) { return 1; } return 9; }"))
            .IsEqualTo((byte)9);
    }
}
