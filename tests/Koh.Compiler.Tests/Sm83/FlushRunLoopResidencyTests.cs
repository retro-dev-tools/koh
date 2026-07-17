using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Backends.Sm83.Mir;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Core.Diagnostics;
using Koh.Debugger;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Sm83;

/// <summary>
/// Confirms the loop-induction-residency admission-widening fix (Phase 2 GEP-arm bitcast/global/wide-
/// gentle-binary/any-Value-ret admission — see <c>FunctionAllocation.IsLoopSafeInstruction</c>) actually
/// fires on the real, unmodified <c>Koh.GameBoy.Graphics.MapWriter.FlushRun</c> hot loop (the
/// <c>gb-gfx-demo</c> sample's own copy), not just on a hand-built synthetic shape. Compiles the real
/// sample source through the real pipeline (Roslyn -&gt; <see cref="CilFrontend"/> -&gt;
/// <see cref="IrOptimizer"/> -&gt; <see cref="Sm83Backend"/> -&gt; <see cref="LinkerType"/>), locates
/// <c>MapWriter.FlushRun</c> in the OPTIMIZED module, and checks both that
/// <see cref="FunctionAllocation.For"/> admits its <c>dst</c>/<c>src</c> pointer phis to <c>Hl</c>/<c>De</c>
/// and that the actual compiled bytes use the fused post-increment addressing mode in the loop body
/// instead of a per-iteration WRAM reload of either pointer.
/// </summary>
public class FlushRunLoopResidencyTests
{
    // ---- Harness: real C# compiled by Roslyn to a real assembly, lowered by CilFrontend, referencing
    // the real Koh.GameBoy.dll (mirrors GbGfxDemoTests/CilBgWinTests's own harness). ------------------

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static readonly string DemoSource = File.ReadAllText(
        Path.Combine(Root(), "samples", "gb-gfx-demo", "Game.cs")
    );

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
        "koh-flushrun-residency-tests"
    );

    private const string GlobalUsings = "global using Koh.GameBoy;\n";

    private static string CompileToAssembly(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(GlobalUsings + source);
        var compilation = CSharpCompilation.Create(
            "FlushRunResidencyAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"flushrun_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    private static CompilerInput InputFor(string source)
    {
        var assemblyPath = CompileToAssembly(source);
        return CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
    }

    /// <summary>Frontend -&gt; optimized IR, mirroring <c>CompilerDriver.Compile</c>'s own ordering, but
    /// returning the module itself (not just the backend's <see cref="Koh.Core.Binding.EmitModel"/>) so the
    /// test can find <c>MapWriter.FlushRun</c>'s <see cref="IrFunction"/> directly.</summary>
    private static IrModule OptimizedModule(string source)
    {
        var diagnostics = new DiagnosticBag();
        var module = new CilFrontend().Lower(InputFor(source), diagnostics);
        if (diagnostics.Any(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error))
            throw new InvalidOperationException(
                string.Join("; ", diagnostics.Select(d => d.Message))
            );
        IrOptimizer.Optimize(module);
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );
        return module;
    }

    private static IrFunction FindFlushRun(IrModule module) =>
        module.Functions.Single(f => f.Name.EndsWith("MapWriter.FlushRun"));

    // ---- Step 1: FunctionAllocation admits dst/src to Hl/De on the REAL compiled IR shape ------------

    [Test]
    public async Task FlushRun_DstAndSrcPointerPhisAreRegisterResident()
    {
        var module = OptimizedModule(DemoSource);
        var flushRun = FindFlushRun(module);

        var allocation = FunctionAllocation.For(
            flushRun,
            baseAddr: 0xC000,
            allowResidency: true,
            allowParamResidency: true
        );

        // The two loop-carried pointer phis (dst, src) — identified structurally (pointer-typed phis in
        // the function), not by variable name, since the CIL frontend/Mem2RegPass don't preserve source
        // names on a phi. FlushRun has exactly these two pointer-typed phis and no others.
        var pointerPhis = flushRun
            .Blocks.SelectMany(b => b.Instructions)
            .OfType<PhiInstruction>()
            .Where(p => p.Type.Kind == IrTypeKind.Pointer)
            .ToList();

        await Assert.That(pointerPhis.Count).IsEqualTo(2);

        foreach (var phi in pointerPhis)
            await Assert.That(allocation.Register.ContainsKey(phi)).IsTrue();

        var regs = pointerPhis.Select(p => allocation.Register[p]).ToHashSet();
        await Assert.That(regs.Count).IsEqualTo(2); // distinct registers - one Hl, one De
        await Assert.That(regs.Contains(Sm83Register.Hl)).IsTrue();
        await Assert.That(regs.Contains(Sm83Register.De)).IsTrue();
    }

    // ---- Step 2: the compiled bytes actually use the fused post-increment addressing mode, with no
    // per-iteration reload of either pointer through its own WRAM home. --------------------------------

    [Test]
    public async Task FlushRun_CompiledLoopBodyUsesFusedAddressingNotPerIterationReload()
    {
        var module = OptimizedModule(DemoSource);
        var flushRun = FindFlushRun(module);

        // Sanity check (mirrors step 1): residency must actually admit both pointers here, or the byte-
        // level assertions below would be checking nothing meaningful.
        var allocation = FunctionAllocation.For(
            flushRun,
            baseAddr: 0xC000,
            allowResidency: true,
            allowParamResidency: true
        );
        var pointerPhis = flushRun
            .Blocks.SelectMany(b => b.Instructions)
            .OfType<PhiInstruction>()
            .Where(p => p.Type.Kind == IrTypeKind.Pointer)
            .ToList();
        await Assert.That(pointerPhis.All(p => allocation.Register.ContainsKey(p))).IsTrue();

        var diagnostics = new DiagnosticBag();
        var model = new Sm83Backend().Compile(module, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error))
            .IsFalse();

        var link = new LinkerType().Link([new LinkerInput("flushrun", model)]);
        await Assert.That(link.Success).IsTrue();
        var rom = link.RomData!;

        var sym = link.Symbols.Single(s => s.Name.EndsWith("MapWriter.FlushRun"));
        int startAddr = (int)sym.AbsoluteAddress;
        int bank = sym.PlacedBank;

        byte ReadByte(int gbAddr)
        {
            int physOffset =
                bank == 0 || gbAddr < 0x4000 ? gbAddr : bank * 0x4000 + (gbAddr - 0x4000);
            return rom[physOffset];
        }

        // Disassemble forward from the function's entry until RET/RETI (0xC9/0xD9), collecting
        // (address, mnemonic) pairs. FlushRun is a single, self-contained function with no calls out, so
        // this cleanly captures its whole body including the loop.
        var decoded = new List<(int Addr, string Mnemonic)>();
        int addr = startAddr;
        for (int guard = 0; guard < 400; guard++) // generous bound; the real function is well under this
        {
            var (mnemonic, length) = Disassembler.DecodeOne(a => ReadByte(a), (ushort)addr);
            decoded.Add((addr, mnemonic));
            addr += length;
            if (mnemonic is "RET" or "RETI")
                break;
        }
        var mnemonics = decoded.Select(d => d.Mnemonic).ToList();

        // Evidence residency fired: the fused post-increment opcode (LD A,(HL+) = 0x2A, decoded as
        // "LD A,(HL+)") or the De pair's "LD (DE),A" + "INC DE" appears — this addressing mode is emitted
        // ONLY via FunctionAllocation's FusedPointerSite table (Layer 1 Phase 2 / Layer 2), never by the
        // ordinary (non-resident) Load/Store path, which uses "LD A,(HL)"/"LD (HL),A" plus a separate
        // "INC HL", or a per-iteration LoadPointerToHL reload instead.
        bool hasFusedHl = mnemonics.Any(m => m == "LD A,(HL+)" || m == "LD (HL+),A");
        bool hasFusedDe = mnemonics
            .Zip(mnemonics.Skip(1), (a, b) => (a, b))
            .Any(p => p.a == "LD (DE),A" && p.b == "INC DE");
        await Assert.That(hasFusedHl || hasFusedDe).IsTrue();

        // Scope the "no per-iteration reload" check to the LOOP BODY specifically, not the whole
        // function: the backend's own preheader sync (FunctionAllocation.LoopInductionPreheaderSync) is
        // legitimate one-time-per-call setup that loads each pointer's initial value into its register
        // via the exact same "LD A,($nnnn) ; LD L,A ; LD A,($nnnn) ; LD H,A" byte pattern as an ordinary
        // reload (Sm83Backend.EmitContext.LoadValueIntoRegister uses the same AToResidentOpcode encoding
        // LoadPointerToHL's Slot-reload path does) — so that pattern legitimately appears exactly once per
        // resident pointer, BEFORE the loop, and must not be mistaken for a per-iteration bug. All branch
        // opcodes this backend emits are absolute JP forms (Sm83Emitter.Jump always encodes 0xC2/0xC3/0xCA/
        // 0xD2/0xDA, never a relative JR), so the loop's back edge is identifiable as the JP-family
        // instruction whose target address is EARLIER than its own — the standard "backward branch = loop"
        // signature. Bytes from that target up to and including the back-edge jump itself are the loop
        // body, executed every iteration; bytes before it (the preheader) run once regardless of the
        // iteration count.
        int? loopBodyStart = null;
        int? loopBodyEnd = null;
        foreach (var (a, m) in decoded)
        {
            int dollar = m.IndexOf('$');
            if (dollar < 0 || !m.Contains("JP"))
                continue;
            if (
                !int.TryParse(
                    m[(dollar + 1)..],
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out int target
                )
            )
                continue;
            if (target < a && target >= startAddr)
            {
                loopBodyStart = target;
                loopBodyEnd = a;
            }
        }
        await Assert.That(loopBodyStart.HasValue).IsTrue(); // the loop must have a recognizable back edge

        var loopBody = decoded
            .Where(d => d.Addr >= loopBodyStart!.Value && d.Addr <= loopBodyEnd!.Value)
            .Select(d => d.Mnemonic)
            .ToList();

        // The fused opcode must itself be inside the loop body (not just somewhere in the function) —
        // the strong form of "residency fires for the hot loop specifically".
        bool loopHasFusedHl = loopBody.Any(m => m == "LD A,(HL+)" || m == "LD (HL+),A");
        bool loopHasFusedDe = loopBody
            .Zip(loopBody.Skip(1), (a, b) => (a, b))
            .Any(p => p.a == "LD (DE),A" && p.b == "INC DE");
        await Assert.That(loopHasFusedHl || loopHasFusedDe).IsTrue();

        // And the classic non-resident reload signature must NOT appear inside the loop body — it may
        // legitimately appear once before it (the preheader sync), but never as part of the repeating
        // per-iteration bytes.
        bool hasPointerReloadSequenceInLoop = false;
        for (int i = 0; i + 3 < loopBody.Count; i++)
        {
            if (
                loopBody[i].StartsWith("LD A,($")
                && loopBody[i + 1] == "LD L,A"
                && loopBody[i + 2].StartsWith("LD A,($")
                && loopBody[i + 3] == "LD H,A"
            )
            {
                hasPointerReloadSequenceInLoop = true;
                break;
            }
        }
        await Assert.That(hasPointerReloadSequenceInLoop).IsFalse();
    }
}
