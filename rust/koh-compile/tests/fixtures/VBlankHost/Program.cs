using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Emulator.Debug;

byte[] rom = File.ReadAllBytes(args[0]);
ushort stop = Convert.ToUInt16(args[1], 16);
ushort site = Convert.ToUInt16(args[2], 16);
ushort end = Convert.ToUInt16(args[3], 16);
foreach (int line in Enumerable.Range(0, 154))
foreach (int dot in new[] { 0, 452 })
{
    var gb = new GameBoySystem(CartridgeFactory.Load(rom));
    gb.Cpu.Registers.Pc = 0x100;
    gb.Mmu.VramArray.AsSpan().Fill(0xA7);
    gb.Mmu.DebugWrite(0xFF70, 2);
    int steps = 0;
    while (gb.Cpu.Registers.Pc != site && steps++ < 100_000)
        gb.StepInstruction();
    if (gb.Cpu.Registers.Pc != site)
        throw new InvalidOperationException("Cannot reach copy operation");
    gb.Cpu.Registers.Pc = 0x3001;
    gb.StepInstruction();
    gb.StepInstruction();
    gb.Mmu.DebugWrite(0xFF40, 0x91);
    for (int ticks = 0; ticks < line * 456 + dot; ticks += 4)
    {
        gb.Cpu.Registers.Pc = 0x3000;
        gb.StepInstruction();
    }
    if (args.Length > 4 && args[4] == "off")
    {
        while (gb.Ppu.LY < 144)
        {
            gb.Cpu.Registers.Pc = 0x3000;
            gb.StepInstruction();
        }
        gb.Mmu.DebugWrite(0xFF40, 0);
    }
    gb.Cpu.Registers.Pc = site;
    var trace = new Writes(gb);
    gb.Mmu.Hook = trace;
    ulong start = gb.Clock.SystemTicks;
    steps = 0;
    bool? ime = null;
    while (gb.Cpu.Registers.Pc != stop && steps++ < 100_000)
    {
        if (gb.Cpu.Registers.Pc == end)
            ime = gb.Cpu.Ime;
        gb.StepInstruction();
    }
    if (gb.Cpu.Registers.Pc != stop)
        throw new InvalidOperationException("Copy did not finish");
    if (ime is null)
        throw new InvalidOperationException("Copy boundary was not observed");
    uint count =
        ((uint)gb.Cpu.Registers.D << 24)
        | ((uint)gb.Cpu.Registers.E << 16)
        | ((uint)gb.Cpu.Registers.H << 8)
        | gb.Cpu.Registers.L;
    string writes =
        trace.Cells.Count == 0
            ? "-"
            : string.Join(',', trace.Cells.Select(cell => string.Join(':', cell)));
    Console.WriteLine(
        $"{line} {dot} {count} {gb.Cpu.Registers.Sp} {gb.Mmu.DebugRead(0xFF70) & 7} {trace.Reads} {gb.Clock.SystemTicks - start} {Convert.ToHexString(gb.Mmu.VramArray)} {writes} {trace.Sources} {(ime.Value ? 1 : 0)}"
    );
}

sealed class Writes(GameBoySystem system) : MemoryHook
{
    public int Reads { get; private set; }
    public int Sources { get; private set; }
    public List<int[]> Cells { get; } = [];

    public override void OnRead(ushort address, byte value)
    {
        if (address == 0xFF44)
            Reads++;
        if (address >= 0x2000 && address < 0x2200)
            Sources++;
    }

    public override void OnWrite(ushort address, byte value)
    {
        if (address >= 0x8000 && address < 0xA000)
            Cells.Add([address, value, system.Ppu.LY, system.Ppu.Dot]);
    }
}
