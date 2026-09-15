using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Compiler.Targets;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Graphics-library build-plan SLICE 0 (see
/// <c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c> §8 decision 3): <c>ldstr</c>
/// lowers to one ASCII byte per character in an <see cref="AddressSpace.Rom"/> global — see
/// <c>CilMethodLowerer.Strings.cs</c>'s class remarks for the exact contract (<c>.Length</c>/indexer
/// usable within the same method that produced the string value). Follows <see cref="CilStaticsTests"/>'s
/// own harness shape (its own compile-to-assembly pipeline) rather than depending on it.
/// </summary>
public class CilStringLiteralTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk -----------------------------------

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (
                var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            )
            {
                try
                {
                    builder.Add(MetadataReference.CreateFromFile(path));
                }
                catch (IOException) { }
                catch (BadImageFormatException) { }
            }
        }
        builder.Add(
            MetadataReference.CreateFromFile(typeof(Koh.GameBoy.Hardware).Assembly.Location)
        );
        return builder.ToImmutable();
    });

    private static readonly string ScratchDir = Path.Combine(
        Path.GetTempPath(),
        "koh-cil-string-literal-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilStringLitAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true // the WriteAscii(byte*, string) fixture below needs a pointer parameter
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_strlit_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR, verified -------------------------------------------------------------

    private static IrModule Frontend(
        string source,
        OptimizationLevel level,
        DiagnosticBag diagnostics
    )
    {
        var assemblyPath = CompileToAssembly(source, level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        if (!diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
        {
            var errors = IrVerifier.Verify(module);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "IR verification failed:\n  " + string.Join("\n  ", errors)
                );
        }
        return module;
    }

    private static EmitModel Compile(IrModule module)
    {
        IrOptimizer.Optimize(module); // CompilerDriver's default path (Mem2RegPass does SSA construction).
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    private static (IrModule Module, EmitModel Model) FrontendAndCompile(
        string source,
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        var model = Compile(module);
        return (module, model);
    }

    // ---- Emulator harness (mirrors CilStaticsTests) --------------------------------------------

    private static GameBoySystem Load(
        EmitModel model,
        out int start,
        out int length,
        out LinkResult link
    )
    {
        link = new LinkerType().Link([new LinkerInput("cil", model)]);
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
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
    }

    // ---- Fixture: index a string literal byte-by-byte into a WRAM array, exactly the shape the ----
    // ---- graphics library's Text.Draw(col, row, "SCORE") loop needs (see class remarks). ----------

    private const string ScoreText = "SCORE";

    private const string StringIndexLoopSource = """
        using Koh.GameBoy;

        public static class Program
        {
            public static byte[] Dest = new byte[5];

            public static void Main()
            {
                string text = "SCORE";
                for (int i = 0; i < text.Length; i++)
                {
                    Dest[i] = (byte)text[i];
                }
                Hardware.BGP = Dest[0];
            }
        }
        """;

    private static (IrModule Module, byte[] DestBytes) RunStringIndexLoop(OptimizationLevel level)
    {
        var (module, model) = FrontendAndCompile(StringIndexLoopSource, level);
        var gb = Load(model, out int s, out int l, out var link);
        Run(gb, s, l);

        var destSymbol = link.Symbols.Single(sym => sym.Name.EndsWith(".Dest"));
        var bytes = new byte[ScoreText.Length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = gb.DebugReadByte((ushort)(destSymbol.AbsoluteAddress + 2 + i)); // +2: E4 length prefix — Dest is a WRAM array alias, its payload starts past the u16 count
        return (module, bytes);
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StringLiteral_IndexLoop_WritesExactAsciiBytesToWram(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(StringIndexLoopSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // The literal itself must live in a ROM global carrying its LENGTH-PREFIXED ASCII bytes — the
        // representation CilMethodLowerer.Strings.cs documents: a u16 length (little-endian) followed by
        // one byte per char, ASCII not UTF-16, not a heap allocation or a WRAM holder.
        var expectedAscii = ScoreText.Select(c => (byte)c).ToArray();
        var expectedBlob = new byte[] { (byte)expectedAscii.Length, 0 }
            .Concat(expectedAscii)
            .ToArray();
        var romGlobal = module.Globals.SingleOrDefault(g =>
            g.AddressSpace == AddressSpace.Rom
            && g.Initializer is not null
            && g.Initializer.AsSpan().SequenceEqual(expectedBlob)
        );
        await Assert.That(romGlobal).IsNotNull();

        var (_, destBytes) = RunStringIndexLoop(level);
        // The loop indexed the ROM'd literal byte-by-byte into WRAM: the observed bytes must be the
        // real ASCII codes for 'S','C','O','R','E', not a UTF-16 code unit or a garbage read.
        await Assert.That(destBytes).IsEquivalentTo(expectedAscii);

        var gb = Load(
            Compile(Frontend(StringIndexLoopSource, level, new DiagnosticBag())),
            out int s,
            out int l,
            out _
        );
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)'S');
    }

    [Test]
    public async Task StringLiteral_DebugAndReleaseProduceIdenticalObservableBytes()
    {
        var (_, debugBytes) = RunStringIndexLoop(OptimizationLevel.Debug);
        var (_, releaseBytes) = RunStringIndexLoop(OptimizationLevel.Release);
        await Assert.That(releaseBytes).IsEquivalentTo(debugBytes);
    }

    // ---- foreach (char c in text): per ECMA-334, foreach over 'string' is REQUIRED to lower to a --
    // ---- Length/indexer loop, never an enumerator — see CilMethodLowerer.Strings.cs's remarks. This --
    // ---- is exactly the Text-module shape ("foreach char, tile = base + (byte)ch"). -----------------

    private const string ForeachSource = """
        using Koh.GameBoy;

        public static class Program
        {
            public static byte[] Dest = new byte[5];

            public static void Main()
            {
                int i = 0;
                foreach (char c in "SCORE")
                {
                    Dest[i] = (byte)c;
                    i++;
                }
                Hardware.BGP = Dest[4];
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StringLiteral_ForeachLoop_WritesExactAsciiBytes(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(ForeachSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var model = Compile(module);
        var gb = Load(model, out int s, out int l, out var link);
        Run(gb, s, l);

        var destSymbol = link.Symbols.Single(sym => sym.Name.EndsWith(".Dest"));
        var bytes = new byte[ScoreText.Length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = gb.DebugReadByte((ushort)(destSymbol.AbsoluteAddress + 2 + i)); // +2: E4 length prefix — Dest is a WRAM array alias, its payload starts past the u16 count
        await Assert.That(bytes).IsEquivalentTo(ScoreText.Select(c => (byte)c).ToArray());
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)'E');
    }

    // ---- A non-ASCII character is a diagnostic, never a silent truncation/miscompile — Game Boy ----
    // ---- text has no representation above 0x7F (see CilMethodLowerer.Strings.cs's LowerLdstr). -----

    private const string NonAsciiSource = """
        using Koh.GameBoy;

        public static class Program
        {
            public static void Main()
            {
                string text = "café";
                Hardware.BGP = (byte)text[3];
            }
        }
        """;

    [Test]
    public async Task StringLiteral_NonAsciiCharacter_ReportsDiagnostic_DoesNotThrow()
    {
        var diagnostics = new DiagnosticBag();
        Frontend(NonAsciiSource, OptimizationLevel.Debug, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error)).IsTrue();
    }

    // ---- WAVE 2: a string value that crosses a real call boundary (received as a parameter) is now --
    // ---- fully supported — see CilMethodLowerer.Strings.cs's class remarks. A string is represented --
    // ---- as a pointer to a LENGTH-PREFIXED ROM blob, so '.Length'/the indexer are ordinary runtime ---
    // ---- memory reads off whatever pointer value the receiver holds, not a compile-time-provenance ---
    // ---- lookup — a parameter is just as valid a receiver as a same-method 'ldstr'. (Formerly this ---
    // ---- reported a diagnostic under wave 1's "no traceable provenance" representation.) -------------

    private const string StringLengthOnParameterSource = """
        using Koh.GameBoy;

        public static class Program
        {
            private static int Len(string s) => s.Length;

            public static void Main()
            {
                Hardware.BGP = (byte)Len("SCORE");
            }
        }
        """;

    [Test]
    public async Task StringLength_OnParameterAcrossCallBoundary_Compiles_ReturnsRealLength()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(StringLengthOnParameterSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var model = Compile(module);
        var gb = Load(model, out int s, out int l, out _);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)ScoreText.Length);
    }

    // ---- WAVE 2 SLICE (was the wave-1 KNOWN GAP, now closed): the graphics library's actual target --
    // ---- shape — a LIBRARY METHOD, Text.Draw(byte col, byte row, string text), that loops on --------
    // ---- '.Length'/the indexer INSIDE ITS OWN BODY, called from game code with a literal ("SCORE") — -
    // ---- now compiles and runs. See CilMethodLowerer.Strings.cs's class remarks / --------------------
    // ---- CilLoweringContext.EnsureStringLiteralGlobal for the length-prefixed-ROM-blob representation
    // ---- that makes this possible: the string parameter Draw receives is just an ordinary pointer to
    // ---- the same blob Main's 'ldstr' produced, so Draw's own '.Length'/'[i]' reads work identically
    // ---- to the same-method case above. This is exactly the RESOLVED DECISION 3 shape
    // ---- (docs/superpowers/specs/2026-07-15-graphics-library-design.md §8): 'Text.Draw(1, 0, "SCORE")'
    // ---- as a real library call.
    private const string TextDrawShapedSource = """
        using Koh.GameBoy;

        public static class Program
        {
            public static byte[] Dest = new byte[5];

            // Exactly the graphics-library design doc's Text.Draw(byte col, byte row, string text)
            // shape: a library method that walks its string PARAMETER byte-by-byte.
            private static void Draw(byte col, byte row, string text)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    Dest[i] = (byte)text[i];
                }
            }

            public static void Main()
            {
                Draw(1, 0, "SCORE");
                Hardware.BGP = Dest[0];
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task TextDrawShapedLibraryCall_StringParameterAcrossCallBoundary_CompilesAndRuns(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(TextDrawShapedSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var model = Compile(module);
        var gb = Load(model, out int s, out int l, out var link);
        Run(gb, s, l);

        var destSymbol = link.Symbols.Single(sym => sym.Name.EndsWith(".Dest"));
        var bytes = new byte[ScoreText.Length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = gb.DebugReadByte((ushort)(destSymbol.AbsoluteAddress + 2 + i)); // +2: E4 length prefix — Dest is a WRAM array alias, its payload starts past the u16 count
        await Assert.That(bytes).IsEquivalentTo(ScoreText.Select(c => (byte)c).ToArray());
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)'S');
    }

    // ---- Graphics-library slice 0 acceptance criterion: a helper shaped exactly like ----------------
    // ---- 'static void WriteAscii(byte* dst, string s) { for (int i=0;i<s.Length;i++) dst[i]=(byte)s[i]; }'
    // ---- called with a literal — the string PARAMETER flows across the call boundary, its '.Length'/--
    // ---- indexer are read at runtime, and the literal's blob lands in ROM. -----------------------------

    private const string WriteAsciiSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static byte[] Dest = new byte[5];

            private static unsafe void WriteAscii(byte* dst, string s)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    dst[i] = (byte)s[i];
                }
            }

            public static unsafe void Main()
            {
                byte* buf = stackalloc byte[5];
                WriteAscii(buf, "SCORE");
                for (int i = 0; i < 5; i++)
                {
                    Dest[i] = buf[i];
                }
                Hardware.BGP = Dest[0];
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task WriteAsciiHelper_StringParameter_LandsAsciiBytesAndRomBlob(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(WriteAsciiSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // The literal's ROM blob carries the u16 length prefix ahead of the ASCII bytes — see
        // CilLoweringContext.EnsureStringLiteralGlobal.
        var expectedAscii = ScoreText.Select(c => (byte)c).ToArray();
        var expectedBlob = new byte[] { (byte)expectedAscii.Length, 0 }
            .Concat(expectedAscii)
            .ToArray();
        var romGlobal = module.Globals.SingleOrDefault(g =>
            g.AddressSpace == AddressSpace.Rom
            && g.Initializer is not null
            && g.Initializer.AsSpan().SequenceEqual(expectedBlob)
        );
        await Assert.That(romGlobal).IsNotNull();

        var model = Compile(module);
        var gb = Load(model, out int s, out int l, out var link);
        Run(gb, s, l);

        var destSymbol = link.Symbols.Single(sym => sym.Name.EndsWith(".Dest"));
        var bytes = new byte[ScoreText.Length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = gb.DebugReadByte((ushort)(destSymbol.AbsoluteAddress + 2 + i)); // +2: E4 length prefix — Dest is a WRAM array alias, its payload starts past the u16 count
        await Assert.That(bytes).IsEquivalentTo(expectedAscii);
    }
}
