using Koh.Emulator;
using Koh.Emulator.Cartridge;

try
{
    if (args is not ["execute", var path])
        throw new ArgumentException("Expected execute <rom>");
    var system = new GameBoySystem(
        CartridgeFactory.Load(File.ReadAllBytes(path)),
        HardwareMode.Dmg
    );
    system.Registers.Pc = 0x100;
    system.Registers.Sp = 0xFFFE;
    for (var step = 1; step <= 10000; step++)
    {
        system.StepInstruction();
        if (system.DebugReadByte(0xC000) != 42 || system.DebugReadByte(0xC001) != 43)
            continue;
        Console.WriteLine($"Executed {step} instructions; WRAM C000=2A C001=2B");
        return 0;
    }
    throw new InvalidOperationException(
        $"Execution signature absent after 10000 instructions; PC={system.Registers.Pc:X4}, "
            + $"C000={system.DebugReadByte(0xC000):X2}, C001={system.DebugReadByte(0xC001):X2}"
    );
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
