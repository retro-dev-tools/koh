using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Debugger.Dap.Handlers;
using Koh.Debugger.Dap.Messages;
using Koh.Emulator.Core;
using Koh.Linker.Core;

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

    // Packed banked-address set of one-shot breakpoints installed by
    // step-over / step-out. The PausedOnBreak handler translates hits
    // on these addresses into "step" events and removes the breakpoint
    // so the run-to-here isn't permanent.
    private readonly HashSet<uint> _oneShotAddrs = new();

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
        _loop.PausedOnBreak += reason =>
        {
            // If the halt was on an address we installed for step-over
            // / step-out, remove the breakpoint and report it as a
            // step rather than a breakpoint hit.
            if (reason == StopReason.Breakpoint && _loop.CurrentSystem is { } sys)
            {
                ushort pc = sys.Cpu.Registers.Pc;
                byte bank = pc >= 0x4000 ? sys.Cartridge.CurrentRomBank : (byte)0;
                var addr = new BankedAddress(bank, pc);
                if (_oneShotAddrs.Remove(addr.Packed))
                {
                    _session.Breakpoints.Remove(addr);
                    SendStoppedEvent("step");
                    return;
                }
            }
            SendStoppedEvent(reason switch
            {
                StopReason.Breakpoint          => "breakpoint",
                StopReason.Watchpoint          => "data breakpoint",
                StopReason.InstructionComplete => "step",
                _ => "paused",
            });
        };

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
        // honoured in configurationDone; debugInfo is loaded now so
        // that setBreakpoints (which follows shortly) can resolve
        // source lines against the SourceMap.
        dispatcher.RegisterHandler("launch", req =>
        {
            _stopOnEntry = false;
            if (req.Arguments is JsonElement arg && arg.ValueKind == JsonValueKind.Object)
            {
                if (arg.TryGetProperty("stopOnEntry", out var soe) && soe.ValueKind == JsonValueKind.True)
                    _stopOnEntry = true;
                if (arg.TryGetProperty("debugInfo", out var diArg) && diArg.ValueKind == JsonValueKind.String)
                {
                    var diPath = diArg.GetString();
                    if (!string.IsNullOrEmpty(diPath))
                    {
                        try
                        {
                            var bytes = File.ReadAllBytes(diPath);
                            _session.DebugInfo.Load(bytes);
                            Console.Error.WriteLine($"[koh-dap] loaded .kdbg from {diPath}");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[koh-dap] failed to load .kdbg ({diPath}): {ex.Message}");
                        }
                    }
                }
            }
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

        // Stepping:
        //   stepIn (F11)    → single-instruction step, dives into CALLs.
        //   next   (F10)    → step-over; CALL / RST install a one-shot
        //                     breakpoint at PC + length and continue
        //                     until that return address. Non-control-
        //                     flow instructions fall back to single
        //                     step.
        //   stepOut (Shift-F11) → install a one-shot breakpoint at the
        //                     return address read from [SP] and
        //                     continue until the current function
        //                     returns.
        dispatcher.RegisterHandler("stepIn",  req => { _loop.StepOne();              return Ok(req); });
        dispatcher.RegisterHandler("next",    req => { StepOver();                    return Ok(req); });
        dispatcher.RegisterHandler("stepOut", req => { StepOut();                     return Ok(req); });

        // Read-only inspection handlers — these are pure functions of
        // GameBoySystem state and Koh.Debugger's implementations work
        // unchanged against an adopted system.
        var stackTrace = new StackTraceHandler(_session);
        var variables  = new VariablesHandler(_session);
        var readMemH   = new ReadMemoryHandler(_session);
        var disasm     = new DisassembleHandler(_session);
        var evaluate   = new EvaluateHandler(_session);
        var setBpH     = new SetBreakpointsHandler(_session);
        var bpLocsH    = new BreakpointHandlers(_session);
        dispatcher.RegisterHandler("stackTrace", stackTrace.Handle);
        dispatcher.RegisterHandler("scopes",     ScopesHandler.Handle);
        dispatcher.RegisterHandler("variables",  variables.Handle);
        dispatcher.RegisterHandler("readMemory", readMemH.Handle);
        dispatcher.RegisterHandler("disassemble", disasm.Handle);
        dispatcher.RegisterHandler("evaluate",   evaluate.Handle);
        // Breakpoints: SetBreakpointsHandler resolves source line →
        // BankedAddress via DebugInfo.SourceMap and installs into
        // BreakpointManager. Our DebugSession.AdoptSystem wired
        // System.BreakpointChecker to consult BreakpointManager
        // every instruction, so the emulator thread halts with
        // StopReason.Breakpoint the next time execution hits a
        // resolved address — PausedOnBreak then sends the stopped
        // event to VS Code.
        dispatcher.RegisterHandler("setBreakpoints",            setBpH.Handle);
        dispatcher.RegisterHandler("breakpointLocations",       bpLocsH.HandleBreakpointLocations);
        dispatcher.RegisterHandler("setInstructionBreakpoints", bpLocsH.HandleSetInstructionBreakpoints);
        dispatcher.RegisterHandler("setFunctionBreakpoints",    bpLocsH.HandleSetFunctionBreakpoints);

        // Data breakpoints (watchpoints): VS Code calls
        // dataBreakpointInfo to ask "is this expression watchable?",
        // then setDataBreakpoints to install. DebugSession's
        // WatchpointHook picks them up via System.Mmu.Hook (wired in
        // AdoptSystem) and halts with StopReason.Watchpoint.
        dispatcher.RegisterHandler("dataBreakpointInfo",  new DataBreakpointInfoHandler().Handle);
        dispatcher.RegisterHandler("setDataBreakpoints",  new SetDataBreakpointsHandler(_session).Handle);

        // Write memory: the VS Code hex-editor extension lets the
        // user poke bytes into WRAM / HRAM / IO registers. Useful
        // for "what if this flag were true" debugging.
        dispatcher.RegisterHandler("writeMemory", new WriteMemoryHandler(_session).Handle);

        // exceptionInfo is an advanced feature; no-op stub keeps VS
        // Code quiet if it asks after an unhandled exception event,
        // which this emulator never sends. Returning a plain success
        // with null body is well-formed.
        dispatcher.RegisterHandler("exceptionInfo", ExceptionInfoHandler.Handle);
    }

    private static Response Ok(Request req) =>
        new() { RequestSeq = req.Seq, Command = req.Command, Success = true };

    /// <summary>
    /// DAP <c>next</c> / "step over". For CALL / RST instructions we
    /// don't want to dive into the called function; install a one-
    /// shot breakpoint at the return address and resume. Everything
    /// else single-steps — we don't need the whole roundtrip cost.
    /// </summary>
    private void StepOver()
    {
        var sys = _loop.CurrentSystem;
        if (sys is null) { _loop.StepOne(); return; }

        ushort pc = sys.Cpu.Registers.Pc;
        var (mnemonic, length) = Disassembler.DecodeOne(a => sys.DebugReadByte(a), pc);
        bool isControlFlow = mnemonic.StartsWith("CALL", StringComparison.Ordinal)
                          || mnemonic.StartsWith("RST",  StringComparison.Ordinal);
        if (!isControlFlow) { _loop.StepOne(); return; }

        ushort ret = (ushort)(pc + length);
        RunUntilPc(sys, ret);
    }

    /// <summary>
    /// DAP <c>stepOut</c>. Reads the return address from the top of
    /// stack (SM83 stack: pushed little-endian, so the two bytes at
    /// [SP] + [SP+1] form the 16-bit return PC), installs a one-shot
    /// breakpoint there, and resumes. Works for ordinary CALL /
    /// RST / interrupt frames; fancy tricks that manipulate SP or
    /// the return value on the stack fall through and step out to
    /// the modified target, which is usually what the user wants.
    /// </summary>
    private void StepOut()
    {
        var sys = _loop.CurrentSystem;
        if (sys is null) { _loop.StepOne(); return; }

        ushort sp = sys.Cpu.Registers.Sp;
        byte lo = sys.DebugReadByte(sp);
        byte hi = sys.DebugReadByte((ushort)(sp + 1));
        ushort ret = (ushort)(lo | (hi << 8));
        RunUntilPc(sys, ret);
    }

    /// <summary>
    /// Install a one-shot breakpoint at <paramref name="targetPc"/>
    /// and resume execution. The PausedOnBreak handler removes the
    /// breakpoint and reports the hit as a "step" stopped event.
    /// </summary>
    private void RunUntilPc(GameBoySystem sys, ushort targetPc)
    {
        byte bank = targetPc >= 0x4000 ? sys.Cartridge.CurrentRomBank : (byte)0;
        var addr = new BankedAddress(bank, targetPc);
        _session.Breakpoints.Add(addr);
        _oneShotAddrs.Add(addr.Packed);
        _loop.Resume();
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
