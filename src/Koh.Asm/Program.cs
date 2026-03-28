using System.Reflection;
using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Koh.Emit;

return KohAsm.Run(args);

static class KohAsm
{
    public static int Run(string[] args)
    {
        if (args.Contains("--version"))
            return ShowVersion();

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            return ShowUsage(exitCode: args.Length == 0 ? 1 : 0);

        var (inputPath, outputPath, format, error) = ParseArgs(args);
        if (error != null)
            return Fail(error);
        if (!File.Exists(inputPath!))
            return Fail($"file not found: {inputPath}");

        var input = inputPath!;
        var defaultExt = format == OutputFormat.Rgbds ? ".o" : ".kobj";
        outputPath ??= Path.ChangeExtension(input, defaultExt);

        var source = ReadSource(input);
        if (source == null)
            return 1; // error already reported

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var emitModel = Assemble(source, format);

        stopwatch.Stop();
        var elapsed = stopwatch.Elapsed;

        int errors = 0, warnings = 0;
        foreach (var diag in emitModel.Diagnostics)
        {
            var (line, col) = GetLocation(diag, source);
            var severity = diag.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info",
            };
            Console.Error.WriteLine($"{source.FilePath}:{line}:{col}: {severity}: {diag.Message}");
            if (diag.Severity == DiagnosticSeverity.Error) errors++;
            else if (diag.Severity == DiagnosticSeverity.Warning) warnings++;
        }

        if (errors > 0)
        {
            PrintSummary(input, elapsed, errors, warnings, null);
            return 1;
        }

        var result = WriteOutput(emitModel, outputPath, input, format);
        if (result == 0)
            PrintSummary(input, elapsed, errors, warnings, outputPath);
        return result;
    }

    static void PrintSummary(string input, TimeSpan elapsed, int errors, int warnings, string? outputPath)
    {
        var inputName = Path.GetFileName(input);
        var timeStr = elapsed.TotalSeconds < 1
            ? $"{elapsed.TotalMilliseconds:F0}ms"
            : $"{elapsed.TotalSeconds:F2}s";

        if (errors > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  {inputName}: {errors} error(s), {warnings} warning(s) [{timeStr}]");
        }
        else
        {
            var outName = outputPath != null ? Path.GetFileName(outputPath) : "?";
            Console.WriteLine($"  {inputName} -> {outName}  [{timeStr}]");
            if (warnings > 0)
                Console.Error.WriteLine($"  {warnings} warning(s)");
        }
    }

    enum OutputFormat { Kobj, Rgbds }

    // -------------------------------------------------------------------------
    // Pipeline stages
    // -------------------------------------------------------------------------

    static SourceText? ReadSource(string path)
    {
        try
        {
            var absolutePath = Path.GetFullPath(path);
            return SourceText.From(File.ReadAllText(absolutePath), absolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"cannot read '{path}': {ex.Message}");
            return null;
        }
    }

    static EmitModel Assemble(SourceText source, OutputFormat format)
    {
        var tree = SyntaxTree.Parse(source);
        var options = new Koh.Core.Binding.BinderOptions
        {
            AllowUndefinedSymbols = format == OutputFormat.Rgbds,
        };
        return Compilation.Create(options, tree).Emit();
    }

    // Diagnostics are now reported inline in Run()

    static int WriteOutput(EmitModel model, string outputPath, string inputPath, OutputFormat format)
    {
        var tempPath = outputPath + "." + Path.GetRandomFileName();
        try
        {
            using (var stream = File.Create(tempPath))
            {
                if (format == OutputFormat.Rgbds)
                    RgbdsObjectWriter.Write(stream, model);
                else
                    KobjWriter.Write(stream, model);
            }
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"cannot write '{outputPath}': {ex.Message}");
        }
        finally
        {
            // Clean up temp file regardless of exception type.
            // No-op on success path — File.Move already renamed it away.
            if (File.Exists(tempPath))
                try
                {
                    File.Delete(tempPath);
                }
                catch { }
        }

        // Success message printed by PrintSummary in Run()
        return 0;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    static (string? input, string? output, OutputFormat format, string? error) ParseArgs(string[] args)
    {
        string? input = null, output = null;
        var format = OutputFormat.Kobj;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-o" or "--output")
            {
                if (i + 1 >= args.Length)
                    return (null, null, format, $"option '{args[i]}' requires an argument");
                output = args[++i];
            }
            else if (args[i] is "--format" or "-f")
            {
                if (i + 1 >= args.Length)
                    return (null, null, format, $"option '{args[i]}' requires an argument");
                var val = args[++i].ToLowerInvariant();
                OutputFormat? parsed = val switch
                {
                    "kobj" => OutputFormat.Kobj,
                    "rgbds" or "o" => OutputFormat.Rgbds,
                    _ => null,
                };
                if (parsed is null)
                    return (null, null, format, $"unknown format '{val}' (expected: kobj, rgbds, o)");
                format = parsed.Value;
            }
            else if (!args[i].StartsWith('-'))
            {
                if (input != null)
                    return (null, null, format, $"unexpected argument '{args[i]}'");
                input = args[i];
            }
            else
            {
                return (null, null, format, $"unknown option '{args[i]}' (try --help)");
            }
        }
        return input == null ? (null, null, format, "no input file specified") : (input, output, format, null);
    }

    static (int line, int col) GetLocation(Diagnostic diag, SourceText source)
    {
        if (diag.Span == default)
            return (1, 1);
        var lineIdx = source.GetLineIndex(diag.Span.Start);
        return (lineIdx + 1, diag.Span.Start - source.Lines[lineIdx].Start + 1);
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine($"koh-asm: {message}");
        return 1;
    }

    static int ShowVersion()
    {
        var ver = typeof(KohAsm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        // Strip git commit hash suffix (e.g. "1.0.0+abc123" → "1.0.0")
        var display = ver.Contains('+') ? ver[..ver.IndexOf('+')] : ver;
        Console.WriteLine($"koh-asm {display}");
        return 0;
    }

    static int ShowUsage(int exitCode)
    {
        // Help requested explicitly → stdout so `koh-asm --help | less` works.
        // Triggered by missing args → stderr (it is an error condition).
        var output = exitCode == 0 ? Console.Out : Console.Error;
        output.WriteLine(
            """
            Usage: koh-asm <input.asm> [-o output] [--format kobj|rgbds]

            Options:
              -o, --output <path>    Output file path (default: input.kobj or input.o)
              -f, --format <format>  Output format: kobj (default), rgbds or o (.o for rgblink)
                  --version          Show version information
              -h, --help             Show this help
            """
        );
        return exitCode;
    }
}
