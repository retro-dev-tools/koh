using System.Text;
using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator;

var dispatcher = new DapDispatcher();
var session = new DebugSession();
dispatcher.ResponseReady += data => Console.WriteLine(Encoding.UTF8.GetString(data.Span));
HandlerRegistration.RegisterAll(dispatcher, session, path => File.ReadAllBytes(path));
while (Console.ReadLine() is { } line)
{
    using var request = JsonDocument.Parse(line);
    if (request.RootElement.GetProperty("command").GetString() == "advance")
    {
        var gb = session.System ?? throw new InvalidOperationException("No session");
        var reason = StopReason.FrameComplete;
        for (int frame = 0; frame < 120 && reason != StopReason.Breakpoint; frame++)
            reason = gb.RunFrame().Reason;
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    reason = reason.ToString(),
                    bootMapped = gb.BootRomMapped,
                    pc = gb.Registers.Pc,
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
