using Koh.Boot;
using Koh.Emulator;
using Koh.Emulator.Boot;
using Koh.Emulator.Cartridge;
using Koh.Emulator.Debug;

var gb = new GameBoySystem(CartridgeFactory.Load(File.ReadAllBytes(args[0])));
gb.LoadBootRom(BootRom.FromBytes(gb.Mode == HardwareMode.Cgb ? KohBootRoms.Cgb : KohBootRoms.Dmg));
gb.Cpu.Registers.Pc = 0;
var guard = new Mode3WriteGuard(gb);
var trace = new ViolationTrace(gb, guard);
gb.Mmu.Hook = new CompositeMemoryHook(guard, trace);
void Frame()
{
    var result = gb.RunFrame();
    if (result.Reason != StopReason.FrameComplete)
        throw new InvalidOperationException($"Stopped: {result.Reason}");
}
int bootFrames = 0;
while (gb.BootRomMapped && bootFrames++ < 600)
    Frame();
if (gb.BootRomMapped)
    throw new InvalidOperationException($"Boot did not hand off: PC={gb.Cpu.Registers.Pc:X4}");
int startupFrames = 0;
while ((gb.Mmu.DebugRead(0xFF40) != 0xF3 || gb.Mmu.DebugRead(0xFF43) < 2) && startupFrames++ < 2000)
    Frame();
if (startupFrames >= 2000)
    throw new InvalidOperationException("Showcase did not start");
byte[] Read(ushort address, int count) =>
    Enumerable.Range(address, count).Select(i => gb.Mmu.DebugRead((ushort)i)).ToArray();
if (args.Length > 1 && args[1] == "timeline")
{
    using var timeline = File.Create(args[0] + ".timeline");
    for (int frame = 0; frame < 6000; frame++)
    {
        Frame();
        var digits = Read(0x9C06, 5);
        timeline.Write(digits);
        timeline.Write(Read(0xFE00, 16));
        timeline.Write(Read(0xFF47, 2));
        timeline.Write(gb.Ppu.BgPalette.RawData[..8]);
        timeline.Write(gb.Ppu.ObjPalette.RawData[..8]);
        int score = 0;
        foreach (byte digit in digits)
        {
            if (digit == 16)
                continue;
            if (digit < 32 || digit > 41)
            {
                score = -1;
                break;
            }
            score = score * 10 + digit - 32;
        }
        if (score >= 80 * 17)
        {
            Console.WriteLine($"{bootFrames} {startupFrames} {frame + 1} {guard.Violations.Count}");
            return;
        }
    }
    throw new InvalidOperationException("Showcase timeline did not complete");
}
byte scroll = gb.Mmu.DebugRead(0xFF43);
byte[] oam = Read(0xFE00, 160);
int scrollChanges = 0;
int spriteChanges = 0;
var hud = new List<byte>();
for (int i = 0; i < 120; i++)
{
    Frame();
    hud.AddRange(Read(0x9C06, 5));
    byte current = gb.Mmu.DebugRead(0xFF43);
    byte[] sprites = Read(0xFE00, 160);
    if (current != scroll)
        scrollChanges++;
    if (!sprites.AsSpan().SequenceEqual(oam))
        spriteChanges++;
    scroll = current;
    oam = sprites;
}
for (int i = 0; gb.OamDma.IsBusLocking && i < 1000; i++)
    gb.StepInstruction();
if (gb.OamDma.IsBusLocking)
    throw new InvalidOperationException("OAM transfer did not complete");
oam = Read(0xFE00, 160);
File.WriteAllBytes(args[0] + ".hud", hud.ToArray());
using (var snapshot = File.Create(args[0] + ".snapshot"))
{
    snapshot.Write(Read(0x8000, 32));
    snapshot.Write(Read(0x9800, 1024));
    snapshot.Write(Read(0x9C00, 1024));
    snapshot.Write(oam);
}
File.WriteAllBytes(args[0] + ".rgba", gb.Framebuffer.Front.ToArray());
using (var png = File.Create(args[0] + ".png"))
    gb.Framebuffer.WritePng(png);
Console.WriteLine(
    $"{bootFrames} {startupFrames} {scrollChanges} {spriteChanges} {guard.Violations.Count}"
);

sealed class ViolationTrace(GameBoySystem system, Mode3WriteGuard guard) : MemoryHook
{
    int seen;
    byte lastLine;
    ulong lastRead;

    public override void OnRead(ushort address, byte value)
    {
        if (address == 0xFF44)
        {
            lastLine = value;
            lastRead = system.Clock.SystemTicks;
        }
    }

    public override void OnWrite(ushort address, byte value)
    {
        if (guard.Violations.Count == seen)
            return;
        var violation = guard.Violations[^1];
        if (seen++ < 16)
            Console.Error.WriteLine(
                $"mode3 {system.Cartridge.CurrentRomBank} {violation.Pc:X4} {violation.Address:X4} {violation.Ly} check={lastLine} elapsed={system.Clock.SystemTicks - lastRead}"
            );
    }
}
