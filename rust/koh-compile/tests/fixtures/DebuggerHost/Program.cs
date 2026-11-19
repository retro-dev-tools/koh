using System.Text;
using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Objects;

var dispatcher = new DapDispatcher();
var session = new DebugSession();
dispatcher.ResponseReady += data => Console.WriteLine(Encoding.UTF8.GetString(data.Span));
HandlerRegistration.RegisterAll(dispatcher, session, path => File.ReadAllBytes(path));
while (Console.ReadLine() is { } line)
{
    using var request = JsonDocument.Parse(line);
    string? command = request.RootElement.GetProperty("command").GetString();
    if (command == "machine")
    {
        var state = request.RootElement.GetProperty("arguments");
        var gb = new GameBoySystem(
            CartridgeFactory.Load(File.ReadAllBytes(state.GetProperty("program").GetString()!))
        );
        var registers = state.GetProperty("r");
        gb.Registers.B = registers[0].GetByte();
        gb.Registers.C = registers[1].GetByte();
        gb.Registers.D = registers[2].GetByte();
        gb.Registers.E = registers[3].GetByte();
        gb.Registers.H = registers[4].GetByte();
        gb.Registers.L = registers[5].GetByte();
        gb.Registers.A = registers[7].GetByte();
        gb.Registers.F = state.GetProperty("f").GetByte();
        gb.Registers.Pc = state.GetProperty("pc").GetUInt16();
        gb.Registers.Sp = state.GetProperty("sp").GetUInt16();
        gb.Cpu.Ime = state.GetProperty("ime").GetBoolean();
        foreach (var write in state.GetProperty("writes").EnumerateArray())
            if (!gb.Mmu.DebugWrite(write[0].GetUInt16(), write[1].GetByte()))
                throw new InvalidOperationException("Machine write rejected");
        var observed = new MachineAccesses();
        gb.Mmu.Hook = observed;
        int steps = state.GetProperty("steps").GetInt32();
        if (steps is < 1 or > 2)
            throw new InvalidOperationException("Invalid machine step count");
        for (int step = 0; step < steps; step++)
            gb.StepInstruction();
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    r = new int[]
                    {
                        gb.Registers.B,
                        gb.Registers.C,
                        gb.Registers.D,
                        gb.Registers.E,
                        gb.Registers.H,
                        gb.Registers.L,
                        gb.Mmu.DebugRead(0xC020),
                        gb.Registers.A,
                    },
                    f = gb.Registers.F,
                    pc = gb.Registers.Pc,
                    sp = gb.Registers.Sp,
                    ime = gb.Cpu.Ime,
                    halted = gb.Cpu.Halted,
                    io = gb.Mmu.DebugRead(0xFF40),
                    stack = Enumerable
                        .Range(0, 4)
                        .Select(i => (int)gb.Mmu.DebugRead((ushort)(0xD00E + i)))
                        .ToArray(),
                    events = observed.Events,
                }
            )
        );
    }
    else if (command == "state")
    {
        var gb = session.System ?? throw new InvalidOperationException("No session");
        var state = request.RootElement.GetProperty("arguments");
        gb.Registers.Pc = state.GetProperty("pc").GetUInt16();
        gb.Registers.Sp = state.GetProperty("sp").GetUInt16();
        foreach (var symbol in state.GetProperty("symbols").EnumerateArray())
            session.DebugInfo.SymbolMap.Add(
                new DebugSymbol(
                    DebugSymbolKind.Label,
                    0,
                    gb.Registers.Pc,
                    symbol.GetString()!,
                    null,
                    0,
                    0,
                    []
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

sealed class MachineAccesses : Koh.Emulator.Debug.MemoryHook
{
    public List<string> Events { get; } = [];

    public override void OnRead(ushort address, byte value)
    {
        if (address >= 0x8000)
            Record('R', address, value);
    }

    public override void OnWrite(ushort address, byte value) => Record('W', address, value);

    private void Record(char kind, ushort address, byte value)
    {
        if (Events.Count >= 16)
            throw new InvalidOperationException("Machine trace capacity exceeded");
        Events.Add($"{kind}{address:X4}:{value:X2}");
    }
}
