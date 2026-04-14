using System.Text.Json;
using Koh.Debugger.Dap.Messages;
using Koh.Emulator.Core.Cpu;

namespace Koh.Debugger.Dap.Handlers;

public sealed class VariablesHandler
{
    private readonly DebugSession _session;
    public VariablesHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.VariablesArguments);
        if (args is null)
            return new Response { Success = false, Message = "variables: missing args" };

        var system = _session.System;
        if (system is null)
            return new Response { Success = true, Body = new VariablesResponseBody { Variables = [] } };

        Variable[] variables = args.VariablesReference switch
        {
            ScopesHandler.RegistersVariablesRef => RegistersScope(system),
            ScopesHandler.HardwareVariablesRef => HardwareScope(system),
            ScopesHandler.SymbolsVariablesRef => SymbolsScope(_session),
            ScopesHandler.SourceContextVariablesRef => SourceContextScope(_session, system),
            _ => [],
        };

        return new Response
        {
            Success = true,
            Body = new VariablesResponseBody { Variables = variables },
        };
    }

    private static Variable[] RegistersScope(Emulator.Core.GameBoySystem gb)
    {
        ref var r = ref gb.Registers;
        static string H8(byte v) => "$" + v.ToString("X2");
        static string H16(ushort v) => "$" + v.ToString("X4");
        return
        [
            new Variable { Name = "A",  Value = H8(r.A) },
            new Variable { Name = "F",  Value = H8(r.F) },
            new Variable { Name = "B",  Value = H8(r.B) },
            new Variable { Name = "C",  Value = H8(r.C) },
            new Variable { Name = "D",  Value = H8(r.D) },
            new Variable { Name = "E",  Value = H8(r.E) },
            new Variable { Name = "H",  Value = H8(r.H) },
            new Variable { Name = "L",  Value = H8(r.L) },
            new Variable { Name = "AF", Value = H16(r.AF) },
            new Variable { Name = "BC", Value = H16(r.BC) },
            new Variable { Name = "DE", Value = H16(r.DE) },
            new Variable { Name = "HL", Value = H16(r.HL) },
            new Variable { Name = "SP", Value = H16(r.Sp) },
            new Variable { Name = "PC", Value = H16(r.Pc) },
            new Variable { Name = "Z",  Value = r.FlagSet(CpuRegisters.FlagZ) ? "true" : "false" },
            new Variable { Name = "N",  Value = r.FlagSet(CpuRegisters.FlagN) ? "true" : "false" },
            new Variable { Name = "H_",  Value = r.FlagSet(CpuRegisters.FlagH) ? "true" : "false" },
            new Variable { Name = "C_",  Value = r.FlagSet(CpuRegisters.FlagC) ? "true" : "false" },
        ];
    }

    private static Variable[] SymbolsScope(DebugSession session)
    {
        // Pagination is limited to 200 entries to keep the DAP response size
        // reasonable; a future revision can add lazy-loading via nested refs.
        const int Limit = 200;
        return session.DebugInfo.SymbolMap.All
            .Take(Limit)
            .Select(s => new Variable
            {
                Name = s.Name,
                Value = $"${s.Bank:X2}:${s.Address:X4}",
                Type = s.Kind.ToString(),
            })
            .ToArray();
    }

    private static Variable[] SourceContextScope(DebugSession session, Emulator.Core.GameBoySystem gb)
    {
        ushort pc = gb.Registers.Pc;
        byte bank = pc >= 0x4000 ? gb.Cartridge.CurrentRomBank : (byte)0;
        var addr = new Koh.Linker.Core.BankedAddress(bank, pc);

        var list = new List<Variable>
        {
            new() { Name = "PC", Value = $"${bank:X2}:${pc:X4}" },
        };

        // Nearest symbol at or before PC (best-effort).
        var nearest = session.DebugInfo.SymbolMap.All
            .Where(s => s.Bank == bank && s.Address <= pc)
            .OrderByDescending(s => s.Address)
            .FirstOrDefault();
        if (nearest is not null)
        {
            int offset = pc - nearest.Address;
            list.Add(new Variable
            {
                Name = "nearestSymbol",
                Value = offset == 0 ? nearest.Name : $"{nearest.Name} + {offset}",
                Type = nearest.Kind.ToString(),
            });
        }

        // Matching symbols at this exact address.
        var atAddr = session.DebugInfo.SymbolMap.LookupByAddress(addr);
        for (int i = 0; i < atAddr.Count; i++)
        {
            list.Add(new Variable
            {
                Name = $"symbol[{i}]",
                Value = atAddr[i].Name,
                Type = atAddr[i].Kind.ToString(),
            });
        }

        return list.ToArray();
    }

    private static Variable[] HardwareScope(Emulator.Core.GameBoySystem gb)
    {
        static string H8(byte v) => "$" + v.ToString("X2");
        return
        [
            new Variable { Name = "LY",   Value = H8(gb.Ppu.LY) },
            new Variable { Name = "IF",   Value = H8(gb.Io.Interrupts.IF) },
            new Variable { Name = "IE",   Value = H8(gb.Io.Interrupts.IE) },
            new Variable { Name = "IME",  Value = gb.Cpu.Ime ? "true" : "false" },
            new Variable { Name = "DIV",  Value = H8(gb.Timer.DIV) },
            new Variable { Name = "TIMA", Value = H8(gb.Timer.TIMA) },
            new Variable { Name = "TMA",  Value = H8(gb.Timer.TMA) },
            new Variable { Name = "TAC",  Value = H8(gb.Timer.TAC) },
        ];
    }
}
