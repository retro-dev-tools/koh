using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Build.Framework;

namespace Koh.Build.Tasks;

public sealed class GenerateKohTileSheets : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Sheets { get; set; } = [];

    [Required]
    public string OutputFile { get; set; } = "";

    [Required]
    public string Namespace { get; set; } = "";

    [Required]
    public string CompilerPath { get; set; } = "";

    public override bool Execute()
    {
        try
        {
            var start = new ProcessStartInfo(CompilerPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (
                var argument in new[]
                {
                    "--tile-sheets",
                    "--namespace",
                    Namespace,
                    "-o",
                    OutputFile,
                    "--",
                }
            )
                start.ArgumentList.Add(argument);
            foreach (var sheet in Sheets)
                start.ArgumentList.Add(sheet.GetMetadata("FullPath"));
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = output.GetAwaiter().GetResult();
            var stderr = errors.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                Log.LogError($"Koh: '{CompilerPath}' exited {process.ExitCode}: {stderr}");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(stdout))
                Log.LogMessage(MessageImportance.Normal, stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
                Log.LogMessage(MessageImportance.Normal, stderr);
            return true;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or Win32Exception
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            Log.LogError(ex.Message);
            return false;
        }
    }
}
