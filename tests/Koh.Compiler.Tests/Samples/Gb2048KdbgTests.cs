using System.Collections.Immutable;
using System.Text;
using Koh.Compiler;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Debugger.Session;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// End-to-end proof that a <c>.kdbg</c> built the way <c>CompileKohRom</c> now builds one (the Koh
/// SDK task the graphics-library debug-tooling design's priority 1 targets) round-trips through the
/// same <c>Koh.Debugger.Session</c> stack the DAP debugger uses. Compiles the real, unmodified
/// <c>samples/gb-2048-cs</c> sources through the actual <c>cil</c> frontend -&gt; SM83 backend -&gt;
/// linker pipeline (the same pipeline <see cref="CilGame2048Tests"/> exercises, and that
/// <c>CompileKohRom</c> drives in-process), builds a <c>.kdbg</c> from the resulting
/// <see cref="LinkResult"/> exactly like <c>Koh.Link/Program.cs</c> and <c>CompileKohRom</c> both do
/// (<see cref="DebugInfoBuilder"/> + <see cref="DebugInfoPopulator"/> + <see cref="KdbgFileWriter"/>),
/// then loads those bytes back with <see cref="DebugInfoLoader"/> and confirms both halves resolve:
/// <see cref="SymbolMap"/> (address -&gt; function name) and <see cref="SourceMap"/> (address -&gt;
/// file/line, populated because the CIL frontend now reads the game assembly's portable PDB and
/// stamps <c>IrInstruction.Source</c> via sequence points — see <see cref="CilFrontend"/> and
/// <see cref="CilMethodLowerer"/>). Self-contained (compiles its own throwaway assembly rather than
/// reading the sample's own build output), so it does not depend on `dotnet build samples/gb-2048-cs`
/// having already run.
/// </summary>
public class Gb2048KdbgTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("could not locate the repository root (Koh.slnx).");
        return dir.FullName;
    }

    private static string SamplePath(string name) =>
        Path.Combine(RepoRoot(), "samples", "gb-2048-cs", name);

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
        "koh-cil-game2048-kdbg-tests"
    );

    // The Koh SDK brings Koh.GameBoy into scope everywhere via a global <Using> (Sdk.props); compiling
    // the sample files directly with Roslyn (no SDK) needs the same global using injected explicitly.
    private const string GlobalUsings = "global using Koh.GameBoy;\n";

    /// <summary>
    /// Compiles the real sample sources to a real assembly ON DISK with Roslyn, embedding a portable
    /// PDB (<c>DebugType.Portable</c>) — a plain <c>dotnet build</c> already does this by default, so
    /// this mirrors that rather than special-casing anything for the CIL frontend's benefit. Each tree
    /// is parsed with its real on-disk <paramref name="sourcePaths"/> entry as its <c>path</c> (a plain
    /// `dotnet build` does the same — the compiler invokes Roslyn with the real file paths on disk),
    /// so the emitted sequence points carry a real <c>Document.Url</c> — parsing from text alone (no
    /// path) leaves it empty, which <see cref="DebugInfoBuilder.InternSourceFile"/> then treats as "no
    /// file" (same sentinel as null) and <see cref="DebugInfoLoader"/> silently drops.
    /// </summary>
    private static string CompileToAssembly(IReadOnlyList<string> sourcePaths)
    {
        // A portable PDB needs each source's encoding recorded (CS8055), so read through
        // SourceText.From(..., Encoding.UTF8) rather than the bare-string ParseText overload.
        var trees = sourcePaths
            .Select(p =>
                CSharpSyntaxTree.ParseText(
                    Microsoft.CodeAnalysis.Text.SourceText.From(File.ReadAllText(p), Encoding.UTF8),
                    path: p
                )
            )
            .Append(CSharpSyntaxTree.ParseText(GlobalUsings))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "CilGame2048KdbgAsm_" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var dllPath = Path.Combine(ScratchDir, $"cil_2048_kdbg_{Guid.NewGuid():N}.dll");
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        EmitResult emitResult;
        using (var dllStream = File.Create(dllPath))
        using (var pdbStream = File.Create(pdbPath))
        {
            emitResult = compilation.Emit(
                dllStream,
                pdbStream,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)
            );
        }
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return dllPath;
    }

    [Test]
    public async Task Kdbg_ResolvesSymbolAndSourceLocation_ForCilFrontendBuild()
    {
        var assemblyPath = CompileToAssembly([
            SamplePath("Board.cs"),
            SamplePath("Tiles.cs"),
            SamplePath("Game.cs"),
        ]);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );

        // CompilerDriver.Compile is exactly what CompileKohRom itself calls: frontend -> IrOptimizer
        // (Mem2RegPass, default-on) -> backend. Compiling the CIL frontend's raw, un-mem2reg'd output
        // directly (skipping CompilerDriver) produces far larger code than the real SDK build does -
        // enough to overflow this ROM into multi-bank codegen, which is not what a real
        // `dotnet build samples/gb-2048-cs` produces (a single 32KB bank) and not what this test means
        // to exercise.
        var diagnostics = new DiagnosticBag();
        EmitModel model = CompilerDriver.Compile(
            new CilFrontend(),
            new Sm83Backend(),
            input,
            diagnostics
        );
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();

        var link = new LinkerType().Link([new LinkerInput("2048", model)]);
        await Assert.That(link.Success).IsTrue();

        // Mirrors Koh.Link/Program.cs:104-107 and CompileKohRom's own (now identical) call shape.
        var builder = new DebugInfoBuilder();
        DebugInfoPopulator.Populate(builder, link);
        using var kdbgStream = new MemoryStream();
        KdbgFileWriter.Write(kdbgStream, builder);

        var loader = new DebugInfoLoader();
        loader.Load(kdbgStream.ToArray());

        await Assert.That(loader.SymbolMap.All.Any()).IsTrue();

        // Both halves of "PC -> name + file:line" must resolve from the same .kdbg for at least one
        // real function symbol - the exact story section 5 of the debug-tooling design promises.
        KdbgParsedSymbol? resolvedSymbol = null;
        SourceLocation? resolvedLocation = null;
        foreach (var sym in loader.SymbolMap.All)
        {
            var location = loader.SourceMap.Lookup(new BankedAddress(sym.Bank, sym.Address));
            if (location is null)
                continue;
            resolvedSymbol = sym;
            resolvedLocation = location;
            break;
        }

        await Assert.That(resolvedSymbol).IsNotNull();
        await Assert.That(resolvedLocation).IsNotNull();

        Console.WriteLine(
            $"Resolved bank {resolvedSymbol!.Bank} 0x{resolvedSymbol.Address:X4} "
                + $"-> {resolvedSymbol.Name} @ {resolvedLocation!.File}:{resolvedLocation.Line}"
        );
    }

    /// <summary>
    /// An assembly built with no PDB at all (e.g. a Release build with <c>DebugType=none</c>) has no
    /// sequence points for <see cref="CilFrontend"/> to read. <see cref="CilFrontend.Lower"/>'s
    /// <c>ReadSymbols</c> attempt must fall back to a symbol-less read rather than let Cecil's
    /// <c>SymbolsNotFoundException</c> escape and abort the whole compile over optional debug info.
    /// </summary>
    [Test]
    public async Task Frontend_DoesNotThrow_WhenAssemblyHasNoPdb()
    {
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(
                """
                global using Koh.GameBoy;

                namespace Koh.Samples.NoPdb;

                static unsafe class Program
                {
                    static byte counter;

                    static void Main()
                    {
                        counter = (byte)(counter + 1);
                    }
                }
                """
            ),
        };
        var compilation = CSharpCompilation.Create(
            "CilNoPdbAsm_" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var dllPath = Path.Combine(ScratchDir, $"cil_nopdb_{Guid.NewGuid():N}.dll");
        EmitResult emitResult;
        using (var dllStream = File.Create(dllPath))
        {
            // No pdbStream, no EmitOptions - Roslyn emits with no debug information at all, exactly
            // like a Release build with <DebugType>none</DebugType>.
            emitResult = compilation.Emit(dllStream);
        }
        await Assert
            .That(emitResult.Success)
            .IsTrue()
            .Because(string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString())));

        var input = CompilerInput.FromAssembly(
            dllPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var diagnostics = new DiagnosticBag();
        var module = new CilFrontend().Lower(input, diagnostics);

        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(module.Functions.Any()).IsTrue();
    }
}
