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
/// M6 gap fixes surfaced by the JRPG north star: RANK-2 RECTANGULAR ARRAYS (Roslyn emits
/// <c>newobj T[0...,0...]::.ctor</c> plus instance <c>Get</c>/<c>Set</c> calls on the array type —
/// no CIL opcodes, no resolvable MethodDefinitions) and REFERENCE-ELEMENT arrays
/// (<c>ldelem.ref</c>/<c>stelem.ref</c> — the <c>string[]</c> dialogue table). A rank-2 array is
/// laid out <c>[u16 d0][u16 d1][row-major payload]</c> with the reference at the payload, so an
/// accessor on an UNTRACEABLE array (parameter, static field) reads its row stride from
/// payload−2 at runtime — the same convention as enabler E4's counted 1-D arrays.
/// </summary>
public class CilMdArrayTests
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
        "koh-cil-mdarray-tests"
    );

    private static (IrModule Module, GameBoySystem Gb) Compile(
        string source,
        OptimizationLevel level
    )
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilMdArrayAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_md_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );

        var diagnostics = new DiagnosticBag();
        var input = CompilerInput.FromAssembly(
            path,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
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
        var rom =
            link.RomData
            ?? throw new InvalidOperationException(
                "no ROM; linker diagnostics:\n  "
                    + string.Join("\n  ", link.Diagnostics.Select(d => d.Message))
            );
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = 0x100;
        return (module, gb);
    }

    private static void Run(GameBoySystem gb, int stepBudget = 2_000_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < 0x100 || pc >= 0x8000)
                return;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not finish within the step budget");
    }

    // ============================================================================================
    // Fixture 1: static readonly byte[,] — ROM-folded with the [d0][d1] header, read through BOTH
    // the aliased field and an untraceable parameter (the header path), plus writes to a runtime
    // 'new byte[h,w]' round-tripped back out.
    // ============================================================================================

    private const string RectSource = """
        using Koh.GameBoy;

        public class Program
        {
            static readonly byte[,] Map =
            {
                { 10, 11, 12, 13 },
                { 20, 21, 22, 23 },
                { 30, 31, 32, 33 },
            };

            static byte ReadCell(byte[,] m, int y, int x) => m[y, x]; // untraceable: header path

            public static void Main()
            {
                byte ok = 1;

                if (Map[0, 0] != 10) ok = 0;      // direct (aliased field)
                if (Map[2, 3] != 33) ok = 0;
                if (Map[1, 2] != 22) ok = 0;
                if (ReadCell(Map, 2, 1) != 31) ok = 0;   // through a parameter
                if (ReadCell(Map, 0, 3) != 13) ok = 0;

                byte[,] scratch = new byte[3, 5];        // runtime rank-2 allocation
                scratch[0, 0] = 1;
                scratch[2, 4] = 99;
                scratch[1, 3] = 55;
                if (scratch[0, 0] != 1) ok = 0;
                if (scratch[2, 4] != 99) ok = 0;
                if (ReadCell(scratch, 1, 3) != 55) ok = 0;
                if (scratch[0, 1] != 0) ok = 0;          // zero-filled

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RectangularArrays_RomFoldHeapCtorAndHeaderPath(OptimizationLevel level)
    {
        var (module, gb) = Compile(RectSource, level);

        // The ROM global is [u16 3][u16 4][12 row-major bytes].
        var mapGlobal = module.Globals.Single(g => g.Name.EndsWith(".Map"));
        var expected = new byte[] { 3, 0, 4, 0, 10, 11, 12, 13, 20, 21, 22, 23, 30, 31, 32, 33 };
        await Assert.That(mapGlobal.Initializer!).IsEquivalentTo(expected);

        Run(gb);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }

    // ============================================================================================
    // Fixture 2: string[] — reference elements. Static readonly table (unfolded cctor path:
    // heap array + stelem.ref stores at boot), elements read back via ldelem.ref through a
    // parameter, string semantics (Length + indexer) intact on the way out, and .Length of the
    // string[] itself via the E4 prefix.
    // ============================================================================================

    private const string StringArraySource = """
        using Koh.GameBoy;

        public class Program
        {
            static readonly string[] Lines = { "HELLO", "WORLD!", "GB" };

            static byte FirstChar(string[] lines, int i) => (byte)lines[i][0]; // ldelem.ref + indexer

            static int Count(string[] lines) => lines.Length; // E4 prefix through a parameter

            public static void Main()
            {
                byte ok = 1;

                if (Count(Lines) != 3) ok = 0;
                if (FirstChar(Lines, 0) != (byte)'H') ok = 0;
                if (FirstChar(Lines, 1) != (byte)'W') ok = 0;
                if (FirstChar(Lines, 2) != (byte)'G') ok = 0;
                if (Lines[1].Length != 6) ok = 0;      // string semantics survive the array trip

                string[] local = new string[2];        // stelem.ref into a runtime array
                local[0] = "AB";
                local[1] = "CDE";
                if (local[0].Length != 2) ok = 0;
                if (FirstChar(local, 1) != (byte)'C') ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StringArrays_ReferenceElementsAndLengths(OptimizationLevel level)
    {
        var (_, gb) = Compile(StringArraySource, level);
        Run(gb);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }
}
