using System.Diagnostics;
using System.Text.Json;
using Koh.Common;
using Koh.Objects;

namespace Koh.Native;

public sealed record LinkOptions(
    bool CgbCompatible = false,
    bool CgbOnly = false,
    bool PadToPowerOfTwo = true,
    int MinSize = 0x8000,
    bool EmitDebugInfo = true
);

public sealed record LinkerInput(string FilePath, EmitModel Model);

public sealed record NativeLinkedSymbol(
    string Name,
    long AbsoluteAddress,
    int PlacedBank,
    SymbolKind Kind
);

public sealed record NativeLinkedLine(
    int Bank,
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

public sealed class NativeLinker(string? executable = null)
{
    public DebugData ReadDebugInfo(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("DWARF input exceeds 64 MiB");
        using var document = JsonDocument.Parse(
            RunAsync(["--read-debug-info"], null, bytes).GetAwaiter().GetResult()
        );
        var symbols = document
            .RootElement.GetProperty("symbols")
            .EnumerateArray()
            .Select(symbol =>
            {
                var source = symbol.GetProperty("source");
                bool hasSource = source.ValueKind != JsonValueKind.Null;
                var kind = symbol.GetProperty("kind").GetByte() switch
                {
                    0 => DebugSymbolKind.Label,
                    1 => DebugSymbolKind.Constant,
                    2 => DebugSymbolKind.RamLabel,
                    _ => throw new InvalidDataException("Unsupported debug symbol kind"),
                };
                var bank = symbol.GetProperty("bank");
                return new DebugSymbol(
                    kind,
                    bank.ValueKind == JsonValueKind.Null ? 0 : bank.GetUInt32(),
                    symbol.GetProperty("value").GetInt32(),
                    symbol.GetProperty("name").GetString()!,
                    hasSource ? source.GetProperty("file").GetString() : null,
                    hasSource ? source.GetProperty("line").GetUInt32() : 0,
                    hasSource ? source.GetProperty("column").GetUInt32() : 0,
                    hasSource ? ReadExpansion(source) : []
                );
            })
            .ToArray();
        var lines = document
            .RootElement.GetProperty("lines")
            .EnumerateArray()
            .Select(line => new DebugAddressMapEntry(
                line.GetProperty("bank").GetUInt32(),
                line.GetProperty("count").GetUInt32(),
                line.GetProperty("address").GetUInt16(),
                line.GetProperty("file").GetString()!,
                line.GetProperty("line").GetUInt32(),
                ReadExpansion(line)
            ))
            .ToArray();
        return new DebugData(symbols, lines);
    }

    private static DebugExpansionFrame[] ReadExpansion(JsonElement source) =>
        source
            .GetProperty("expansion")
            .EnumerateArray()
            .Select(frame => new DebugExpansionFrame(
                frame.GetProperty("file").GetString()!,
                frame.GetProperty("line").GetUInt32(),
                frame.GetProperty("column").GetUInt32()
            ))
            .ToArray();

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
                if (!input.Model.Success)
                    throw new InvalidDataException(
                        $"{input.FilePath}: compilation failed: {string.Join("; ", input.Model.Diagnostics.Select(diagnostic => diagnostic.Message))}"
                    );
                var path = Path.Combine(scratch, $"{i}.o");
                if (input.Model.ElfImage is { } elf)
                    File.WriteAllBytes(path, elf);
                else
                    Run(["--emit-elf", "-o", path], NativeObject.Serialize(input.Model));
                arguments.Add(path);
            }
            var output = Path.Combine(scratch, "output.gb");
            var sym = Path.Combine(scratch, "output.sym");
            var debug = Path.Combine(scratch, "output.elf");
            var report = Path.Combine(scratch, "report.json");
            arguments.AddRange([
                "-o",
                output,
                "-n",
                sym,
                "--report-json",
                report,
                $"--size={options.MinSize:x}",
            ]);
            if (options.EmitDebugInfo)
                arguments.AddRange(["-d", debug]);
            if (!options.PadToPowerOfTwo)
                arguments.Add("--raw");
            if (options.CgbOnly)
                arguments.Add("--cgb-only");
            else if (options.CgbCompatible)
                arguments.Add("--cgb-compatible");
            Run(arguments);
            var debugData = options.EmitDebugInfo ? File.ReadAllBytes(debug) : [];
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
                        line.GetProperty("bank").GetInt32(),
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
                        or InvalidDataException
                        or UnauthorizedAccessException
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

    private void Run(IEnumerable<string> arguments, string? input = null) =>
        RunAsync(arguments, input).GetAwaiter().GetResult();

    private async Task<string> RunAsync(
        IEnumerable<string> arguments,
        string? input,
        ReadOnlyMemory<byte>? binaryInput = null
    )
    {
        var start = new ProcessStartInfo(executable ?? FindExecutable())
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var output = ReadBoundedAsync(process.StandardOutput, 64 * 1024 * 1024, timeout);
        var errors = ReadBoundedAsync(process.StandardError, 1024 * 1024, timeout);
        try
        {
            if (binaryInput is { } binary)
                await process
                    .StandardInput.BaseStream.WriteAsync(binary, timeout.Token)
                    .ConfigureAwait(false);
            else if (input is not null)
                await process
                    .StandardInput.WriteAsync(input.AsMemory(), timeout.Token)
                    .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException(
                    $"koh-link exited {process.ExitCode}: {await errors.ConfigureAwait(false)}{await output.ConfigureAwait(false)}"
                );
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
        {
            if (output.IsFaulted)
                await output.ConfigureAwait(false);
            if (errors.IsFaulted)
                await errors.ConfigureAwait(false);
            throw new IOException("koh-link exceeded 120 seconds", error);
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

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int limit,
        CancellationTokenSource cancellation
    )
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[8192];
        try
        {
            int count;
            while (
                (
                    count = await reader
                        .ReadAsync(buffer.AsMemory(), cancellation.Token)
                        .ConfigureAwait(false)
                ) != 0
            )
            {
                if (count > limit - text.Length)
                    throw new InvalidDataException("Native linker response exceeds its size limit");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        catch
        {
            cancellation.Cancel();
            throw;
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
