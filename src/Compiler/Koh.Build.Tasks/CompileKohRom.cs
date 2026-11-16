using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Build.Framework;

namespace Koh.Build.Tasks;

public sealed class CompileKohRom : Microsoft.Build.Utilities.Task, ICancelableTask
{
    [Required]
    public string OutputPath { get; set; } = "";

    [Required]
    public string ObjectPath { get; set; } = "";

    [Required]
    public string AssemblyPath { get; set; } = "";

    public string PdbPath { get; set; } = "";

    [Required]
    public string ProjectDirectory { get; set; } = "";

    [Required]
    public string CompilerPath { get; set; } = "";

    [Required]
    public string LinkerPath { get; set; } = "";

    public ITaskItem[] ReferencePaths { get; set; } = [];
    public bool CgbCompatible { get; set; }
    public bool CgbOnly { get; set; }
    public bool EmitDebugInfo { get; set; } = true;

    private volatile bool cancelled;
    private volatile Process? active;

    private static string Identity(string path, int links = 0)
    {
        if (links > 40)
            throw new IOException("Koh: too many symbolic links in an artifact path.");
        var current = Path.GetPathRoot(path)!;
        foreach (
            var part in path[current.Length..]
                .Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries
                )
        )
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (entry.LinkTarget is not null)
                current = Identity(
                    entry.ResolveLinkTarget(true)?.FullName
                        ?? throw new IOException($"Cannot resolve artifact path '{current}'."),
                    links + 1
                );
        }
        return current;
    }

    public void Cancel()
    {
        cancelled = true;
        try
        {
            active?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private bool Run(string executable, IEnumerable<string> arguments)
    {
        if (cancelled)
            return false;
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = ProjectDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        active = process;
        try
        {
            process.Start();
            if (cancelled)
                process.Kill(entireProcessTree: true);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = output.GetAwaiter().GetResult();
            var stderr = errors.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stdout))
                Log.LogMessage(MessageImportance.Normal, stdout);
            if (process.ExitCode != 0)
            {
                Log.LogError($"Koh: '{executable}' exited {process.ExitCode}: {stderr}");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(stderr))
                Log.LogMessage(MessageImportance.Normal, stderr);
            return !cancelled;
        }
        finally
        {
            active = null;
        }
    }

    public override bool Execute()
    {
        try
        {
            ProjectDirectory = Path.GetFullPath(ProjectDirectory);
            string Resolve(string path) => Path.GetFullPath(path, ProjectDirectory);
            var assembly = Resolve(AssemblyPath);
            var references = ReferencePaths.Select(item => Resolve(item.ItemSpec)).ToArray();
            var output = Resolve(OutputPath);
            var obj = Resolve(ObjectPath);
            var debug = Path.ChangeExtension(output, ".kdbg");
            var pdb = string.IsNullOrEmpty(PdbPath)
                ? Path.ChangeExtension(assembly, ".pdb")
                : Resolve(PdbPath);
            var inputs = references
                .Append(assembly)
                .Concat(
                    references
                        .Select(path => Path.ChangeExtension(path, ".pdb"))
                        .Append(pdb)
                        .Where(File.Exists)
                )
                .Select(path => Identity(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outputs = new[] { output, obj, debug };
            var identities = outputs.Select(path => Identity(path)).ToArray();
            if (
                identities.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length
                || identities.Any(inputs.Contains)
            )
            {
                Log.LogError(
                    "Koh: output paths must be distinct from each other and input assemblies."
                );
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(obj)!);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var arguments = new List<string> { assembly, "-o", obj };
            if (!EmitDebugInfo)
                arguments.Add("--no-pdb");
            else if (File.Exists(pdb))
                arguments.AddRange(["--pdb", pdb]);
            foreach (var reference in references)
                arguments.AddRange(["-r", reference]);
            if (!Run(CompilerPath, arguments))
                return false;
            arguments = ["-o", output];
            if (EmitDebugInfo)
                arguments.AddRange(["--kdbg", debug]);
            if (CgbOnly)
                arguments.Add("--cgb-only");
            else if (CgbCompatible)
                arguments.Add("--cgb-compatible");
            arguments.Add(obj);
            if (!Run(LinkerPath, arguments))
                return false;
            if (!EmitDebugInfo)
                File.Delete(debug);
            Log.LogMessage(
                MessageImportance.High,
                $"Koh: built {output} ({new FileInfo(output).Length} bytes)."
            );
            return true;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or Win32Exception
                        or ArgumentException
                        or InvalidOperationException
            )
        {
            Log.LogError($"Koh: {error.Message}");
            return false;
        }
    }
}
