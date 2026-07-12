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

/// <summary>Phase 4 of the semantic-model migration: <c>MethodLowerer</c>'s call/field/enum/struct
/// resolution sites (<c>LowerCall</c>'s static-callee switch, <c>LowerInstanceCall</c>,
/// <c>TryGlobal</c>/<c>TryModuleConst</c>, enum member access in <c>LowerMemberAccess</c>, and
/// <c>StructFieldPointer</c>/<c>StructBaseOf</c>'s field-name resolution) now consult a resolved Roslyn
/// symbol against <see cref="CSharpSemantics"/>'s symbol-keyed indexes first, falling back to the
/// pre-migration string-keyed lookup only when no symbol resolves (a detached monomorphized-generic
/// body, or no compilation). These tests exercise the real end-to-end pipeline (frontend -&gt; backend -&gt;
/// linker -&gt; emulator), mirroring <see cref="CSharpSemanticsIntrinsicsTests"/>'s harness, proving ordinary
/// programs still lower/run identically and that the symbol-first paths and their string fallbacks both
/// actually fire.</summary>
public class CSharpSemanticsResolutionTests
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

    private static byte RunThenRead(string src, int address)
    {
        var gb = Load(Compile(src), out int s, out int l);
        Run(gb, s, l);
        return gb.DebugReadByte((ushort)address);
    }

    private static DiagnosticBag LowerDiagnostics(string src)
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        return diagnostics;
    }

    private static bool HasError(string src) =>
        LowerDiagnostics(src).Any(d => d.Severity == DiagnosticSeverity.Error);

    // ---- Static calls: qualified, unqualified sibling, and cross-class name collisions --------------

    [Test]
    public async Task QualifiedStaticCall_ResolvesBySymbol_AndRuns()
    {
        // A qualified `Board.Get()` call: LowerCall's symbol-first path resolves the invocation's method
        // symbol, maps it via CSharpSemantics.Methods to the very same CsMethod the string-keyed switch
        // would have found.
        const string src =
            "static byte Main() { return Board.Get(); }\n"
            + "static class Board { public static byte Get() => 7; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
    }

    [Test]
    public async Task UnqualifiedSiblingCall_ResolvesBySymbol_AndRuns()
    {
        // A bare call from within the same static class resolves to its sibling (Board.Slide calling
        // Board.Helper unqualified) via the enclosing class, matching Roslyn's own member-lookup scoping.
        const string src =
            "static byte Main() { return Board.Slide(); }\n"
            + "static class Board { "
            + "public static byte Slide() => Helper(); "
            + "static byte Helper() => 11; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)11);
    }

    [Test]
    public async Task TwoClassesShareAMethodName_UnqualifiedCallStaysWithinItsOwnClass()
    {
        // A.Get and B.Get share a simple name; a bare call from within each class must resolve to its
        // own sibling, not the other class's same-named method. Symbol identity (not name text) decides.
        const string src =
            "static class M { static byte Main() { return (byte)(A.Get() + B.Get()); } }\n"
            + "static class A { public static byte Get() => Helper(); static byte Helper() => 3; }\n"
            + "static class B { public static byte Get() => Helper(); static byte Helper() => 40; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)43);
    }

    // ---- Generic call resolution: symbol confirms the template, mangled name selects the instance ----

    [Test]
    public async Task GenericCall_Monomorphized_ResolvesTemplateBySymbol_InstanceByMangledName()
    {
        // The call `Max<byte>(3, 7)` resolves (via Sym) to a constructed generic method symbol whose
        // OriginalDefinition is the `Max<T>` template — never registered in CSharpSemantics.Methods (only
        // monomorphized instances are, and those are detached), so this always falls through to the
        // syntax-based mangled name `Max__g1_4_byte`, exactly as before the symbol lookup was added.
        const string src =
            "static byte Main() { return (byte)Max<byte>(3, 7); }\n"
            + "static T Max<T>(T a, T b) { if (a > b) return a; return b; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);

        var module = Frontend(src);
        await Assert.That(module.Functions.Any(f => f.Name.StartsWith("Max__g"))).IsTrue();
    }

    [Test]
    public async Task GenericBody_CallingOtherUserFunctions_StringFallbackStillWorks()
    {
        // Touch<T>'s specialized body is a detached synthesized tree (TypeParamRewriter), so
        // CSharpSemantics.Sym returns null for every node inside it; a call from within that body to an
        // ordinary sibling function must still resolve via the pre-migration string-keyed `_methods`
        // table, not silently fail because there is no symbol to consult.
        const string src =
            "static byte Main() { return Touch<byte>(5); }\n"
            + "static T Touch<T>(T x) { return (T)Helper(x); }\n"
            + "static byte Helper(byte x) => (byte)(x + 1);";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)6);
    }

    // ---- Globals and module consts: read and write through symbol-first TryGlobal/TryModuleConst -----

    [Test]
    public async Task Global_ReadAndWrite_ResolveBySymbol()
    {
        const string src =
            "static byte Score;\n"
            + "static byte Main() { Score = 5; Score = (byte)(Score + 1); return Score; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)6);
    }

    [Test]
    public async Task Global_InsideStaticClass_PrefersItsOwnMemberOverATopLevelGlobal()
    {
        // Board.Cell and a top-level Cell coexist; from within Board's own method, the unqualified name
        // must resolve to Board's own field — exactly the preference TryGlobal's string fallback encodes,
        // which a resolved field symbol (identifying Board's own field) must agree with.
        const string src =
            "static byte Cell = 1;\n"
            + "static class Board { static byte Cell = 9; public static byte Get() { Cell = (byte)(Cell + 1); return Cell; } }\n"
            + "static byte Main() { return Board.Get(); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)10);
    }

    [Test]
    public async Task ModuleConst_Read_ResolvesBySymbol()
    {
        const string src = "const byte MaxScore = 42;\nstatic byte Main() { return MaxScore; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    // ---- Enum member access -----------------------------------------------------------------------

    [Test]
    public async Task EnumMember_ResolvesBySymbol_AndKeepsKohFoldedValue()
    {
        // Color's type symbol identifies the enum via CSharpSemantics.Enums, but the member's value
        // still comes from Koh's own folded CsEnum.Members table (ConstEval stays authoritative).
        const string src =
            "enum Color : byte { Red, Green = 5, Blue }\n"
            + "static byte Main() { return (byte)Color.Blue; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)6); // Green=5, Blue=6
    }

    [Test]
    public async Task EnumMember_UnknownMember_IsStillADiagnostic()
    {
        const string src =
            "enum Color : byte { Red, Green, Blue }\n"
            + "static byte Main() { return (byte)Color.Purple; }";
        await Assert.That(HasError(src)).IsTrue();
    }

    // ---- Struct field access through symbols -------------------------------------------------------

    [Test]
    public async Task StructField_ReadAndWrite_ResolveBySymbol()
    {
        const string src =
            "struct Point { public byte X; public byte Y; }\n"
            + "static byte Main() { Point p; p.X = 3; p.Y = 4; return (byte)(p.X + p.Y); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
    }

    [Test]
    public async Task NestedStructField_ResolvesBySymbol()
    {
        // `e.pos.x` recurses through StructBaseOf's nested-field case, which also resolves the field name
        // via symbol (confirmed against the already-established parent struct info).
        const string src =
            "struct Point { public byte X; public byte Y; }\n"
            + "struct Entity { public Point Pos; public byte Id; }\n"
            + "static byte Main() { Entity e; e.Pos.X = 9; return e.Pos.X; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)9);
    }

    [Test]
    public async Task ClassField_ReadAndWrite_ResolveBySymbol()
    {
        // A class instance's field access goes through the same StructFieldPointer/ResolvedFieldName path
        // as a struct (via StructBaseOf's ClassLocalOf branch).
        const string src =
            "class Counter { public byte Value; }\n"
            + "static byte Main() { Counter c = new Counter(); c.Value = 3; c.Value = (byte)(c.Value + 4); return c.Value; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
    }

    [Test]
    public async Task BareFieldInsideInstanceMethod_ResolvesAgainstThisBySymbol()
    {
        // An unqualified field reference inside an instance method resolves against `this` — the
        // WritePlace path's symbol-first field-name resolution.
        const string src =
            "class Counter { public byte Value; public byte Bump() { Value = (byte)(Value + 1); return Value; } }\n"
            + "static byte Main() { Counter c = new Counter(); c.Value = 5; return c.Bump(); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)6);
    }

    // ---- Instance method calls: receiver + callee resolve via symbol ------------------------------

    [Test]
    public async Task InstanceMethodCall_ResolvesCalleeBySymbol()
    {
        const string src =
            "class Counter { public byte Value; public byte Get() => Value; }\n"
            + "static byte Main() { Counter c = new Counter(); c.Value = 8; return c.Get(); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)8);
    }

    // ---- MathF still routes to the compiled softfloat library, not the real BCL MathF --------------

    private static uint RunI32(string src, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
        Run(gb, s, l);
        return ((uint)gb.Registers.DE << 16) | gb.Registers.HL;
    }

    [Test]
    public async Task MathF_BareAndSystemQualified_BothRouteToSoftfloatLibrary()
    {
        // Bare `MathF.Round` binds directly to Koh's own in-tree MathF class (an ordinary registered
        // method, found via the symbol/Methods lookup); `System.MathF.Round` binds to the real BCL
        // System.MathF symbol, which IsBclMathF must catch and reroute to the same compiled library —
        // both must produce the identical IEEE bit pattern the host's real MathF.Round would.
        await Assert
            .That(LowerDiagnostics("static float Main() { return MathF.Round(2.5f); }"))
            .IsEmpty();
        await Assert
            .That(RunI32("static float Main() { return MathF.Round(2.5f); }"))
            .IsEqualTo(BitConverter.SingleToUInt32Bits(MathF.Round(2.5f)));
        await Assert
            .That(LowerDiagnostics("static float Main() { return System.MathF.Round(2.5f); }"))
            .IsEmpty();
        await Assert
            .That(RunI32("static float Main() { return System.MathF.Round(2.5f); }"))
            .IsEqualTo(BitConverter.SingleToUInt32Bits(MathF.Round(2.5f)));
    }

    // ---- Regression guards from earlier passes still hold ------------------------------------------

    [Test]
    public async Task HardwareAndGbIntrinsics_StillResolve_AlongsideCallResolutionChanges()
    {
        const string src =
            "static void Main() { Hardware.BGP = 0xE4; byte* v = Gb.Vram; *(v + 2) = 0x11; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunThenRead(src, 0xFF47)).IsEqualTo((byte)0xE4);
        await Assert.That(RunThenRead(src, 0x8002)).IsEqualTo((byte)0x11);
    }

    [Test]
    public async Task DuplicateFunctionName_IsStillADiagnostic()
    {
        // Overload resolution stays out of scope: two same-named top-level functions is still reported,
        // and the call still resolves to the first definition (unchanged by the symbol-first conversion).
        const string src =
            "static byte Main() { return F(); }\n"
            + "static byte F() => 1;\n"
            + "static byte F() => 2;";
        await Assert.That(HasError(src)).IsTrue();
        await Assert.That(RunA(src)).IsEqualTo((byte)1);
    }
}
