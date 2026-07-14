using Koh.Compiler;
using Koh.Compiler.Frontends;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Linker.Core;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Build.Tasks;

/// <summary>
/// MSBuild task that compiles a Koh game to a Game Boy ROM in-process, the same way the toolchain
/// does: pick a frontend (by name, default <c>csharp</c>) and a backend (by name) from
/// <see cref="CompilerRegistry"/>, run <see cref="CompilerDriver"/> to get an <c>EmitModel</c>,
/// then link it to a <c>.gb</c>. This is what the Koh SDK invokes after the ordinary build, so a
/// game project needs no build driver of its own and never references the compiler or linker
/// directly. The <c>csharp</c> frontend compiles <see cref="SourceFiles"/> as one translation unit
/// (the original path); a non-csharp frontend (e.g. <c>cil</c>) instead lowers the already-built
/// <see cref="AssemblyPath"/> plus <see cref="ReferencePaths"/> — no source files are read in that
/// case.
/// </summary>
public sealed class CompileKohRom : Microsoft.Build.Utilities.Task
{
    /// <summary>The game's C# source file(s), used by the <c>csharp</c> frontend. Compiled as a
    /// single translation unit.</summary>
    public ITaskItem[] SourceFiles { get; set; } = [];

    /// <summary>Absolute path of the ROM to write.</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>Frontend name registered in <see cref="CompilerRegistry"/> (default <c>csharp</c>).</summary>
    public string Frontend { get; set; } = "csharp";

    /// <summary>Backend name registered in <see cref="CompilerRegistry"/> (default <c>sm83</c>).</summary>
    public string Backend { get; set; } = "sm83";

    /// <summary>The game's already-built assembly, used by assembly-driven frontends (e.g. <c>cil</c>).</summary>
    public string AssemblyPath { get; set; } = "";

    /// <summary>Reference assembly paths for assembly-driven frontends (e.g. <c>cil</c>).</summary>
    public ITaskItem[] ReferencePaths { get; set; } = [];

    /// <summary>Linker program/module name.</summary>
    public string ProgramName { get; set; } = "rom";

    public bool CgbCompatible { get; set; }

    public override bool Execute()
    {
        bool isCSharp = string.Equals(Frontend, "csharp", StringComparison.OrdinalIgnoreCase);
        if (isCSharp && SourceFiles.Length == 0)
        {
            Log.LogError("Koh: no source files to compile.");
            return false;
        }
        if (!isCSharp && string.IsNullOrEmpty(AssemblyPath))
        {
            Log.LogError($"Koh: frontend '{Frontend}' requires AssemblyPath.");
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

        CompilerInput input;
        string primary;
        List<(string Path, int Start, SourceText Source)> files = [];
        if (isCSharp)
        {
            primary = SourceFiles[0].GetMetadata("FullPath");

            // A Koh program is one translation unit; concatenate multiple sources (the common case is
            // one). Track where each file starts in the joined text so a diagnostic's offset can be
            // mapped back to the file it actually came from, not just the first one.
            var joined = new System.Text.StringBuilder();
            foreach (var item in SourceFiles)
            {
                string path = item.GetMetadata("FullPath");
                string content = File.ReadAllText(path);
                files.Add((path, joined.Length, SourceText.From(content, path)));
                joined.Append(content).Append('\n');
            }
            var source = SourceText.From(joined.ToString(), primary);
            input = CompilerInput.FromSource(source);
        }
        else
        {
            primary = AssemblyPath;
            var referencePaths = ReferencePaths.Select(r => r.GetMetadata("FullPath")).ToList();
            input = CompilerInput.FromAssembly(AssemblyPath, referencePaths);
        }

        var diagnostics = new DiagnosticBag();
        EmitModel model = CompilerDriver.Compile(frontend, backend, input, diagnostics);

        bool hadError = false;
        foreach (var d in diagnostics)
        {
            var (file, line, col) = Locate(files, primary, d.Span.Start);
            if (d.Severity == DiagnosticSeverity.Error)
            {
                hadError = true;
                Log.LogError(null, null, null, file, line, col, line, col, d.Message);
            }
            else
            {
                Log.LogWarning(null, null, null, file, line, col, line, col, d.Message);
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
        return true;
    }

    /// <summary>Map an offset in the concatenated translation unit back to the originating file and its
    /// 1-based line/column, so a diagnostic in the Nth source file points at that file, not the first.</summary>
    private static (string File, int Line, int Column) Locate(
        List<(string Path, int Start, SourceText Source)> files,
        string fallback,
        int position
    )
    {
        if (position < 0 || files.Count == 0)
            return (fallback, 0, 0);
        int i = files.Count - 1;
        while (i > 0 && files[i].Start > position)
            i--;
        var (path, start, src) = files[i];
        int local = Math.Clamp(position - start, 0, Math.Max(0, src.Length - 1));
        int lineIndex = src.GetLineIndex(local);
        return (path, lineIndex + 1, local - src.Lines[lineIndex].Start + 1);
    }
}
