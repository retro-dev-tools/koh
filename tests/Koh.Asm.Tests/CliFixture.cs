using System.Runtime.InteropServices;
using ReflectionAssembly = System.Reflection.Assembly;

namespace Koh.Asm.Tests;

/// <summary>
/// Locates the koh-asm executable and provides helpers for running it as a subprocess.
/// </summary>
internal static class CliFixture
{
    /// <summary>
    /// Absolute path to the koh-asm executable.
    /// When the test project references Koh.Asm via ProjectReference, MSBuild copies the
    /// Koh.Asm output (including its .exe) into the test output directory alongside the
    /// test assembly.  We resolve from there rather than from the source tree.
    /// </summary>
    public static readonly string ExecutablePath = ResolveExecutable();

    private static string ResolveExecutable()
    {
        var testAssemblyDir = Path.GetDirectoryName(
            ReflectionAssembly.GetExecutingAssembly().Location)!;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "koh-asm.exe"
            : "koh-asm";

        var candidate = Path.Combine(testAssemblyDir, exeName);

        if (!File.Exists(candidate))
            throw new FileNotFoundException(
                $"koh-asm executable not found at '{candidate}'. " +
                "Ensure the Koh.Asm project is built and its output is copied to the test output directory.",
                candidate);

        return candidate;
    }

    /// <summary>
    /// Runs koh-asm with the given arguments and returns the result.
    /// </summary>
    public static async Task<CliResult> RunAsync(string[] args, CancellationToken ct = default)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start koh-asm process.");

        // Read both pipes concurrently to avoid pipe-buffer deadlock
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new CliResult(process.ExitCode, stdout, stderr);
    }
}

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);
