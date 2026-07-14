using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests;

/// <summary><c>MethodLowerer</c>'s call/field/enum/struct resolution sites (<c>LowerCall</c>,
/// <c>LowerInstanceCall</c>, <c>TryGlobal</c>/<c>TryModuleConst</c>, enum member access in
/// <c>LowerMemberAccess</c>, and <c>StructFieldPointer</c>/<c>StructBaseOf</c>'s field-name resolution)
/// resolve a Roslyn symbol against <see cref="CSharpSemantics"/>'s symbol-keyed indexes — since Stage-2
/// P5 the ONLY resolution path (the pre-migration string-keyed lookups are deleted; a monomorphized
/// generic instance's body binds in the constructed instances tree, so symbols resolve there too). These
/// tests exercise the real end-to-end pipeline (frontend -&gt; backend -&gt; linker -&gt; emulator),
/// mirroring <see cref="CSharpSemanticsIntrinsicsTests"/>'s harness, proving ordinary programs
/// lower/run identically through the symbol-only paths.</summary>
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

    private static byte RunThenRead(string src, int address, Action<GameBoySystem>? args = null)
    {
        var gb = Load(Compile(src), out int s, out int l);
        args?.Invoke(gb);
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
        // A qualified `Board.Get()` call: LowerCall resolves the invocation's method symbol and maps it
        // via CSharpSemantics.Methods to the registered CsMethod — the only callee lookup since Stage-2 P5.
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
        // The call `Max<byte>(3, 7)` resolves (via SymOrCandidate) to a constructed generic method symbol
        // whose OriginalDefinition is the `Max<T>` template — never registered in CSharpSemantics.Methods
        // directly (only monomorphized instances are). Since Stage-2 P4, LowerCall routes such a call
        // through CSharpSemantics.GenericInstances: the template's OriginalDefinition symbol plus the
        // call's own mangled suffix (`__g1_4_byte`, from its `<byte>` syntax) selects the very same
        // instance the pre-P4 syntax-based mangled-name lookup would also have found — so behavior is
        // unchanged, but this call is now reached by symbol rather than only by text.
        const string src =
            "static byte Main() { return (byte)Max<byte>(3, 7); }\n"
            + "static T Max<T>(T a, T b) { if (a > b) return a; return b; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);

        var diagnostics = new DiagnosticBag();
        var (module, semantics) = CSharpFrontend.LowerForTest(src, diagnostics);
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(module.Functions.Any(f => f.Name.StartsWith("Max__g"))).IsTrue();

        // The instance really is reachable through the new symbol-keyed index: the template's own
        // declaration resolves to a symbol, and that symbol plus the call's own mangled suffix finds the
        // exact CsMethod whose IrFunction is the one just asserted to exist in the module.
        var mainRoot = semantics
            .Compilation!.SyntaxTrees.First(t =>
                t != IntrinsicsStub.Tree && t.FilePath != "__KohGenericInstances.cs"
            )
            .GetCompilationUnitRoot();
        var templateDecl = mainRoot
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Max");
        var templateSymbol = semantics.DeclaredSym(templateDecl);
        await Assert.That(templateSymbol).IsTypeOf<Microsoft.CodeAnalysis.IMethodSymbol>();

        var call = mainRoot
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is GenericNameSyntax { Identifier.Text: "Max" });
        var suffix = CSharpFrontend.MangleSuffix(
            ((GenericNameSyntax)call.Expression).TypeArgumentList.Arguments
        );
        await Assert.That(suffix).IsEqualTo("__g1_4_byte");

        var found = semantics.TryGetGenericInstance(
            (Microsoft.CodeAnalysis.IMethodSymbol)templateSymbol!,
            suffix,
            out var instance
        );
        await Assert.That(found).IsTrue();
        await Assert.That(instance.Fn.Name).IsEqualTo("Max__g1_4_byte");
    }

    [Test]
    public async Task GenericBody_CallingOtherUserFunctions_ResolvesBySymbol()
    {
        // Touch<byte>'s specialized body lives in the constructed instances tree (Stage-2 P2), a real
        // member of the compilation — so a call from within it to an ordinary sibling function resolves
        // by symbol exactly like any other call. Since Stage-2 P5 this is the ONLY way it can resolve
        // (the string-keyed fallback that used to cover the pre-P2 detached body is deleted), so this
        // test both runs the program end-to-end and pins the in-instance-body symbol resolution itself.
        const string src =
            "static byte Main() { return Touch<byte>(5); }\n"
            + "static T Touch<T>(T x) { return (T)Helper(x); }\n"
            + "static byte Helper(byte x) => (byte)(x + 1);";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)6);

        // The call node inside the monomorphized instance body resolves to the real Helper declaration
        // (SymOrCandidate — the same accessor LowerCall uses — yields Helper's own method symbol, and
        // that symbol is a key in the symbol-keyed Methods index the callee is looked up in).
        var diagnostics = new DiagnosticBag();
        var (_, semantics) = CSharpFrontend.LowerForTest(src, diagnostics);
        await Assert.That(diagnostics).IsEmpty();
        var instancesRoot = semantics
            .Compilation!.SyntaxTrees.First(t => t.FilePath == "__KohGenericInstances.cs")
            .GetCompilationUnitRoot();
        var helperCall = instancesRoot
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "Helper" });
        var resolved = semantics.SymOrCandidate(helperCall);
        await Assert.That(resolved).IsTypeOf<Microsoft.CodeAnalysis.IMethodSymbol>();
        var method = (Microsoft.CodeAnalysis.IMethodSymbol)resolved!;
        await Assert.That(method.Name).IsEqualTo("Helper");
        await Assert.That(semantics.Methods.ContainsKey(method.OriginalDefinition)).IsTrue();
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
        // must resolve to Board's own field — C#'s own scoping (the enclosing type's member wins), which
        // the resolved field symbol TryGlobal keys on encodes by construction.
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

    // ---- Instance-field-vs-top-level-global shadowing: the unflagged sibling of the disclosed --------
    // ---- instance-vs-static CALL collision (Stage-2 P5 commit message) --------------------------------

    [Test]
    public async Task InstanceField_ShadowsTopLevelGlobal_InsideInstanceMethod()
    {
        // C.x and the top-level `x` share a simple name. TryGlobal (WritePlace's read/write path) is
        // symbol-keyed since Stage-2 P5: CSharpSemantics.Globals holds only program-scope statics, so a
        // resolved field symbol identifying C's own instance field never matches an entry there — TryGlobal
        // misses, and WritePlace falls through to the this-relative field branch instead. Per C#'s own
        // scoping, the bare `x` inside an instance method always resolves to the enclosing type's own
        // member first, so this was always the intended (C#-correct) resolution — this test pins it: the
        // write lands in C's own instance field (through a this-relative GEP), not the top-level global.
        const string src =
            "static byte x = 1;\n"
            + "class C { public byte x; public void M() { x = 5; } public byte Get() => x; }\n"
            + "static byte Main() { C c = new C(); c.M(); return c.Get(); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)5);

        // Direct IR-shape assertion: the store inside C.M targets a GEP off `this`, never a GlobalRef —
        // the load-bearing evidence that the write actually went through the instance-field branch of
        // WritePlace rather than (somehow) still hitting the top-level global of the same name.
        var (module, _) = CSharpFrontend.LowerForTest(src, new DiagnosticBag());
        var mFn = module.FindFunction("C.M");
        await Assert.That(mFn).IsNotNull();
        var store = mFn!
            .Blocks.SelectMany(b => b.Instructions)
            .OfType<StoreInstruction>()
            .First(s => s.Value is IrConstInt { Value: 5 });
        await Assert.That(store.Pointer).IsTypeOf<GetElementPtrInstruction>();
    }

    [Test]
    public async Task InstanceField_ShadowsModuleConst_InsideInstanceMethod()
    {
        // Same shadowing as the global case, but for a module-level `const`: TryModuleConst is also
        // symbol-keyed (CSharpSemantics.ModuleConsts holds only program-scope consts), so a resolved field
        // symbol for C's own instance field never matches there either — the bare read inside the instance
        // method resolves to C's own field, not the top-level const of the same simple name.
        const string src =
            "const byte Max = 9;\n"
            + "class C { public byte Max; public byte Get() { Max = 3; return Max; } }\n"
            + "static byte Main() { C c = new C(); return c.Get(); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)3);
    }

    [Test]
    public async Task NonShadowedGlobal_StillResolvesFromInsideAnInstanceMethod()
    {
        // Guards the fix direction: when there is no same-named instance field to shadow it, a top-level
        // global read from inside an instance method still resolves via TryGlobal exactly as before —
        // shadowing is name-collision-specific, not a general regression in reading globals from `this`.
        const string src =
            "static byte Score = 7;\n"
            + "class C { public byte Id; public byte Get() => Score; }\n"
            + "static byte Main() { C c = new C(); return c.Get(); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
    }

    // ---- Instance-vs-static CALL collision: the change the Stage-2 P5 commit message flagged as --------
    // ---- latent and untested — pinned here. ------------------------------------------------------------

    [Test]
    public async Task BareCall_CollidesBetweenInstanceMethodAndSameNamedStatic_ResolvesToInstanceMethod()
    {
        // A genuine bare-name collision: the top-level (wrapper-scope) `Foo()` and `class C`'s own instance
        // method `Foo()` share a simple name, and C — nested inside the synthetic wrapper class — has the
        // wrapper as its enclosing outer scope, so both really are in a bare `Foo()` call's lookup path
        // from inside one of C's own instance methods. C# scoping picks the nearer (enclosing type's own)
        // member first: LowerCall's `symCallee is not { IsStatic: true }` check rules the top-level static
        // out whenever the call's own resolved symbol is C's instance method, exactly mirroring Roslyn's own
        // choice — C#'s own scoping, not the pre-migration static-first string gate this replaced (Stage-2
        // P5 commit message: "a bare call name colliding between an instance method of `this` and a
        // same-named static now follows C# scoping (instance wins) instead of the old static-first string
        // gate; no existing program or test exercises the collision"). Distinct return values prove which
        // Foo actually ran; a second, unrelated static (`S.Foo`) called by its qualified name from within C
        // confirms a static method is still reachable at all once explicitly qualified — the change is a
        // bare-name preference only, not a loss of access to statics in general.
        const string src =
            "static byte Foo() => 100;\n"
            + "static class S { public static byte Foo() => 77; }\n"
            + "class C { public byte Id; public byte Foo() => 11; public byte CallBare() => Foo(); public byte CallQualified() => S.Foo(); }\n"
            + "static byte Main() { C c = new C(); return (byte)(c.CallBare() + c.CallQualified()); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        // c.CallBare() must return 11 (C's own instance Foo wins the collision against the enclosing
        // wrapper's top-level Foo) and c.CallQualified() must still return 77 (S's static Foo, reached
        // explicitly): 11 + 77 = 88.
        await Assert.That(RunA(src)).IsEqualTo((byte)88);
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
        // The VRAM store's correctness (not its hardware-timing safety) is what this test is checking, so
        // the LCD is turned off before the program runs — real hardware (and this emulator, faithfully)
        // blocks VRAM writes during PPU mode 3, and boot leaves the LCD on; this program never waits for
        // a safe window before writing, so whether the write lands is otherwise a coincidence of how many
        // cycles the compiled code takes, which a compiler optimization can legitimately shift either way.
        await Assert
            .That(RunThenRead(src, 0x8002, gb => gb.DebugWriteByte(0xFF40, 0x00)))
            .IsEqualTo((byte)0x11);
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
