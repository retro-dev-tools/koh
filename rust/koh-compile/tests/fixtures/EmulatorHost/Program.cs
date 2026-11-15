using Koh.Emulator;
using Koh.Emulator.Cartridge;

bool stack = args.Length > 0 && args[0] == "--native-stack";
bool frame = args.Length > 0 && args[0] == "--native-frame";
bool video = frame || (args.Length > 0 && args[0] == "--native-video");
bool heap = video || stack || (args.Length > 0 && args[0] == "--native-heap");
bool native = args.Length > 0 && (args[0] == "--native" || args[0] == "--native-poison" || heap);
ushort stop = native ? Convert.ToUInt16(args[1], 16) : (ushort)0;
foreach (string path in args.Skip(native ? 2 : 0))
{
    byte[] rom = File.ReadAllBytes(path);
    bool color = (rom[0x143] & 0x80) != 0;
    var gb = new GameBoySystem(CartridgeFactory.Load(rom));
    string dmaBefore = Convert.ToHexString(
        new byte[] { gb.Hdma.Source1, gb.Hdma.Source2, gb.Hdma.Dest1, gb.Hdma.Dest2 }
    );
    for (ushort address = 0xC000; address < 0xC020; address++)
        if (!gb.Mmu.DebugWrite(address, 0))
            throw new InvalidOperationException("Cannot initialize test output");
    gb.Cpu.Registers.Pc = 0x100;
    if (video)
    {
        foreach (byte bank in color ? new byte[] { 0, 1 } : new byte[] { 0 })
        {
            gb.Mmu.DebugWrite(0xFF4F, bank);
            byte poison = bank == 0 ? (byte)0xA7 : (byte)0xB8;
            for (ushort address = 0x8000; address < 0xA000; address++)
                if (!gb.Mmu.DebugWrite(address, poison) || gb.Mmu.DebugRead(address) != poison)
                    throw new InvalidOperationException("Cannot poison video test memory");
        }
        gb.Mmu.DebugWrite(0xFF4F, 0);
    }
    if (stack)
        for (ushort address = 0xC100; address < 0xD000; address++)
            if (!gb.Mmu.DebugWrite(address, 0xA6) || gb.Mmu.DebugRead(address) != 0xA6)
                throw new InvalidOperationException("Cannot poison stack test memory");
    if (heap || (args.Length > 0 && args[0] == "--native-poison"))
        for (ushort address = 0xC000; address < 0xC080; address++)
            if (!gb.Mmu.DebugWrite(address, 0xA5) || gb.Mmu.DebugRead(address) != 0xA5)
                throw new InvalidOperationException("Cannot poison static test memory");
    if (heap)
        foreach (byte bank in color ? new byte[] { 2, 1 } : new byte[] { 1 })
        {
            gb.Mmu.DebugWrite(0xFF70, bank);
            byte poison = bank == 1 ? (byte)0x5A : (byte)0x6B;
            for (ushort address = 0xD000; address < 0xE000; address++)
                if (!gb.Mmu.DebugWrite(address, poison) || gb.Mmu.DebugRead(address) != poison)
                    throw new InvalidOperationException("Cannot poison heap test memory");
        }
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
    if (frame)
    {
        for (int i = 0; i < 2; i++)
            if (gb.RunFrame().Reason != StopReason.FrameComplete)
                throw new InvalidOperationException("Cannot complete video test frame");
        File.WriteAllBytes(Path.ChangeExtension(path, ".rgba"), gb.Framebuffer.Front.ToArray());
        using var capture = File.Create(Path.ChangeExtension(path, ".png"));
        gb.Framebuffer.WritePng(capture);
    }
    byte[] result = new byte[8];
    for (int i = 0; i < result.Length; i++)
        result[i] = gb.Mmu.DebugRead((ushort)(0xC008 + i));
    bool guard = Enumerable
        .Range(0xC080, 0x80)
        .All(address => gb.Mmu.DebugRead((ushort)address) == 0x5A);
    if (native)
    {
        string snapshot = "";
        if (heap)
        {
            byte saved = gb.Mmu.DebugRead(0xFF70);
            foreach (byte bank in color ? new byte[] { 1, 2 } : new byte[] { 1 })
            {
                gb.Mmu.DebugWrite(0xFF70, bank);
                byte[] memory = new byte[0x1000];
                for (int i = 0; i < memory.Length; i++)
                    memory[i] = gb.Mmu.DebugRead((ushort)(0xD000 + i));
                snapshot += " " + Convert.ToHexString(memory);
            }
            gb.Mmu.DebugWrite(0xFF70, saved);
        }
        if (video)
        {
            byte saved = gb.Mmu.DebugRead(0xFF4F);
            foreach (byte bank in color ? new byte[] { 0, 1 } : new byte[] { 0 })
            {
                gb.Mmu.DebugWrite(0xFF4F, bank);
                byte[] memory = new byte[0x2000];
                for (int i = 0; i < memory.Length; i++)
                    memory[i] = gb.Mmu.DebugRead((ushort)(0x8000 + i));
                snapshot += " " + Convert.ToHexString(memory);
            }
            gb.Mmu.DebugWrite(0xFF4F, saved);
            byte[] dma =
            {
                gb.Hdma.Source1,
                gb.Hdma.Source2,
                gb.Hdma.Dest1,
                gb.Hdma.Dest2,
                (byte)(gb.Hdma.Active ? 1 : 0),
                (byte)(gb.Hdma.IsHBlankMode ? 1 : 0),
                (byte)(gb.Hdma.CpuHaltedByGp ? 1 : 0),
            };
            snapshot += " " + dmaBefore + ":" + Convert.ToHexString(dma);
        }
        Console.WriteLine(
            $"{gb.Cpu.Registers.A:X2} {gb.Cpu.Registers.DE:X4}{gb.Cpu.Registers.HL:X4} {gb.Cpu.Registers.Sp:X4} {(guard ? "OK" : "CORRUPT")}{snapshot}"
        );
        continue;
    }
    Console.WriteLine(
        $"{gb.Mmu.DebugRead(0xC000):X2} {gb.Mmu.DebugRead(0xC001):X2} {Convert.ToHexString(result)} {gb.Cpu.Registers.Sp:X4} {(guard ? "OK" : "CORRUPT")}"
    );
}
