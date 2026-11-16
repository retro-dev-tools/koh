using Koh.Boot;
using Koh.Emulator;
using Koh.Emulator.Boot;
using Koh.Emulator.Cartridge;
using Koh.Emulator.Debug;
using Koh.Emulator.Joypad;

var gb = new GameBoySystem(CartridgeFactory.Load(File.ReadAllBytes(args[0])));
gb.LoadBootRom(BootRom.FromBytes(gb.Mode == HardwareMode.Cgb ? KohBootRoms.Cgb : KohBootRoms.Dmg));
ushort abort = Convert.ToUInt16(args[1], 16);
var actions = File.ReadAllLines(args[2]).Select(line => line.Split(':')).ToArray();
int last = actions.Max(action => int.Parse(action[0]));
var guard = new Mode3WriteGuard(gb);
gb.Mmu.Hook = guard;
void Frame()
{
    if (gb.RunFrame().Reason != StopReason.FrameComplete)
        throw new InvalidOperationException("Cannot complete a hardware frame");
    if (!gb.BootRomMapped && gb.Cpu.Registers.Pc >= abort && gb.Cpu.Registers.Pc < abort + 3)
        throw new InvalidOperationException(
            $"Runtime fault {gb.Cpu.Registers.A}, SP={gb.Cpu.Registers.Sp:X4}"
        );
}
int boot = 0;
while (gb.BootRomMapped && boot++ < 600)
    Frame();
if (gb.BootRomMapped)
    throw new InvalidOperationException("Boot did not complete");
for (int frame = 0; frame <= last; frame++)
{
    var current = actions.Where(action => int.Parse(action[0]) == frame).ToArray();
    foreach (var action in current.Where(action => action[1] != "dump"))
    {
        if (action[1] == "write")
        {
            ushort address = Convert.ToUInt16(action[2], 16);
            var bytes = Convert.FromHexString(action[3]);
            for (int index = 0; index < bytes.Length; index++)
                gb.Mmu.DebugWrite(checked((ushort)(address + index)), bytes[index]);
            continue;
        }
        var button = Enum.Parse<JoypadButton>(action[2]);
        if (action[1] == "press")
            gb.JoypadPress(button);
        else if (action[1] == "release")
            gb.JoypadRelease(button);
        else
            throw new InvalidOperationException("Unknown input action");
    }
    Frame();
    foreach (var action in current.Where(action => action[1] == "dump"))
    {
        string path = args[0] + "." + action[2];
        byte[] memory = new byte[0x2000];
        for (int index = 0; index < 0x1000; index++)
            memory[index] = gb.Mmu.DebugRead((ushort)(0xC000 + index));
        byte bank = gb.Mmu.DebugRead(0xFF70);
        gb.Mmu.DebugWrite(0xFF70, 1);
        for (int index = 0; index < 0x1000; index++)
            memory[0x1000 + index] = gb.Mmu.DebugRead((ushort)(0xD000 + index));
        gb.Mmu.DebugWrite(0xFF70, bank);
        File.WriteAllBytes(path + ".wram", memory);
        File.WriteAllBytes(path + ".rgba", gb.Framebuffer.Front.ToArray());
        File.WriteAllBytes(path + ".vram", gb.Mmu.VramArray);
        File.WriteAllBytes(path + ".palette", gb.Ppu.BgPalette.RawData.ToArray());
        using var png = File.Create(path + ".png");
        gb.Framebuffer.WritePng(png);
    }
}
Console.WriteLine($"{boot} {last + 1} {guard.Violations.Count} {gb.Mode}");
