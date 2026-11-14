using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Koh.Native;

internal sealed class NativeClient : IDisposable
{
    internal static NativeClient Shared { get; } = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private readonly object gate = new();
    private Process? process;
    private readonly StringBuilder errors = new();
    private readonly ProcessStartInfo? startInfo;
    private readonly TimeSpan requestTimeout;

    internal NativeClient(ProcessStartInfo? startInfo = null, TimeSpan? requestTimeout = null)
    {
        this.startInfo = startInfo;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
    }

    internal NativeResponse Request(
        string op,
        IEnumerable<NativeSource> sources,
        IEnumerable<string> roots,
        bool allowUndefined = false,
        string format = "kobj",
        IEnumerable<NativeBinary>? binaryFiles = null
    )
    {
        lock (gate)
        {
            EnsureStarted();
            using var timeout = new CancellationTokenSource(requestTimeout);
            try
            {
                var request = JsonSerializer.Serialize(
                    new
                    {
                        version = 1,
                        op,
                        sources,
                        roots,
                        allowUndefined,
                        format,
                        binaryFiles = binaryFiles ?? [],
                    },
                    Json
                );
                var line = ExchangeAsync(process!, request, timeout.Token)
                    .WaitAsync(timeout.Token)
                    .GetAwaiter()
                    .GetResult();
                var response =
                    JsonSerializer.Deserialize<NativeResponse>(line, Json)
                    ?? throw new InvalidDataException("Empty native assembler response");
                if (response.Version != 1)
                    throw new InvalidDataException(
                        $"Unsupported native assembler protocol version {response.Version}"
                    );
                return response;
            }
            catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
            {
                Stop(force: true);
                throw new TimeoutException(
                    $"Native assembler request exceeded {requestTimeout.TotalSeconds:g} seconds",
                    error
                );
            }
            catch
            {
                Stop(force: true);
                throw;
            }
        }
    }

    private async Task<string> ExchangeAsync(
        Process child,
        string request,
        CancellationToken cancellationToken
    )
    {
        var input = child.StandardInput;
        var output = child.StandardOutput;
        await input.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await output.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Native assembler closed its protocol stream: {ErrorText()}");
    }

    private void EnsureStarted()
    {
        if (process is { HasExited: false })
            return;
        Stop();
        var start = startInfo ?? new ProcessStartInfo(FindExecutable());
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.StandardInputEncoding = new UTF8Encoding(false);
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        if (startInfo == null)
            start.ArgumentList.Add("--serve-json");
        var child = new Process { StartInfo = start };
        child.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
                lock (errors)
                {
                    if (errors.Length > 16_384)
                        errors.Remove(0, errors.Length - 8192);
                    errors.AppendLine(args.Data);
                }
        };
        try
        {
            if (!child.Start())
                throw new IOException("Cannot start native assembler");
            child.BeginErrorReadLine();
            process = child;
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private static string FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("KOH_ASM");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var name = OperatingSystem.IsWindows() ? "koh-asm.exe" : "koh-asm";
        var sibling = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(sibling))
            return sibling;
        foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (
                var directory = new DirectoryInfo(origin);
                directory != null;
                directory = directory.Parent
            )
            {
                if (!File.Exists(Path.Combine(directory.FullName, "Cargo.toml")))
                    continue;
                foreach (var profile in new[] { "debug", "release" })
                {
                    var path = Path.Combine(directory.FullName, "target", profile, name);
                    if (File.Exists(path))
                        return path;
                }
            }
        }
        return name;
    }

    private string ErrorText()
    {
        lock (errors)
            return errors.ToString();
    }

    private void Stop(bool force = false)
    {
        var child = process;
        process = null;
        if (child == null)
            return;
        try
        {
            if (!child.HasExited)
            {
                if (force)
                    child.Kill(entireProcessTree: true);
                else
                {
                    child.StandardInput.Close();
                    if (!child.WaitForExit(1000))
                        child.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException) when (child.HasExited) { }
        finally
        {
            child.StandardInput.BaseStream.Dispose();
            child.StandardOutput.BaseStream.Dispose();
            child.Dispose();
            lock (errors)
                errors.Clear();
        }
    }

    public void Dispose()
    {
        lock (gate)
            Stop();
    }
}
