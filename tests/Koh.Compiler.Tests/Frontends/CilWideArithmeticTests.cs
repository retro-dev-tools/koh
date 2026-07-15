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
/// Regression coverage for <c>Sm83Backend.EmitMulWide</c>'s significant-byte early-exit (see its own
/// doc comment): a 32-bit-or-wider multiply whose right operand's high bytes are all zero now runs the
/// shift-and-add loop only over the bytes up to and including the highest nonzero one, instead of
/// always iterating the full width. That is a codegen-only change to the routine's ITERATION COUNT, not
/// its arithmetic, so every case below (including operands with every byte significant, operands that
/// are exactly zero, and negative operands whose two's-complement bit pattern has no zero high bytes at
/// all, so the early exit never fires) must still produce the exact same truncated 32-bit result as
/// plain C# <c>int</c>/<c>uint</c> multiplication. Mirrors <c>CilEndToEndTests</c>'s own compile-to-
/// assembly harness (Debug and Release IL of the same source must agree).
/// </summary>
public class CilWideArithmeticTests
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
        "koh-cil-wide-arith-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilWideArithAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_wide_{Guid.NewGuid():N}.dll");
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

    private static EmitModel Compile(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        IrOptimizer.Optimize(module);
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
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
        for (int steps = 0; steps < 400_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
    }

    private static int ReadI32(GameBoySystem gb, ushort addr) =>
        gb.DebugReadByte(addr)
        | (gb.DebugReadByte((ushort)(addr + 1)) << 8)
        | (gb.DebugReadByte((ushort)(addr + 2)) << 16)
        | (gb.DebugReadByte((ushort)(addr + 3)) << 24);

    // Left, right operand pairs spanning: zero on either side, both-zero, values whose bit pattern has
    // every byte significant (large magnitudes and negatives, which never take the early-exit shortcut),
    // values with exactly one significant byte (the common case in the gb-3d cube demo this fix targets),
    // a value whose ONLY significant byte is the exact top (4th) byte, and the standard int32 overflow
    // boundary (65536 * 65536 wraps mod 2^32).
    private static readonly (int Left, int Right)[] Cases =
    [
        (0, 0),
        (0, 12345),
        (12345, 0),
        (1, 1),
        (300000, 5),
        (5, 300000),
        (-5, 300000),
        (300000, -5),
        (-70000, -30000),
        (int.MaxValue, 2),
        (int.MinValue, 1),
        (int.MinValue, -1),
        (65536, 65536),
        (16777216, 3), // only the 4th (top) byte of the left operand is significant
        (3, 16777216),
        (-1, -1),
        (-1, 1),
    ];

    // The operands are read back out of arrays at a LOOP-CARRIED index (not inlined as literal
    // multiplies) specifically so the compiler cannot constant-fold the multiplications away — the
    // optimizer pipeline has no loop unrolling pass, so `lefts[i] * rights[i]` inside a `for` loop stays
    // a genuine runtime i32 Mul reaching Sm83Backend.EmitMulWide, exercising the actual routine this
    // fix changes rather than a compile-time-folded constant.
    private static string BuildSource()
    {
        var lefts = string.Join(", ", Cases.Select(c => c.Left));
        var rights = string.Join(", ", Cases.Select(c => c.Right));
        return $$"""
            using Koh.GameBoy;
            public class Program {
                static int[] lefts = { {{lefts}} };
                static int[] rights = { {{rights}} };
                public static unsafe void Main() {
                    for (int i = 0; i < {{Cases.Length}}; i++) {
                        int result = lefts[i] * rights[i];
                        *(int*)(0xC800 + i * 4) = result;
                    }
                }
            }
            """;
    }

    private static int[] RunAndReadAll(OptimizationLevel level)
    {
        var gb = Load(Compile(BuildSource(), level), out int s, out int l);
        Run(gb, s, l);
        var results = new int[Cases.Length];
        for (var i = 0; i < Cases.Length; i++)
            results[i] = ReadI32(gb, (ushort)(0xC800 + i * 4));
        return results;
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task WideMultiply_MatchesCSharpInt32Wraparound(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(BuildSource(), level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var actual = RunAndReadAll(level);
        for (var i = 0; i < Cases.Length; i++)
        {
            var (l, r) = Cases[i];
            var expected = unchecked(l * r);
            await Assert.That(actual[i]).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task WideMultiply_DebugAndReleaseProduceIdenticalResults()
    {
        var debugResults = RunAndReadAll(OptimizationLevel.Debug);
        var releaseResults = RunAndReadAll(OptimizationLevel.Release);
        await Assert.That(releaseResults).IsEquivalentTo(debugResults);
    }

    // ---- Wide signed divide: regression coverage for EmitUDivWide's dividend-leading-zero-byte skip --
    //
    // sdivmod_wide takes the absolute value of both operands before calling udivmod_wide, so every one
    // of these (including the negative-operand cases) exercises the new dividend scan/pre-shift path
    // whenever the operand's true magnitude fits in fewer than 4 significant bytes. int.MinValue / -1 is
    // deliberately excluded — C# division throws OverflowException for that one combination regardless
    // of checked/unchecked context, so it is not a case this fix (or the routine it touches) needs to
    // agree with C# about.
    private static readonly (int Left, int Right)[] DivCases =
    [
        (0, 5),
        (300000, 5),
        (300000, 7),
        (-300000, 5),
        (300000, -5),
        (-300000, -5),
        (5, 300000), // dividend smaller than divisor -> quotient 0
        (16777216, 3), // only the top byte of the dividend is significant
        (3, 16777216), // dividend tiny (1 significant byte), divisor huge
        (int.MaxValue, 7),
        (int.MinValue, 7),
        (int.MinValue, 2),
        (-1, 1),
        (1, -1),
        (-1, -1),
        (70000, 70000),
        (int.MaxValue, 1),
    ];

    private static string BuildDivSource()
    {
        var lefts = string.Join(", ", DivCases.Select(c => c.Left));
        var rights = string.Join(", ", DivCases.Select(c => c.Right));
        return $$"""
            using Koh.GameBoy;
            public class Program {
                static int[] lefts = { {{lefts}} };
                static int[] rights = { {{rights}} };
                public static unsafe void Main() {
                    for (int i = 0; i < {{DivCases.Length}}; i++) {
                        int result = lefts[i] / rights[i];
                        *(int*)(0xC800 + i * 4) = result;
                    }
                }
            }
            """;
    }

    private static int[] RunAndReadAllDiv(OptimizationLevel level)
    {
        var gb = Load(Compile(BuildDivSource(), level), out int s, out int l);
        Run(gb, s, l);
        var results = new int[DivCases.Length];
        for (var i = 0; i < DivCases.Length; i++)
            results[i] = ReadI32(gb, (ushort)(0xC800 + i * 4));
        return results;
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task WideDivide_MatchesCSharpInt32Semantics(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(BuildDivSource(), level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var actual = RunAndReadAllDiv(level);
        for (var i = 0; i < DivCases.Length; i++)
        {
            var (l, r) = DivCases[i];
            var expected = l / r;
            await Assert.That(actual[i]).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task WideDivide_DebugAndReleaseProduceIdenticalResults()
    {
        var debugResults = RunAndReadAllDiv(OptimizationLevel.Debug);
        var releaseResults = RunAndReadAllDiv(OptimizationLevel.Release);
        await Assert.That(releaseResults).IsEquivalentTo(debugResults);
    }

    // ---- Signed divide by a CONSTANT power of two: regression coverage for
    // StrengthReductionPass's bias-before-shift reduction (SDiv by a literal 2^k -> AShr/And/Add/AShr,
    // no runtime routine call at all). The dividend is read out of a loop-carried array (not inlined
    // as a literal) so the divide survives as a genuine IR SDiv reaching the pass, and the divisor is
    // a literal `128`/`2` so it matches the reduction's IrConstInt guard — unlike WideDivide's cases
    // above, whose divisor is itself a runtime array load and therefore never reduces (this is the
    // pass's only test coverage). Spans zero, positive/negative magnitudes on both sides of the
    // divisor's own bit width, and int.MinValue/int.MaxValue: a mis-signed bias (e.g. an LShr in place
    // of the sign-broadcasting AShr, or a missing/incorrect mask) shows up as a wrong quotient exactly
    // on the negative cases here, not the positive ones.
    private static readonly (int Left, int Right)[] ConstPow2DivCases =
    [
        (0, 128),
        (1, 128),
        (127, 128),
        (128, 128),
        (129, 128),
        (255, 128),
        (256, 128),
        (-1, 128),
        (-127, 128),
        (-128, 128),
        (-129, 128),
        (-256, 128),
        (int.MaxValue, 128),
        (int.MinValue, 128),
        (-1, 2),
        (1, 2),
        (-3, 2),
        (3, 2),
        (int.MinValue, 2),
    ];

    private static string BuildConstPow2DivSource()
    {
        var lefts = string.Join(", ", ConstPow2DivCases.Select(c => c.Left));
        var rights = string.Join(", ", ConstPow2DivCases.Select(c => c.Right));
        return $$"""
            using Koh.GameBoy;
            public class Program {
                static int[] lefts = { {{lefts}} };
                static int[] rights = { {{rights}} };
                public static unsafe void Main() {
                    for (int i = 0; i < {{ConstPow2DivCases.Length}}; i++) {
                        int result = rights[i] == 128 ? lefts[i] / 128 : lefts[i] / 2;
                        *(int*)(0xC800 + i * 4) = result;
                    }
                }
            }
            """;
    }

    private static int[] RunAndReadAllConstPow2Div(OptimizationLevel level)
    {
        var gb = Load(Compile(BuildConstPow2DivSource(), level), out int s, out int l);
        Run(gb, s, l);
        var results = new int[ConstPow2DivCases.Length];
        for (var i = 0; i < ConstPow2DivCases.Length; i++)
            results[i] = ReadI32(gb, (ushort)(0xC800 + i * 4));
        return results;
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ConstPow2Divide_MatchesCSharpInt32Semantics(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(BuildConstPow2DivSource(), level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var actual = RunAndReadAllConstPow2Div(level);
        for (var i = 0; i < ConstPow2DivCases.Length; i++)
        {
            var (l, r) = ConstPow2DivCases[i];
            var expected = r == 128 ? l / 128 : l / 2;
            await Assert.That(actual[i]).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task ConstPow2Divide_DebugAndReleaseProduceIdenticalResults()
    {
        var debugResults = RunAndReadAllConstPow2Div(OptimizationLevel.Debug);
        var releaseResults = RunAndReadAllConstPow2Div(OptimizationLevel.Release);
        await Assert.That(releaseResults).IsEquivalentTo(debugResults);
    }

    // ---- i32 shift by a CONSTANT amount: regression coverage for
    // Sm83Backend.ArithmeticEmitter.EmitWideShiftConst, the unrolled byte+bit fast path that
    // ConstPow2Divide's SDiv reduction above relies on for its shift-by-(width-1)=31 sign broadcast to
    // be cheap (see StrengthReductionPass's class remarks). Amounts span a pure byte shift (8, 16, 24),
    // a pure bit shift (1, 7), a byte+bit mix (9, 17, 25, and 31 — the exact amount the SDiv reduction
    // emits), and 0 (identity); operands span zero, small and large magnitudes on both sides of zero,
    // and int.Min/MaxValue. `<<` and `>>` (arithmetic, sign-extending) are tested directly; `>>>`
    // (logical, zero-filling) exercises EmitByteShift's/EmitBitShiftOnce's separate LShr fill-with-zero
    // path on the SAME negative operands `>>`'s sign-fill path uses, so a mixed-up fill byte (0x00 vs
    // the sign-extended 0xFF) shows up as a wrong high byte on exactly the negative cases.
    private static readonly int[] ShiftAmounts = [0, 1, 7, 8, 9, 15, 16, 17, 23, 24, 25, 31];
    private static readonly int[] ShiftOperands =
    [
        0,
        1,
        -1,
        5,
        -5,
        1000,
        -1000,
        int.MaxValue,
        int.MinValue,
        -123456,
        123456,
    ];

    private static string BuildShiftSource(string csharpOp)
    {
        var lefts = string.Join(", ", ShiftOperands);
        var n = ShiftOperands.Length;
        var body = new System.Text.StringBuilder();
        for (var a = 0; a < ShiftAmounts.Length; a++)
        {
            body.AppendLine(
                $"          for (int j = 0; j < {n}; j++) *(int*)(0xC800 + ({a} * {n} + j) * 4) = lefts[j] {csharpOp} {ShiftAmounts[a]};"
            );
        }
        return $$"""
            using Koh.GameBoy;
            public class Program {
                static int[] lefts = { {{lefts}} };
                public static unsafe void Main() {
            {{body}}
                }
            }
            """;
    }

    private static int[] RunAndReadAllShift(string csharpOp, OptimizationLevel level)
    {
        var gb = Load(Compile(BuildShiftSource(csharpOp), level), out int s, out int l);
        Run(gb, s, l);
        var total = ShiftAmounts.Length * ShiftOperands.Length;
        var results = new int[total];
        for (var i = 0; i < total; i++)
            results[i] = ReadI32(gb, (ushort)(0xC800 + i * 4));
        return results;
    }

    private static int[] ExpectedShift(string csharpOp)
    {
        var total = ShiftAmounts.Length * ShiftOperands.Length;
        var expected = new int[total];
        for (var a = 0; a < ShiftAmounts.Length; a++)
        for (var j = 0; j < ShiftOperands.Length; j++)
        {
            var l = ShiftOperands[j];
            var amount = ShiftAmounts[a];
            expected[a * ShiftOperands.Length + j] = csharpOp switch
            {
                "<<" => l << amount,
                ">>" => l >> amount,
                _ => l >>> amount,
            };
        }
        return expected;
    }

    [Test]
    [Arguments("<<", OptimizationLevel.Debug)]
    [Arguments("<<", OptimizationLevel.Release)]
    [Arguments(">>", OptimizationLevel.Debug)]
    [Arguments(">>", OptimizationLevel.Release)]
    [Arguments(">>>", OptimizationLevel.Debug)]
    [Arguments(">>>", OptimizationLevel.Release)]
    public async Task ConstShift_MatchesCSharpInt32Semantics(
        string csharpOp,
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(BuildShiftSource(csharpOp), level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var actual = RunAndReadAllShift(csharpOp, level);
        var expected = ExpectedShift(csharpOp);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("<<")]
    [Arguments(">>")]
    [Arguments(">>>")]
    public async Task ConstShift_DebugAndReleaseProduceIdenticalResults(string csharpOp)
    {
        var debugResults = RunAndReadAllShift(csharpOp, OptimizationLevel.Debug);
        var releaseResults = RunAndReadAllShift(csharpOp, OptimizationLevel.Release);
        await Assert.That(releaseResults).IsEquivalentTo(debugResults);
    }

    // ---- i64 (N=8) coverage ---------------------------------------------------------------------
    //
    // Every change above is keyed off RtN (the operand width in bytes), not a literal 4, so it applies
    // identically to i64 (N=8, `long`) and i128 (N=16) — this is coverage that an N=8 dividend/multiplier
    // actually needing a MIDDLE byte count (not just "1 byte" or "all 8") drives the scan, byte
    // pre-shift, and bit pre-shift through paths i32 (N=4, at most 4 significant bytes) cannot reach:
    // 5,000,000,000 needs exactly 5 of its 8 bytes.
    private static readonly (long Left, long Right)[] MulCases64 =
    [
        (0L, 12345L),
        (1L, 1L),
        (300000L, 5L),
        (5_000_000_000L, 3L), // exactly 5 of 8 bytes significant
        (3L, 5_000_000_000L),
        (-5_000_000_000L, 3L),
        (5_000_000_000L, -3L),
        (long.MaxValue, 2L),
        (long.MinValue, 1L),
        (long.MinValue, -1L),
        (-1L, -1L),
    ];

    private static readonly (long Left, long Right)[] DivCases64 =
    [
        (0L, 5L),
        (5_000_000_000L, 3L), // exactly 5 of 8 bytes significant
        (-5_000_000_000L, 3L),
        (5_000_000_000L, -3L),
        (3L, 5_000_000_000L), // dividend smaller than divisor -> quotient 0
        (long.MaxValue, 7L),
        (long.MinValue, 7L),
        (long.MinValue, 2L),
        (-1L, 1L),
        (1L, -1L),
        (-1L, -1L),
    ];

    private static long ReadI64(GameBoySystem gb, ushort addr)
    {
        long value = 0;
        for (var b = 7; b >= 0; b--)
            value = (value << 8) | gb.DebugReadByte((ushort)(addr + b));
        return value;
    }

    // long[] element loads lower to CIL's `ldelem.i8`, outside phase 1's opcode subset (see
    // CilLoweringTests's opcode coverage notes) — unlike the i32 test above, this can't index into a
    // `long[]`. A `switch` on the loop-carried index selects each case's literal operands instead: `i`
    // is still a genuine runtime (loop-carried, not compile-time-constant) value, so the compiler cannot
    // fold away which branch runs or the division/multiply inside it.
    private static string BuildSource64(string op, (long Left, long Right)[] cases)
    {
        var body = new System.Text.StringBuilder();
        for (var i = 0; i < cases.Length; i++)
            body.AppendLine(
                $"          case {i}: l = {cases[i].Left}L; r = {cases[i].Right}L; break;"
            );
        return $$"""
            using Koh.GameBoy;
            public class Program {
                public static unsafe void Main() {
                    for (int i = 0; i < {{cases.Length}}; i++) {
                        long l, r;
                        switch (i) {
            {{body}}
                            default: l = 0; r = 1; break;
                        }
                        long result = l {{op}} r;
                        *(long*)(0xC900 + i * 8) = result;
                    }
                }
            }
            """;
    }

    private static long[] RunAndReadAll64(
        string op,
        (long Left, long Right)[] cases,
        OptimizationLevel level
    )
    {
        var gb = Load(Compile(BuildSource64(op, cases), level), out int s, out int l);
        Run(gb, s, l);
        var results = new long[cases.Length];
        for (var i = 0; i < cases.Length; i++)
            results[i] = ReadI64(gb, (ushort)(0xC900 + i * 8));
        return results;
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task WideMultiply64_MatchesCSharpInt64Wraparound(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(BuildSource64("*", MulCases64), level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var actual = RunAndReadAll64("*", MulCases64, level);
        for (var i = 0; i < MulCases64.Length; i++)
        {
            var (l, r) = MulCases64[i];
            var expected = unchecked(l * r);
            await Assert.That(actual[i]).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task WideDivide64_MatchesCSharpInt64Semantics(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(BuildSource64("/", DivCases64), level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var actual = RunAndReadAll64("/", DivCases64, level);
        for (var i = 0; i < DivCases64.Length; i++)
        {
            var (l, r) = DivCases64[i];
            var expected = l / r;
            await Assert.That(actual[i]).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task WideMultiply64_DebugAndReleaseProduceIdenticalResults()
    {
        var debugResults = RunAndReadAll64("*", MulCases64, OptimizationLevel.Debug);
        var releaseResults = RunAndReadAll64("*", MulCases64, OptimizationLevel.Release);
        await Assert.That(releaseResults).IsEquivalentTo(debugResults);
    }

    [Test]
    public async Task WideDivide64_DebugAndReleaseProduceIdenticalResults()
    {
        var debugResults = RunAndReadAll64("/", DivCases64, OptimizationLevel.Debug);
        var releaseResults = RunAndReadAll64("/", DivCases64, OptimizationLevel.Release);
        await Assert.That(releaseResults).IsEquivalentTo(debugResults);
    }

    // ---- i64 shift by a CONSTANT amount: generalization coverage for EmitWideShiftConst at n=8 ----
    //
    // ConstShift above exercises EmitByteShift/EmitBitShiftOnce fully, but only at n=4; MulCases64's
    // only shift-shaped case (`long * 2`, reduced to Shl by 1) never drives byteShift > 0, and neither
    // right-shift fill path (AShr's sign-extend vs LShr's zero-fill) is exercised at all at n=8. This
    // targets exactly that gap: amounts spanning a pure byte shift (8, 24, 40), a byte+bit mix (20),
    // and the full width-1 sign broadcast (63), over negative and long.MinValue operands — where a
    // wrong fill byte or an off-by-one scratch index would show up as a wrong high byte.
    private static readonly int[] ShiftAmounts64 = [0, 1, 8, 20, 24, 40, 63];
    private static readonly long[] ShiftOperands64 =
    [
        0L,
        1L,
        -1L,
        1000L,
        -1000L,
        long.MaxValue,
        long.MinValue,
        -123456789012345L,
    ];

    private static string BuildShiftSource64(string csharpOp)
    {
        var n = ShiftOperands64.Length;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using Koh.GameBoy;");
        sb.AppendLine("public class Program {");
        sb.AppendLine("    public static unsafe void Main() {");
        for (var a = 0; a < ShiftAmounts64.Length; a++)
        {
            sb.AppendLine($"        for (int j = 0; j < {n}; j++) {{");
            sb.AppendLine("            long v;");
            sb.AppendLine("            switch (j) {");
            for (var j = 0; j < n; j++)
                sb.AppendLine($"                case {j}: v = {ShiftOperands64[j]}L; break;");
            sb.AppendLine("                default: v = 0L; break;");
            sb.AppendLine("            }");
            sb.AppendLine($"            long result = v {csharpOp} {ShiftAmounts64[a]};");
            sb.AppendLine($"            *(long*)(0xC900 + ({a} * {n} + j) * 8) = result;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static long[] RunAndReadAllShift64(string csharpOp, OptimizationLevel level)
    {
        var gb = Load(Compile(BuildShiftSource64(csharpOp), level), out int s, out int l);
        Run(gb, s, l);
        var total = ShiftAmounts64.Length * ShiftOperands64.Length;
        var results = new long[total];
        for (var i = 0; i < total; i++)
            results[i] = ReadI64(gb, (ushort)(0xC900 + i * 8));
        return results;
    }

    private static long[] ExpectedShift64(string csharpOp)
    {
        var total = ShiftAmounts64.Length * ShiftOperands64.Length;
        var expected = new long[total];
        for (var a = 0; a < ShiftAmounts64.Length; a++)
        for (var j = 0; j < ShiftOperands64.Length; j++)
        {
            var v = ShiftOperands64[j];
            var amount = ShiftAmounts64[a];
            expected[a * ShiftOperands64.Length + j] = csharpOp switch
            {
                "<<" => v << amount,
                ">>" => v >> amount,
                _ => v >>> amount,
            };
        }
        return expected;
    }

    [Test]
    [Arguments("<<", OptimizationLevel.Debug)]
    [Arguments("<<", OptimizationLevel.Release)]
    [Arguments(">>", OptimizationLevel.Debug)]
    [Arguments(">>", OptimizationLevel.Release)]
    [Arguments(">>>", OptimizationLevel.Debug)]
    [Arguments(">>>", OptimizationLevel.Release)]
    public async Task ConstShift64_MatchesCSharpInt64Semantics(
        string csharpOp,
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(BuildShiftSource64(csharpOp), level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var actual = RunAndReadAllShift64(csharpOp, level);
        var expected = ExpectedShift64(csharpOp);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("<<")]
    [Arguments(">>")]
    [Arguments(">>>")]
    public async Task ConstShift64_DebugAndReleaseProduceIdenticalResults(string csharpOp)
    {
        var debugResults = RunAndReadAllShift64(csharpOp, OptimizationLevel.Debug);
        var releaseResults = RunAndReadAllShift64(csharpOp, OptimizationLevel.Release);
        await Assert.That(releaseResults).IsEquivalentTo(debugResults);
    }
}
