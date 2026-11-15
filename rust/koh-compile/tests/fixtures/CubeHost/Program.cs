using Koh.Boot;
using Koh.Emulator;
using Koh.Emulator.Boot;
using Koh.Emulator.Cartridge;
using Koh.Emulator.Debug;

var cartridge = CartridgeFactory.Load(File.ReadAllBytes(args[0]));
var gb = new GameBoySystem(cartridge);
gb.LoadBootRom(BootRom.FromBytes(gb.Mode == HardwareMode.Cgb ? KohBootRoms.Cgb : KohBootRoms.Dmg));
gb.Cpu.Registers.Pc = 0;
int first = int.Parse(args[1]);
int last = int.Parse(args[2]);
ushort abort = Convert.ToUInt16(args[3], 16);
ushort managed = Convert.ToUInt16(args[4], 16);
ushort raw = Convert.ToUInt16(args[5], 16);
var guard = new Mode3WriteGuard(gb);
var dma = new DmaTrace(gb);
gb.Mmu.Hook = new CompositeMemoryHook(guard, dma);
void Frame()
{
    var result = gb.RunFrame();
    if (result.Reason != StopReason.FrameComplete)
        throw new InvalidOperationException($"Stopped: {result.Reason}");
}
int bootFrames = 0;
while (gb.BootRomMapped && bootFrames++ < 600)
    Frame();
int frame = 0;
byte[] previous = gb.Framebuffer.Front.ToArray();
var changes = new List<int>();
ushort firstPc = 0;
int firstBank = 0;
var profile = new Dictionary<int, ulong>();
if (Environment.GetEnvironmentVariable("KOH_CUBE_PROFILE") == "1")
{
    ulong previousCycles = gb.Cpu.TotalTCycles;
    int previousPc = gb.Cpu.Registers.Pc;
    int previousBank = previousPc < 0x4000 ? 0 : cartridge.CurrentRomBank;
    gb.BreakpointChecker = pc =>
    {
        int key = (previousBank << 16) | previousPc;
        profile[key] = profile.GetValueOrDefault(key) + gb.Cpu.TotalTCycles - previousCycles;
        previousCycles = gb.Cpu.TotalTCycles;
        previousPc = pc;
        previousBank = pc < 0x4000 ? 0 : cartridge.CurrentRomBank;
        return false;
    };
}
if (!gb.BootRomMapped)
    while (frame < last)
    {
        Frame();
        frame++;
        if (!gb.Framebuffer.Front.SequenceEqual(previous))
        {
            changes.Add(frame);
            gb.Framebuffer.Front.CopyTo(previous);
        }
        if (gb.Cpu.Registers.Pc >= abort && gb.Cpu.Registers.Pc < abort + 3)
            break;
        if (frame == first || frame == last)
            File.WriteAllBytes(args[0] + $".{frame}.rgba", gb.Framebuffer.Front.ToArray());
        if (frame == first)
        {
            firstPc = gb.Cpu.Registers.Pc;
            firstBank = cartridge.CurrentRomBank;
            gb.BreakpointChecker = null;
        }
    }
if (profile.Count != 0)
{
    using var trace = new BinaryWriter(File.Create(args[0] + ".profile"));
    foreach (var (pc, cycles) in profile.OrderBy(p => p.Key))
    {
        trace.Write(pc);
        trace.Write(cycles);
    }
}
using (var png = File.Create(args[0] + ".png"))
    gb.Framebuffer.WritePng(png);
using (var trace = new BinaryWriter(File.Create(args[0] + ".gdma")))
    foreach (var (line, dot, control) in dma.Starts)
    {
        trace.Write(line);
        trace.Write(dot);
        trace.Write(control);
    }
int Word(ushort address) =>
    gb.Mmu.DebugRead(address) | (gb.Mmu.DebugRead((ushort)(address + 1)) << 8);
int rawStart = (0xe000 - Word(raw) + 15) & ~15;
Console.Error.WriteLine(
    $"rawNZ={Enumerable.Range(rawStart, 0xe000 - rawStart).Count(a => gb.Mmu.DebugRead((ushort)a) != 0)} vramNZ={gb.Mmu.VramArray.Take(0x1800).Count(b => b != 0)} bank={gb.Mmu.DebugRead(0xff70):X2} palette={Convert.ToHexString(gb.Ppu.BgPalette.RawData[..8])} map={Convert.ToHexString(gb.Mmu.VramArray.AsSpan(0x1800 + 4 * 32 + 4, 12))}"
);
Console.Error.WriteLine($"frame-changes {string.Join(',', changes)}");
Console.WriteLine(
    $"{bootFrames} {frame} {gb.Cpu.Registers.Pc:X4} {gb.Cpu.Registers.A:X2} {gb.Cpu.Registers.Sp:X4} {Word(managed)} {Word(raw)} {gb.Mmu.DebugRead(0xff40):X2} {guard.Violations.Count} {cartridge.CurrentRomBank} {firstPc:X4} {firstBank}"
);

sealed class DmaTrace(GameBoySystem system) : MemoryHook
{
    public List<(byte Line, ushort Dot, byte Control)> Starts { get; } = new();

    public override void OnRead(ushort address, byte value) { }

    public override void OnWrite(ushort address, byte value)
    {
        if (
            address == 0xFF55
            && system.Mode == HardwareMode.Cgb
            && !system.BootRomMapped
            && (system.Ppu.LCDC & 0x80) != 0
        )
            Starts.Add((system.Ppu.LY, (ushort)system.Ppu.Dot, value));
    }
}
