using System.Text;
using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator;
using Koh.Objects;

var dispatcher = new DapDispatcher();
var session = new DebugSession();
dispatcher.ResponseReady += data => Console.WriteLine(Encoding.UTF8.GetString(data.Span));
HandlerRegistration.RegisterAll(dispatcher, session, path => File.ReadAllBytes(path));
while (Console.ReadLine() is { } line)
{
    using var request = JsonDocument.Parse(line);
    string? command = request.RootElement.GetProperty("command").GetString();
    if (command == "state")
    {
        var gb = session.System ?? throw new InvalidOperationException("No session");
        var state = request.RootElement.GetProperty("arguments");
        gb.Registers.Pc = state.GetProperty("pc").GetUInt16();
        gb.Registers.Sp = state.GetProperty("sp").GetUInt16();
        foreach (var symbol in state.GetProperty("symbols").EnumerateArray())
            session.DebugInfo.SymbolMap.Add(
                new KdbgParsedSymbol(
                    KdbgSymbolKind.Label,
                    0,
                    gb.Registers.Pc,
                    0,
                    symbol.GetString()!,
                    null,
                    null,
                    0
                )
            );
        foreach (var write in state.GetProperty("writes").EnumerateArray())
            if (!gb.Mmu.DebugWrite(write[0].GetUInt16(), write[1].GetByte()))
                throw new InvalidOperationException("State write rejected");
        Console.WriteLine("{\"configured\":true}");
    }
    else if (command == "advance")
    {
        var gb = session.System ?? throw new InvalidOperationException("No session");
        int limit = request
            .RootElement.GetProperty("arguments")
            .TryGetProperty("frames", out var frames)
            ? Math.Clamp(frames.GetInt32(), 1, 1200)
            : 120;
        var reason = StopReason.FrameComplete;
        for (int frame = 0; frame < limit && reason != StopReason.Breakpoint; frame++)
            reason = gb.RunFrame().Reason;
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    reason = reason.ToString(),
                    bootMapped = gb.BootRomMapped,
                    pc = gb.Registers.Pc,
                    bank = gb.Cartridge.CurrentRomBank,
                    sp = gb.Registers.Sp,
                    stack = Convert.ToHexString(
                        Enumerable
                            .Range(0, 64)
                            .Select(i => gb.Mmu.DebugRead((ushort)(gb.Registers.Sp + i)))
                            .ToArray()
                    ),
                }
            )
        );
    }
    else
        dispatcher.HandleRequest(Encoding.UTF8.GetBytes(line));
}
