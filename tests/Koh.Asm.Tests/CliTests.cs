namespace Koh.Asm.Tests;

/// <summary>
/// End-to-end integration tests for the koh-asm CLI.
/// Each test writes temporary .asm source to a temp directory, invokes the real
/// koh-asm binary as a subprocess, and asserts on exit code, output files, and
/// diagnostic messages.
/// </summary>
public sealed class CliTests
{
    // ---------------------------------------------------------------------------
    // Help / usage
    // ---------------------------------------------------------------------------

    [Test]
    public async Task NoArgs_PrintsUsageToStderr_ExitsOne()
    {
        var result = await CliFixture.RunAsync([]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr).Contains("Usage:");
    }

    [Test]
    public async Task HelpFlag_PrintsUsageToStdout_ExitsZero()
    {
        var result = await CliFixture.RunAsync(["--help"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        // Help explicitly requested → stdout so `koh-asm --help | less` works.
        await Assert.That(result.Stdout).Contains("Usage:");
        await Assert.That(result.Stderr).IsEmpty();
    }

    [Test]
    public async Task ShortHelpFlag_PrintsUsageToStdout_ExitsZero()
    {
        var result = await CliFixture.RunAsync(["-h"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stdout).Contains("Usage:");
        await Assert.That(result.Stderr).IsEmpty();
    }

    [Test]
    public async Task UnknownFlag_ReportsError_ExitsOne()
    {
        var result = await CliFixture.RunAsync(["--frobnicate"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr).Contains("unknown option");
    }

    // ---------------------------------------------------------------------------
    // Input file validation
    // ---------------------------------------------------------------------------

    [Test]
    public async Task MissingInputFile_ReportsError_ExitsOne()
    {
        var result = await CliFixture.RunAsync(["/nonexistent/path/foo.asm"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr).Contains("file not found");
    }

    // ---------------------------------------------------------------------------
    // Successful assembly
    // ---------------------------------------------------------------------------

    [Test]
    public async Task ValidSource_DefaultOutputPath_ExitsZero_WritesKobj()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("test.asm", MinimalValidSource());
        var expectedKobj = Path.ChangeExtension(asmPath, ".kobj");

        var result = await CliFixture.RunAsync([asmPath]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(File.Exists(expectedKobj)).IsTrue();
        // Success message goes to stdout (not stderr).
        await Assert.That(result.Stdout).Contains("Assembled");
        await Assert.That(result.Stderr).IsEmpty();
    }

    [Test]
    public async Task ValidSource_ExplicitOutputPath_ExitsZero_WritesKobj()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("test.asm", MinimalValidSource());
        var kobjPath = tmp.FilePath("output.kobj");

        var result = await CliFixture.RunAsync([asmPath, "-o", kobjPath]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(File.Exists(kobjPath)).IsTrue();
    }

    [Test]
    public async Task ValidSource_LongOutputFlag_ExitsZero_WritesKobj()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("test.asm", MinimalValidSource());
        var kobjPath = tmp.FilePath("out.kobj");

        var result = await CliFixture.RunAsync([asmPath, "--output", kobjPath]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(File.Exists(kobjPath)).IsTrue();
    }

    [Test]
    public async Task ValidSource_ProducesNonEmptyKobj()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("test.asm", MinimalValidSource());
        var kobjPath = tmp.FilePath("test.kobj");

        await CliFixture.RunAsync([asmPath, "-o", kobjPath]);

        var info = new FileInfo(kobjPath);
        await Assert.That(info.Length).IsGreaterThan(0L);
    }

    [Test]
    public async Task SuccessMessage_AppearsOnStdout_NotStderr()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("test.asm", MinimalValidSource());

        var result = await CliFixture.RunAsync([asmPath]);

        await Assert.That(result.Stdout).Contains("Assembled");
        // stderr must not contain the success message — only diagnostics belong there.
        await Assert.That(result.Stderr).DoesNotContain("Assembled");
    }

    // ---------------------------------------------------------------------------
    // Error handling — assembly errors
    // ---------------------------------------------------------------------------

    [Test]
    public async Task InstructionOutsideSection_ReportsDiagnosticToStderr_ExitsOne()
    {
        using var tmp = new TempDirectory();
        // NOP without a SECTION directive is an assembly error.
        var asmPath = tmp.Write("bad.asm", "nop\n");

        var result = await CliFixture.RunAsync([asmPath]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        // Diagnostic must be on stderr in file:line:col format.
        await Assert.That(result.Stderr).Contains("bad.asm:");
        await Assert.That(result.Stderr).Contains("error:");
        // No output file should be created for a failed build.
        var kobjPath = Path.ChangeExtension(asmPath, ".kobj");
        await Assert.That(File.Exists(kobjPath)).IsFalse();
    }

    [Test]
    public async Task DiagnosticFormat_ContainsLineAndColumn()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("bad.asm", "nop\n");

        var result = await CliFixture.RunAsync([asmPath]);

        // Expected format: bad.asm:1:1: error: ...
        // The line and column numbers must be numeric.
        var stderrLines = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var diagLine = stderrLines.FirstOrDefault(l => l.Contains(": error:"));
        await Assert.That(diagLine).IsNotNull();

        // Verify structure: <file>:<line>:<col>: error: <message>
        // Split on ':' — the file path may contain ':' on Windows (drive letter),
        // so we look for a line matching the pattern after the filename.
        var fileName = Path.GetFileName(asmPath);
        await Assert.That(diagLine).Contains(fileName);
        await Assert.That(diagLine).Contains(": error:");
    }

    [Test]
    public async Task ErrorSource_DoesNotWriteKobj()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("bad.asm", "nop\n");
        var kobjPath = tmp.FilePath("bad.kobj");

        await CliFixture.RunAsync([asmPath, "-o", kobjPath]);

        await Assert.That(File.Exists(kobjPath)).IsFalse();
    }

    // ---------------------------------------------------------------------------
    // I/O error handling
    // ---------------------------------------------------------------------------

    [Test]
    public async Task OutputToNonExistentDirectory_ReportsIoError_ExitsOne()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("test.asm", MinimalValidSource());
        var badOutput = Path.Combine(tmp.Root, "nonexistent", "subdir", "out.kobj");

        var result = await CliFixture.RunAsync([asmPath, "-o", badOutput]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr).Contains("cannot write");
    }

    [Test]
    public async Task VersionFlag_PrintsVersionToStdout_ExitsZero()
    {
        var result = await CliFixture.RunAsync(["--version"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stdout).Contains("koh-asm");
        await Assert.That(result.Stderr).IsEmpty();
    }

    [Test]
    public async Task OnlyFlags_NoInput_ReportsError_ExitsOne()
    {
        using var tmp = new TempDirectory();
        var result = await CliFixture.RunAsync(["-o", Path.Combine(tmp.Root, "out.kobj")]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr).Contains("no input file specified");
    }

    [Test]
    public async Task MultipleInputFiles_ReportsError_ExitsOne()
    {
        using var tmp = new TempDirectory();
        var asm1 = tmp.Write("a.asm", MinimalValidSource());
        var asm2 = tmp.Write("b.asm", MinimalValidSource());

        var result = await CliFixture.RunAsync([asm1, asm2]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr).Contains("unexpected argument");
    }

    [Test]
    public async Task OutputFlagMissingValue_ReportsError_ExitsOne()
    {
        using var tmp = new TempDirectory();
        var asmPath = tmp.Write("test.asm", MinimalValidSource());

        var result = await CliFixture.RunAsync([asmPath, "-o"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stderr).Contains("requires an argument");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string MinimalValidSource() =>
        """
        SECTION "Test",ROM0
        start:
            nop
        """;
}

/// <summary>
/// Manages a temporary directory for a single test. Deleted on disposal.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "koh-asm-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        Directory.CreateDirectory(Root);
    }

    /// <summary>Writes <paramref name="content"/> to a file under the temp root and returns its full path.</summary>
    public string Write(string fileName, string content)
    {
        var fullPath = System.IO.Path.Combine(Root, fileName);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    /// <summary>Returns the full path of a file name under the temp root without creating it.</summary>
    public string FilePath(string fileName) => System.IO.Path.Combine(Root, fileName);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
