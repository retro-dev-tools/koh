using Koh.Emulator;
using Koh.Emulator.Cartridge;

bool native = args.Length > 0 && args[0] == "--native";
ushort stop = native ? Convert.ToUInt16(args[1], 16) : (ushort)0;
foreach (string path in args.Skip(native ? 2 : 0))
{
    var gb = new GameBoySystem(CartridgeFactory.Load(File.ReadAllBytes(path)));
    for (ushort address = 0xC000; address < 0xC020; address++)
        if (!gb.Mmu.DebugWrite(address, 0))
            throw new InvalidOperationException("Cannot initialize test output");
    gb.Cpu.Registers.Pc = 0x100;
    for (ushort address = 0xC080; address < 0xC100; address++)
        gb.Mmu.DebugWrite(address, 0x5A);
    int steps = 0;
    bool Finished() => native ? gb.Cpu.Registers.Pc == stop : gb.Mmu.DebugRead(0xC000) != 0;
    while (!Finished() && steps++ < 2_000_000)
        gb.StepInstruction();
    if (!Finished())
        throw new InvalidOperationException(
            $"ROM did not finish: {path}, PC={gb.Cpu.Registers.Pc:X4}"
        );
    byte[] result = new byte[8];
    for (int i = 0; i < result.Length; i++)
        result[i] = gb.Mmu.DebugRead((ushort)(0xC008 + i));
    bool guard = Enumerable
        .Range(0xC080, 0x80)
        .All(address => gb.Mmu.DebugRead((ushort)address) == 0x5A);
    if (native)
    {
        Console.WriteLine(
            $"{gb.Cpu.Registers.A:X2} {gb.Cpu.Registers.DE:X4}{gb.Cpu.Registers.HL:X4} {gb.Cpu.Registers.Sp:X4} {(guard ? "OK" : "CORRUPT")}"
        );
        continue;
    }
    Console.WriteLine(
        $"{gb.Mmu.DebugRead(0xC000):X2} {gb.Mmu.DebugRead(0xC001):X2} {Convert.ToHexString(result)} {gb.Cpu.Registers.Sp:X4} {(guard ? "OK" : "CORRUPT")}"
    );
}
