using System.Reflection;
using Koh.Common;
using Koh.Core.Binding;
using Koh.Emit;
using Koh.Linker;

return KohLink.Run(args);

static class KohLink
{
    public static int Run(string[] args)
    {
        if (args.Contains("--version"))
            return ShowVersion();

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            return ShowUsage(exitCode: args.Length == 0 ? 1 : 0);

        var (parsed, error) = ParseArgs(args);
        if (error != null)
            return Fail(error);
        var (inputs, outputPath, symPath, kdbgPath, raw, minSize) = parsed!;
        if (inputs.Count == 0)
            return Fail("no input files specified");

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
        var linker = new Koh.Linker.Linker();
        var result = linker.Link(
            linkerInputs,
            new LinkOptions(PadToPowerOfTwo: !raw, MinSize: minSize)
        );

        // Report diagnostics
        foreach (var diag in result.Diagnostics)
        {
            var severity = diag.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info",
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
                try
                {
                    File.Delete(romTemp);
                }
                catch { }
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

        // Write .kdbg file
        if (kdbgPath != null)
        {
            try
            {
                var builder = new DebugInfoBuilder();
                DebugInfoPopulator.Populate(builder, result);
                using var stream = File.Create(kdbgPath);
                KdbgFileWriter.Write(stream, builder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail($"cannot write '{kdbgPath}': {ex.Message}");
            }
        }

        var noun = inputs.Count == 1 ? "object" : "objects";
        Console.WriteLine(
            $"Linked {inputs.Count} {noun} -> {outputPath} ({result.RomData!.Length} bytes)"
        );
        return 0;
    }

    /// <summary>
    /// Parsed command line. A record rather than a tuple: with --raw and --size there are
    /// six fields, and a six-element tuple at every early return is unreadable.
    /// </summary>
    sealed record LinkArgs(
        List<string> Inputs,
        string Output,
        string? Sym,
        string? Kdbg,
        bool Raw,
        int MinSize
    );

    static (LinkArgs? parsed, string? error) ParseArgs(string[] args)
    {
        var inputs = new List<string>();
        string? output = null,
            sym = null,
            kdbg = null;
        bool raw = false;
        int minSize = 0x8000;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-o" or "--output")
            {
                if (i + 1 >= args.Length)
                    return (null, $"option '{args[i]}' requires an argument");
                output = args[++i];
            }
            else if (args[i] is "-n" or "--sym")
            {
                if (i + 1 >= args.Length)
                    return (null, $"option '{args[i]}' requires an argument");
                sym = args[++i];
            }
            else if (args[i] is "-d" or "--kdbg")
            {
                if (i + 1 >= args.Length)
                    return (null, $"option '{args[i]}' requires an argument");
                kdbg = args[++i];
            }
            else if (args[i] is "--raw")
            {
                raw = true;
            }
            else if (args[i].StartsWith("--size=", StringComparison.Ordinal))
            {
                // Hex without a prefix, matching how the rest of the toolchain spells
                // addresses: --size=900 is 2304 bytes, the CGB boot ROM size.
                var text = args[i]["--size=".Length..];
                if (
                    !int.TryParse(
                        text,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out minSize
                    )
                    || minSize <= 0
                )
                    return (null, $"--size expects a positive hex value, got '{text}'");
            }
            else if (!args[i].StartsWith('-'))
            {
                inputs.Add(args[i]);
            }
            else
            {
                return (null, $"unknown option '{args[i]}' (try --help)");
            }
        }

        if (inputs.Count == 0)
            return (new LinkArgs(inputs, "", sym, kdbg, raw, minSize), null);

        output ??= Path.ChangeExtension(inputs[0], raw ? ".bin" : ".gb");
        return (new LinkArgs(inputs, output, sym, kdbg, raw, minSize), null);
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine($"koh-link: {message}");
        return 1;
    }

    static int ShowVersion()
    {
        var ver =
            typeof(KohLink)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "unknown";
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
            Usage: koh-link <input.kobj...> [-o output.gb] [-n symbols.sym] [-d debug.kdbg]

            Options:
              -o, --output <path>  Output ROM file (default: first-input.gb, or .bin with --raw)
              -n, --sym <path>     Write symbol file for emulator debugging
              -d, --kdbg <path>    Write Koh debug info file (.kdbg)
                  --raw            Emit a raw image: exact size, no power-of-two padding,
                                   no cartridge header or checksums. For boot ROMs.
                  --size=<hex>     Image size floor in hex (default 8000 = 32KB).
                                   A DMG boot ROM is --size=100, a CGB one --size=900.
                  --version        Show version information
              -h, --help           Show this help
            """
        );
        return exitCode;
    }
}
