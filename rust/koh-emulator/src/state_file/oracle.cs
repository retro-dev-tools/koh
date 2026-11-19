using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Emulator.State;

var rom = new byte[32768];
var machine = new GameBoySystem(CartridgeFactory.Load(rom));
machine.Registers.Pc = 0x100;
machine.Registers.Sp = 0xdffe;
machine.DebugWriteByte(0xc123, 0x42);
machine.DebugWriteByte(0xff05, 0x67);
for (int i = 0; i < 23; i++)
    machine.StepInstruction();
using var output = File.Create(args[0]);
SaveStateFile.Save(output, machine, rom);
