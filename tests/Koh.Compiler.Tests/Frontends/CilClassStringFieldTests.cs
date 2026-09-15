using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
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
/// Spike fixture for the ideal-game-API program (M0 of
/// <c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>): the north-star sample's
/// <c>EndScene</c> stores a <c>string</c> ctor parameter in a readonly class field and reads it back
/// later (<c>Text.Draw(…, _message)</c>). Strings are length-prefixed ROM blob pointers whose
/// <c>Length</c>/indexer work off ANY pointer (<c>CilMethodLowerer.Strings.cs</c>) — unlike arrays,
/// whose length dies at a call boundary — so a string surviving a ctor→field→method round trip
/// should already lower. This fixture pins that down as a regression test rather than an assumption.
/// Verdict crosses out through SCY, completion through SCX (the repo's register-verdict pattern —
/// never a literal WRAM scratch address, which would collide with the backend's static frames).
/// </summary>
public class CilClassStringFieldTests
{
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
        "koh-cil-class-string-field-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilClassStringFieldAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_strfield_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

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
        return new CilFrontend().Lower(input, diagnostics);
    }

    private static GameBoySystem Compile(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );
        IrOptimizer.Optimize(module);
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = 0x100;
        return gb;
    }

    private static void Run(GameBoySystem gb, int stepBudget = 200_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < 0x100 || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
    }

    // The north-star shape distilled: a string ctor parameter stored in a readonly field, read back
    // via Length and the indexer from another method, on two instances so the field really carries
    // per-instance state rather than accidentally folding to one literal.
    private const string StringFieldSource = """
        using Koh.GameBoy;

        public class Banner
        {
            private readonly string _text;
            private readonly ushort _score;

            public Banner(string text, ushort score)
            {
                _text = text;
                _score = score;
            }

            public byte TextLength() => (byte)_text.Length;

            public byte CharAt(byte i) => (byte)_text[i];

            public ushort Score() => _score;
        }

        public class Program
        {
            public static void Main()
            {
                var win = new Banner("YOU WIN!", 2048);
                var lose = new Banner("GAME OVER", 512);

                byte ok = 1;
                if (win.TextLength() != 8) ok = 0;
                if (lose.TextLength() != 9) ok = 0;
                if (win.CharAt(0) != (byte)'Y') ok = 0;
                if (win.CharAt(4) != (byte)'W') ok = 0;
                if (lose.CharAt(0) != (byte)'G') ok = 0;
                if (lose.CharAt(5) != (byte)'O') ok = 0;
                if (win.Score() != 2048) ok = 0;
                if (lose.Score() != 512) ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE; // completion marker
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StringCtorParameter_StoredInClassField_ReadBackViaLengthAndIndexer(
        OptimizationLevel level
    )
    {
        var gb = Compile(StringFieldSource, level);
        Run(gb);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE); // ran to completion
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1); // all checks passed
    }
}
