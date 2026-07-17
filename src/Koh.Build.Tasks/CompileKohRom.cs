using Koh.Compiler;
using Koh.Compiler.Frontends;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Linker.Core;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Build.Tasks;

/// <summary>
/// MSBuild task that compiles a Koh game to a Game Boy ROM in-process, the same way the toolchain
/// does: pick a frontend (by name, default <c>cil</c>) and a backend (by name) from
/// <see cref="CompilerRegistry"/>, run <see cref="CompilerDriver"/> to get an <c>EmitModel</c>,
/// then link it to a <c>.gb</c>. This is what the Koh SDK invokes after the ordinary build, so a
/// game project needs no build driver of its own and never references the compiler or linker
/// directly. The frontend lowers the already-built <see cref="AssemblyPath"/> plus
/// <see cref="ReferencePaths"/> — no source files are read; <c>Koh.Compiler</c> has one frontend
/// (<c>cil</c>, over Mono.Cecil) since <c>csharp</c> (Roslyn-source-driven) was deleted once <c>cil</c>
/// reached parity.
/// </summary>
public sealed class CompileKohRom : Microsoft.Build.Utilities.Task
{
    /// <summary>Absolute path of the ROM to write.</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>Frontend name registered in <see cref="CompilerRegistry"/> (default <c>cil</c>).</summary>
    public string Frontend { get; set; } = "cil";

    /// <summary>Backend name registered in <see cref="CompilerRegistry"/> (default <c>sm83</c>).</summary>
    public string Backend { get; set; } = "sm83";

    /// <summary>The game's already-built assembly.</summary>
    [Required]
    public string AssemblyPath { get; set; } = "";

    /// <summary>Reference assembly paths (e.g. Koh.GameBoy.dll, for its Hal framework code).</summary>
    public ITaskItem[] ReferencePaths { get; set; } = [];

    /// <summary>Linker program/module name.</summary>
    public string ProgramName { get; set; } = "rom";

    public bool CgbCompatible { get; set; }

    /// <summary>
    /// When true (the default), also write a <c>.kdbg</c> debug-info file next to <see cref="OutputPath"/>
    /// (same base name, <c>.kdbg</c> extension) — mirrors <c>Koh.Link/Program.cs</c>'s own
    /// <c>DebugInfoBuilder</c>/<c>DebugInfoPopulator</c>/<c>KdbgFileWriter</c> call, so every SDK-built ROM
    /// gets the file the DAP debugger (<c>Koh.Debugger.Session.DebugInfoLoader</c>) already knows how to load.
    /// </summary>
    public bool EmitDebugInfo { get; set; } = true;

    public override bool Execute()
    {
        if (string.IsNullOrEmpty(AssemblyPath))
        {
            Log.LogError("Koh: no AssemblyPath to compile.");
            return false;
        }

        var frontend = CompilerRegistry.FrontendByName(Frontend);
        if (frontend is null)
        {
            Log.LogError($"Koh: no frontend named '{Frontend}'.");
            return false;
        }
        var backend = CompilerRegistry.BackendByName(Backend);
        if (backend is null)
        {
            Log.LogError($"Koh: no backend named '{Backend}'.");
            return false;
        }

        var referencePaths = ReferencePaths.Select(r => r.GetMetadata("FullPath")).ToList();
        CompilerInput input = CompilerInput.FromAssembly(AssemblyPath, referencePaths);

        var diagnostics = new DiagnosticBag();
        EmitModel model = CompilerDriver.Compile(frontend, backend, input, diagnostics);

        // The CIL frontend lowers a compiled assembly, not in-memory source text, so a diagnostic's
        // Span has no meaningful line/column here - report against the assembly path itself.
        bool hadError = false;
        foreach (var d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                hadError = true;
                Log.LogError(null, null, null, AssemblyPath, 0, 0, 0, 0, d.Message);
            }
            else
            {
                Log.LogWarning(null, null, null, AssemblyPath, 0, 0, 0, 0, d.Message);
            }
        }
        if (hadError)
            return false;

        var link = new LinkerType().Link(
            [new LinkerInput(ProgramName, model)],
            new LinkOptions(CgbCompatible)
        );
        var rom = link.RomData;
        if (rom is null)
        {
            Log.LogError("Koh: the linker produced no ROM.");
            return false;
        }

        // OutputPath is normally an absolute path, but a caller can override it to a directory-less
        // filename (GetDirectoryName then returns "" or null). Only create the directory when there is
        // one, otherwise write into the current directory.
        var outputDir = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(OutputPath, rom);
        Log.LogMessage(MessageImportance.High, $"Koh: built {OutputPath} ({rom.Length} bytes).");

        if (EmitDebugInfo)
        {
            var kdbgPath = Path.ChangeExtension(OutputPath, ".kdbg");
            var builder = new DebugInfoBuilder();
            DebugInfoPopulator.Populate(builder, link);
            using var kdbgStream = File.Create(kdbgPath);
            KdbgFileWriter.Write(kdbgStream, builder);
            Log.LogMessage(MessageImportance.Normal, $"Koh: wrote {kdbgPath}.");
        }

        return true;
    }
}
