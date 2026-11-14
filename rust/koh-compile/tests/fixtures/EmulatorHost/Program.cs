using Koh.Emulator;
using Koh.Emulator.Cartridge;

foreach (string path in args)
{
    var gb = new GameBoySystem(CartridgeFactory.Load(File.ReadAllBytes(path)));
    for (ushort address = 0xC000; address < 0xC020; address++)
        if (!gb.Mmu.DebugWrite(address, 0))
            throw new InvalidOperationException("Cannot initialize test output");
    gb.Cpu.Registers.Pc = 0x100;
    for (ushort address = 0xC080; address < 0xC110; address++)
        gb.Mmu.DebugWrite(address, 0x5A);
    int steps = 0;
    while (gb.Mmu.DebugRead(0xC000) == 0 && steps++ < 2_000_000)
        gb.StepInstruction();
    if (gb.Mmu.DebugRead(0xC000) == 0)
        throw new InvalidOperationException(
            $"ROM did not finish: {path}, PC={gb.Cpu.Registers.Pc:X4}"
        );
    byte[] result = new byte[8];
    for (int i = 0; i < result.Length; i++)
        result[i] = gb.Mmu.DebugRead((ushort)(0xC008 + i));
    bool guard = Enumerable
        .Range(0xC080, 0x90)
        .All(address => gb.Mmu.DebugRead((ushort)address) == 0x5A);
    Console.WriteLine(
        $"{gb.Mmu.DebugRead(0xC000):X2} {gb.Mmu.DebugRead(0xC001):X2} {Convert.ToHexString(result)} {gb.Cpu.Registers.Sp:X4} {(guard ? "OK" : "CORRUPT")}"
    );
}
