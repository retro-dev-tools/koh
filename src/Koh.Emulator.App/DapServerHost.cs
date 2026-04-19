using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Koh.Debugger;
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
/// parse request / response JSON and route commands to handlers —
/// this class is a thin framing layer (Content-Length headers + pipe
/// I/O) plus the session glue that adopts the emulator's live
/// <see cref="Koh.Emulator.Core.GameBoySystem"/> into the
/// <see cref="DebugSession"/> so Koh.Debugger's handlers read a
/// consistent snapshot instead of building their own.
/// </para>
/// </summary>
internal sealed class DapServerHost : IDisposable
{
    private readonly string _pipeName;
    private readonly EmulatorLoop _loop;
    private readonly DebugSession _session = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private NamedPipeServerStream? _pipe;
    private DapDispatcher? _dispatcher;
    private readonly object _writeLock = new();
    private bool _stopOnEntry;

    public DapServerHost(string pipeName, EmulatorLoop loop)
    {
        _pipeName = pipeName;
        _loop = loop;

        // Attach hooks onto every system the emulator installs. The
        // event fires on whatever thread SetSystem runs on (runner
        // thread for model-driven loads); DebugSession.AdoptSystem is
        // safe to call from there because the loop is paused across
        // SetSystem.
        _loop.SystemInstalled += system => _session.AdoptSystem(system);

        // Loop pause because of a hit breakpoint / watchpoint → tell
        // VS Code. The event fires on the emulator thread, so we
        // build and send the DAP event from there; WriteFramed takes
        // the write-lock so a racing response doesn't interleave.
        _loop.PausedOnBreak += reason => SendStoppedEvent(reason switch
        {
            Koh.Emulator.Core.StopReason.Breakpoint => "breakpoint",
            Koh.Emulator.Core.StopReason.Watchpoint => "data breakpoint",
            _ => "paused",
        });

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
            _dispatcher = dispatcher;
            dispatcher.ResponseReady += bytes => WriteFramed(_pipe, bytes.Span);
            dispatcher.EventReady    += bytes => WriteFramed(_pipe, bytes.Span);

            RegisterHandlers(dispatcher);

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

    private void RegisterHandlers(DapDispatcher dispatcher)
    {
        // Initialize is handled as-is (capabilities are a static
        // response); we follow up with the "initialized" event that
        // tells VS Code it can start sending breakpoint configuration.
        dispatcher.RegisterHandler("initialize", req =>
        {
            var resp = InitializeHandler.Handle(req);
            // Defer the initialized event until after this response
            // hits the wire — the write-lock keeps framing honest
            // even if we fire it inline, but the spec asks for the
            // response first.
            Task.Run(() => dispatcher.SendEvent("initialized", body: null));
            return resp;
        });

        // Launch: the emulator already loaded its ROM from the CLI
        // arg; here we just accept launch options. stopOnEntry is
        // honoured in configurationDone (the canonical "we're about
        // to start executing" point).
        dispatcher.RegisterHandler("launch", req =>
        {
            _stopOnEntry = req.Arguments is JsonElement arg
                && arg.ValueKind == JsonValueKind.Object
                && arg.TryGetProperty("stopOnEntry", out var soe)
                && soe.ValueKind == JsonValueKind.True;
            return new Response { RequestSeq = req.Seq, Command = req.Command, Success = true };
        });

        dispatcher.RegisterHandler("configurationDone", req =>
        {
            if (_stopOnEntry)
            {
                _loop.Pause();
                Task.Run(() => SendStoppedEvent("entry"));
            }
            return new Response { RequestSeq = req.Seq, Command = req.Command, Success = true };
        });

        dispatcher.RegisterHandler("disconnect", req =>
            new Response { RequestSeq = req.Seq, Command = req.Command, Success = true });

        // Threads: one synthetic "CPU" thread so VS Code has something
        // to pin the stack trace to.
        dispatcher.RegisterHandler("threads", req =>
        {
            var resp = new Response { RequestSeq = req.Seq, Command = req.Command, Success = true };
            resp.Body = new ThreadsResponseBody { Threads = [new DapThread { Id = 1, Name = "CPU" }] };
            return resp;
        });

        // Control handlers — drive our EmulatorLoop, not the old
        // ExecutionLoop inside DebugSession.
        dispatcher.RegisterHandler("continue", req =>
        {
            _loop.Resume();
            var resp = new Response { RequestSeq = req.Seq, Command = req.Command, Success = true };
            resp.Body = new ContinueResponseBody { AllThreadsContinued = true };
            return resp;
        });
        dispatcher.RegisterHandler("pause", req =>
        {
            _loop.Pause();
            Task.Run(() => SendStoppedEvent("pause"));
            return new Response { RequestSeq = req.Seq, Command = req.Command, Success = true };
        });
        dispatcher.RegisterHandler("terminate", req =>
        {
            _loop.Pause();
            return new Response { RequestSeq = req.Seq, Command = req.Command, Success = true };
        });

        // Read-only inspection handlers — these are pure functions of
        // GameBoySystem state and Koh.Debugger's implementations work
        // unchanged against an adopted system.
        var stackTrace = new StackTraceHandler(_session);
        var variables  = new VariablesHandler(_session);
        var readMemH   = new ReadMemoryHandler(_session);
        var disasm     = new DisassembleHandler(_session);
        var evaluate   = new EvaluateHandler(_session);
        dispatcher.RegisterHandler("stackTrace", stackTrace.Handle);
        dispatcher.RegisterHandler("scopes",     ScopesHandler.Handle);
        dispatcher.RegisterHandler("variables",  variables.Handle);
        dispatcher.RegisterHandler("readMemory", readMemH.Handle);
        dispatcher.RegisterHandler("disassemble", disasm.Handle);
        dispatcher.RegisterHandler("evaluate",   evaluate.Handle);
    }

    private void SendStoppedEvent(string reason)
    {
        _dispatcher?.SendEvent("stopped", new StoppedEventBody
        {
            Reason = reason,
            ThreadId = 1,
            AllThreadsStopped = true,
        });
    }

    private void WriteFramed(Stream stream, ReadOnlySpan<byte> body)
    {
        lock (_writeLock)
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
        _headerFill = 0;
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
