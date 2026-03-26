using System.Reflection;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emit;
using Koh.Linker.Core;

return KohLink.Run(args);

static class KohLink
{
    public static int Run(string[] args)
    {
        if (args.Contains("--version"))
            return ShowVersion();

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            return ShowUsage(exitCode: args.Length == 0 ? 1 : 0);

        var (inputs, outputPath, symPath, error) = ParseArgs(args);
        if (error != null) return Fail(error);
        if (inputs.Count == 0) return Fail("no input files specified");

        // Load .kobj files
        var linkerInputs = new List<LinkerInput>();
        foreach (var path in inputs)
        {
            if (!File.Exists(path))
                return Fail($"file not found: {path}");

            try
            {
                using var stream = File.OpenRead(path);
                var model = KobjReader.Read(stream);
                linkerInputs.Add(new LinkerInput(path, model));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Fail($"cannot read '{path}': {ex.Message}");
            }
        }

        // Link
        var linker = new Koh.Linker.Core.Linker();
        var result = linker.Link(linkerInputs);

        // Report diagnostics
        foreach (var diag in result.Diagnostics)
        {
            var severity = diag.Severity switch
            {
                DiagnosticSeverity.Error   => "error",
                DiagnosticSeverity.Warning => "warning",
                _                          => "info",
            };
            Console.Error.WriteLine($"koh-link: {severity}: {diag.Message}");
        }

        if (!result.Success)
            return 1;

        // Write ROM atomically: write to a temp file then rename so a mid-write
        // failure never leaves a corrupt .gb at the destination path.
        var romTemp = outputPath + "." + Path.GetRandomFileName();
        try
        {
            File.WriteAllBytes(romTemp, result.RomData!);
            File.Move(romTemp, outputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"cannot write '{outputPath}': {ex.Message}");
        }
        finally
        {
            if (File.Exists(romTemp))
                try { File.Delete(romTemp); } catch { }
        }

        // Write .sym file
        if (symPath != null)
        {
            try
            {
                using var writer = new StreamWriter(symPath);
                SymFileWriter.Write(writer, result.Symbols);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail($"cannot write '{symPath}': {ex.Message}");
            }
        }

        var noun = inputs.Count == 1 ? "object" : "objects";
        Console.WriteLine($"Linked {inputs.Count} {noun} -> {outputPath} ({result.RomData!.Length} bytes)");
        return 0;
    }

    static (List<string> inputs, string output, string? sym, string? error) ParseArgs(string[] args)
    {
        var inputs = new List<string>();
        string? output = null, sym = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-o" or "--output")
            {
                if (i + 1 >= args.Length)
                    return ([], "", null, $"option '{args[i]}' requires an argument");
                output = args[++i];
            }
            else if (args[i] is "-n" or "--sym")
            {
                if (i + 1 >= args.Length)
                    return ([], "", null, $"option '{args[i]}' requires an argument");
                sym = args[++i];
            }
            else if (!args[i].StartsWith('-'))
            {
                inputs.Add(args[i]);
            }
            else
            {
                return ([], "", null, $"unknown option '{args[i]}' (try --help)");
            }
        }

        output ??= Path.ChangeExtension(inputs[0], ".gb");
        return (inputs, output, sym, null);
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine($"koh-link: {message}");
        return 1;
    }

    static int ShowVersion()
    {
        var ver = typeof(KohLink).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        // Strip git commit hash suffix (e.g. "1.0.0+abc123" → "1.0.0")
        var display = ver.Contains('+') ? ver[..ver.IndexOf('+')] : ver;
        Console.WriteLine($"koh-link {display}");
        return 0;
    }

    static int ShowUsage(int exitCode)
    {
        // Help explicitly requested → stdout so `koh-link --help | less` works.
        // Triggered by missing args → stderr (it is an error condition).
        var output = exitCode == 0 ? Console.Out : Console.Error;
        output.WriteLine(
            """
            Usage: koh-link <input.kobj...> [-o output.gb] [-n symbols.sym]

            Options:
              -o, --output <path>  Output ROM file (default: first-input.gb)
              -n, --sym <path>     Write symbol file for emulator debugging
                  --version        Show version information
              -h, --help           Show this help
            """);
        return exitCode;
    }
}
