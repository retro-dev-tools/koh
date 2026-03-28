using System.Diagnostics;
using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Emit;

namespace Koh.Compat.Tests;

/// <summary>
/// Helpers for RGBDS compatibility tests. Provides assembler API wrappers,
/// .o file writing, and rgblink/rgbasm subprocess invocation.
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

            // Sequential drain is safe for tiny --version output
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (Exception) { }
                return null;
            }

            return process.ExitCode == 0 ? "rgblink" : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    public static EmitModel Assemble(string source, BinderOptions? options = null)
    {
        var tree = SyntaxTree.Parse(source);
        return options.HasValue
            ? Compilation.Create(options.Value, tree).Emit()
            : Compilation.Create(tree).Emit();
    }

    /// <summary>
    /// Assemble a source file with rgbasm to produce a .o file.
    /// Returns the path to the .o file, or null if rgbasm is unavailable or assembly failed.
    /// </summary>
    public static async Task<string?> RgbasmAssembleAsync(string source, string directory, string name)
    {
        var asmPath = Path.Combine(directory, name + ".asm");
        var objPath = Path.Combine(directory, name + ".o");
        await File.WriteAllTextAsync(asmPath, source);

        var psi = new ProcessStartInfo
        {
            FileName = "rgbasm",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(objPath);
        psi.ArgumentList.Add(asmPath);

        using var process = Process.Start(psi);
        if (process == null) return null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var results = await Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(cts.Token),
                process.StandardError.ReadToEndAsync(cts.Token));
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0 ? objPath : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (Exception) { }
            return null;
        }
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

        try
        {
            var results = await Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(cts.Token),
                process.StandardError.ReadToEndAsync(cts.Token));
            await process.WaitForExitAsync(cts.Token);

            var stdout = results[0];
            var stderr = results[1];

            byte[]? romData = null;
            if (process.ExitCode == 0 && File.Exists(outputPath))
                romData = await File.ReadAllBytesAsync(outputPath);

            return new LinkResult(process.ExitCode, stdout, stderr, romData);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (Exception) { }
            return new LinkResult(-1, "", "Process timed out", null);
        }
    }
}

internal sealed record LinkResult(int ExitCode, string Stdout, string Stderr, byte[]? RomData);
