using Koh.Compiler;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Linker.Core;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Build.Tasks;

/// <summary>
/// MSBuild task that compiles a Koh C# game to a Game Boy ROM in-process, the same way the toolchain
/// does: pick a frontend by file extension and a backend by name from <see cref="CompilerRegistry"/>,
/// run <see cref="CompilerDriver"/> to get an <c>EmitModel</c>, then link it to a <c>.gb</c>. This is
/// what the Koh SDK invokes after the ordinary build, so a game project needs no build driver of its
/// own and never references the compiler or linker directly.
/// </summary>
public sealed class CompileKohRom : Microsoft.Build.Utilities.Task
{
    /// <summary>The game's C# source file(s). Compiled as a single translation unit.</summary>
    [Required]
    public ITaskItem[] SourceFiles { get; set; } = [];

    /// <summary>Absolute path of the ROM to write.</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>Backend name registered in <see cref="CompilerRegistry"/> (default <c>sm83</c>).</summary>
    public string Backend { get; set; } = "sm83";

    /// <summary>Linker program/module name.</summary>
    public string ProgramName { get; set; } = "rom";

    public override bool Execute()
    {
        if (SourceFiles.Length == 0)
        {
            Log.LogError("Koh: no source files to compile.");
            return false;
        }

        string primary = SourceFiles[0].GetMetadata("FullPath");
        var frontend = CompilerRegistry.FrontendForExtension(Path.GetExtension(primary));
        if (frontend is null)
        {
            Log.LogError($"Koh: no frontend registered for '{Path.GetExtension(primary)}'.");
            return false;
        }
        var backend = CompilerRegistry.BackendByName(Backend);
        if (backend is null)
        {
            Log.LogError($"Koh: no backend named '{Backend}'.");
            return false;
        }

        // A Koh program is one translation unit; concatenate multiple sources (the common case is one).
        // Track where each file starts in the joined text so a diagnostic's offset can be mapped back
        // to the file it actually came from, not just the first one.
        var files = new List<(string Path, int Start, SourceText Source)>();
        var joined = new System.Text.StringBuilder();
        foreach (var item in SourceFiles)
        {
            string path = item.GetMetadata("FullPath");
            string content = File.ReadAllText(path);
            files.Add((path, joined.Length, SourceText.From(content, path)));
            joined.Append(content).Append('\n');
        }
        var source = SourceText.From(joined.ToString(), primary);

        var diagnostics = new DiagnosticBag();
        EmitModel model = CompilerDriver.Compile(frontend, backend, source, diagnostics);

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

        var link = new LinkerType().Link([new LinkerInput(ProgramName, model)]);
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
