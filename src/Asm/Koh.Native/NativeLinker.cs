using System.Diagnostics;
using System.Text.Json;
using Koh.Common;
using Koh.Objects;

namespace Koh.Native;

public sealed record LinkOptions(
    bool CgbCompatible = false,
    bool CgbOnly = false,
    bool PadToPowerOfTwo = true,
    int MinSize = 0x8000
);

public sealed record LinkerInput(string FilePath, EmitModel Model);

public sealed record NativeLinkedSymbol(
    string Name,
    long AbsoluteAddress,
    int PlacedBank,
    SymbolKind Kind
);

public sealed record NativeLinkedLine(
    byte Bank,
    ushort Address,
    int ByteCount,
    string File,
    uint Line
);

public sealed class LinkResult
{
    public byte[]? RomData { get; internal init; }
    public byte[] DebugData { get; internal init; } = [];
    public string SymbolText { get; internal init; } = "";
    public IReadOnlyList<NativeLinkedSymbol> Symbols { get; internal init; } = [];
    public IReadOnlyList<NativeLinkedLine> LineMap { get; internal init; } = [];
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal init; } = [];
    public bool Success => RomData is not null && Diagnostics.Count == 0;
}

public sealed class NativeLinker
{
    public LinkResult Link(IReadOnlyList<LinkerInput> inputs, LinkOptions? options = null)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "koh-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            options ??= new();
            var arguments = new List<string>();
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var path = Path.Combine(scratch, $"{i}.o");
                if (input.Model.ElfImage is { } elf)
                    File.WriteAllBytes(path, elf);
                else
                    Run(["--emit-elf", "-o", path], NativeObject.Serialize(input.Model));
                arguments.Add(path);
            }
            var output = Path.Combine(scratch, "output.gb");
            var sym = Path.Combine(scratch, "output.sym");
            var kdbg = Path.Combine(scratch, "output.kdbg");
            var report = Path.Combine(scratch, "report.json");
            arguments.AddRange([
                "-o",
                output,
                "-n",
                sym,
                "-d",
                kdbg,
                "--report-json",
                report,
                $"--size={options.MinSize:x}",
            ]);
            if (!options.PadToPowerOfTwo)
                arguments.Add("--raw");
            if (options.CgbOnly)
                arguments.Add("--cgb-only");
            else if (options.CgbCompatible)
                arguments.Add("--cgb-compatible");
            Run(arguments);
            var debugData = File.ReadAllBytes(kdbg);
            var symbolText = File.ReadAllText(sym);
            using var metadata = JsonDocument.Parse(File.ReadAllText(report));
            var symbols = metadata
                .RootElement.GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => new NativeLinkedSymbol(
                    symbol.GetProperty("name").GetString()!,
                    symbol.GetProperty("value").GetInt64(),
                    symbol.GetProperty("bank").ValueKind == JsonValueKind.Null
                        ? 0
                        : symbol.GetProperty("bank").GetInt32(),
                    symbol.GetProperty("kind").GetByte() == 1
                        ? SymbolKind.Constant
                        : SymbolKind.Label
                ))
                .ToArray();
            return new LinkResult
            {
                RomData = File.ReadAllBytes(output),
                DebugData = debugData,
                SymbolText = symbolText,
                Symbols = symbols,
                LineMap = metadata
                    .RootElement.GetProperty("lines")
                    .EnumerateArray()
                    .Select(line => new NativeLinkedLine(
                        line.GetProperty("bank").GetByte(),
                        line.GetProperty("address").GetUInt16(),
                        line.GetProperty("count").GetInt32(),
                        line.GetProperty("file").GetString()!,
                        line.GetProperty("line").GetUInt32()
                    ))
                    .ToArray(),
            };
        }
        catch (Exception error)
            when (error
                    is IOException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or OverflowException
                        or ArgumentException
                        or JsonException
                        or KeyNotFoundException
            )
        {
            return new LinkResult
            {
                Diagnostics = [new Diagnostic(default, error.Message, DiagnosticSeverity.Error)],
            };
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Run(IEnumerable<string> arguments, string? input = null)
    {
        var start = new ProcessStartInfo(FindExecutable())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start koh-link");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null)
                process.StandardInput.Write(input);
            process.StandardInput.Close();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new IOException("koh-link exceeded 120 seconds");
            }
            Task.WhenAll(output, errors).GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new IOException(
                    $"koh-link exited {process.ExitCode}: {errors.Result}{output.Result}"
                );
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    private static string FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("KOH_LINK");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var name = OperatingSystem.IsWindows() ? "koh-link.exe" : "koh-link";
        foreach (
            var directory in new[]
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(typeof(NativeLinker).Assembly.Location),
            }
        )
        {
            if (directory is not null && File.Exists(Path.Combine(directory, name)))
                return Path.Combine(directory, name);
        }
        foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (
                var directory = new DirectoryInfo(origin);
                directory is not null;
                directory = directory.Parent
            )
                if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml")))
                    foreach (var profile in new[] { "debug", "release" })
                    {
                        var path = Path.Combine(directory.FullName, "target", profile, name);
                        if (File.Exists(path))
                            return path;
                    }
        return name;
    }
}
