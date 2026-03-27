using System.Diagnostics;
using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Emit;

namespace Koh.Compat.Tests;

/// <summary>
/// Helpers for RGBDS compatibility tests. Provides assembler API wrappers,
/// .o file writing, and rgblink subprocess invocation.
/// </summary>
internal static class RgbdsCompatFixture
{
    private static readonly Lazy<string?> RgblinkPathLazy = new(FindRgblink);

    public static bool IsAvailable => RgblinkPathLazy.Value != null;
    public static string? RgblinkPath => RgblinkPathLazy.Value;

    private static string? FindRgblink()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rgblink",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;

            // Drain both streams concurrently to avoid pipe-buffer deadlock
            var outTask = Task.Run(() => process.StandardOutput.ReadToEnd());
            var errTask = Task.Run(() => process.StandardError.ReadToEnd());
            Task.WaitAll(outTask, errTask);

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                return null;
            }

            return process.ExitCode == 0 ? "rgblink" : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    public static EmitModel Assemble(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    public static string WriteObjectFile(EmitModel model, string directory, string name)
    {
        var path = Path.Combine(directory, name);
        using var stream = File.Create(path);
        RgbdsObjectWriter.Write(stream, model);
        return path;
    }

    public static async Task<LinkResult> LinkAsync(string outputPath, params string[] objectFiles)
    {
        var rgblink = RgblinkPath ?? throw new InvalidOperationException("rgblink not available");

        var psi = new ProcessStartInfo
        {
            FileName = rgblink,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);
        foreach (var obj in objectFiles)
            psi.ArgumentList.Add(obj);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start rgblink");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Drain streams before awaiting process exit
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        byte[]? romData = null;
        if (process.ExitCode == 0 && File.Exists(outputPath))
            romData = await File.ReadAllBytesAsync(outputPath);

        return new LinkResult(process.ExitCode, stdout, stderr, romData);
    }
}

internal sealed record LinkResult(int ExitCode, string Stdout, string Stderr, byte[]? RomData);
