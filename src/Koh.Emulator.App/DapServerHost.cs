using System.IO.Pipes;
using System.Text;
using Koh.Debugger.Dap;
using Koh.Debugger.Dap.Handlers;
using Koh.Debugger.Dap.Messages;

namespace Koh.Emulator.App;

/// <summary>
/// Hosts a DAP debug server over a Windows named pipe (Linux / macOS
/// named-pipe variants land in a later phase). The VS Code extension
/// spawns us with <c>--dap=&lt;pipe-name&gt;</c>, opens the other end,
/// and speaks the Debug Adapter Protocol.
///
/// <para>
/// <see cref="Koh.Debugger.Dap.DapDispatcher"/> already knows how to
/// parse the request/response JSON and route commands to handlers —
/// this class is a thin framing layer (Content-Length headers + pipe
/// I/O) plus a minimal Phase-1 handler set. Future phases wire the
/// remaining handlers (from <see cref="HandlerRegistration"/>) and
/// teach them to drive the live <see cref="EmulatorLoop"/>.
/// </para>
/// </summary>
internal sealed class DapServerHost : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private NamedPipeServerStream? _pipe;

    public DapServerHost(string pipeName)
    {
        _pipeName = pipeName;
        _thread = new Thread(Run) { IsBackground = true, Name = "koh-dap" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            Console.Error.WriteLine($"[koh-dap] listening on \\\\.\\pipe\\{_pipeName}");
            _pipe.WaitForConnection();
            Console.Error.WriteLine("[koh-dap] client connected");

            var dispatcher = new DapDispatcher();
            // Transport callbacks: dispatcher hands us raw JSON bytes
            // for every response/event; we frame them with
            // Content-Length and write to the pipe. Serialised via a
            // lock because ResponseReady and EventReady can race — an
            // event fired from another thread while we're still
            // mid-response write would otherwise interleave.
            var writeLock = new object();
            dispatcher.ResponseReady += bytes => WriteFramed(_pipe, bytes.Span, writeLock);
            dispatcher.EventReady    += bytes => WriteFramed(_pipe, bytes.Span, writeLock);

            // Phase 1 handlers: enough to complete VS Code's initialize
            // → launch → configurationDone → disconnect handshake. The
            // emulator runs normally throughout — no breakpoint / step
            // behaviour yet.
            dispatcher.RegisterHandler("initialize", InitializeHandler.Handle);
            dispatcher.RegisterHandler("launch", NoopSuccessHandler);
            dispatcher.RegisterHandler("configurationDone", NoopSuccessHandler);
            dispatcher.RegisterHandler("disconnect", NoopSuccessHandler);
            dispatcher.RegisterHandler("threads", ThreadsHandler);

            var reader = new FramedReader(_pipe);
            while (!_cts.IsCancellationRequested)
            {
                var body = reader.ReadNext(_cts.Token);
                if (body is null) break;
                dispatcher.HandleRequest(body.Value.Span);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koh-dap] server crashed: {ex.Message}");
        }
        finally
        {
            try { _pipe?.Dispose(); } catch { }
        }
    }

    private static Response NoopSuccessHandler(Request req)
        => new Response { RequestSeq = req.Seq, Command = req.Command, Success = true };

    private static Response ThreadsHandler(Request req)
    {
        // VS Code asks for threads before it displays anything — we
        // report a single synthetic "CPU" thread so the debug UI
        // doesn't show "no threads". Real thread-id is invented; the
        // emulator only ever has one.
        var resp = new Response { RequestSeq = req.Seq, Command = req.Command, Success = true };
        resp.Body = new ThreadsResponseBody
        {
            Threads = [new DapThread { Id = 1, Name = "CPU" }],
        };
        return resp;
    }

    private static void WriteFramed(Stream stream, ReadOnlySpan<byte> body, object writeLock)
    {
        lock (writeLock)
        {
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            stream.Write(header);
            stream.Write(body);
            stream.Flush();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pipe?.Dispose(); } catch { }
        _thread.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }
}

/// <summary>
/// Reads Content-Length-framed DAP messages from a stream. DAP uses
/// the same framing as LSP: ASCII header lines terminated by
/// <c>\r\n\r\n</c>, then a JSON body of the declared length. Other
/// headers (Content-Type, etc.) are ignored — in practice VS Code
/// only sends Content-Length.
/// </summary>
internal sealed class FramedReader
{
    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[1024];
    private int _headerFill;

    public FramedReader(Stream stream) { _stream = stream; }

    public ReadOnlyMemory<byte>? ReadNext(CancellationToken ct)
    {
        // Header terminator = \r\n\r\n (four bytes). Scan the header
        // buffer as bytes arrive; once we see the terminator, parse
        // Content-Length and shift any body bytes we over-read to the
        // start of the next body buffer.
        int terminator = -1;
        while (!ct.IsCancellationRequested)
        {
            if (_headerFill == _headerBuffer.Length)
                throw new IOException("DAP header exceeded 1 KiB");
            int read = _stream.Read(_headerBuffer, _headerFill, 1);
            if (read == 0) return null;
            _headerFill += read;
            if (_headerFill >= 4 &&
                _headerBuffer[_headerFill - 4] == (byte)'\r' &&
                _headerBuffer[_headerFill - 3] == (byte)'\n' &&
                _headerBuffer[_headerFill - 2] == (byte)'\r' &&
                _headerBuffer[_headerFill - 1] == (byte)'\n')
            {
                terminator = _headerFill;
                break;
            }
        }
        if (terminator < 0) return null;

        var headerText = Encoding.ASCII.GetString(_headerBuffer, 0, terminator - 4);
        int contentLength = -1;
        foreach (var line in headerText.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            if (line.AsSpan(0, idx).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.AsSpan(idx + 1).Trim(), out var n)) contentLength = n;
            }
        }
        _headerFill = 0;   // reset for next frame
        if (contentLength < 0) throw new IOException("DAP frame missing Content-Length");

        var body = new byte[contentLength];
        int filled = 0;
        while (filled < contentLength)
        {
            int read = _stream.Read(body, filled, contentLength - filled);
            if (read == 0) return null;
            filled += read;
        }
        return body;
    }
}
