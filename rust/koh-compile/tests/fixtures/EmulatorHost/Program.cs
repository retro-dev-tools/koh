using Koh.Emulator;
using Koh.Emulator.Cartridge;

bool cost = args.Length > 0 && args[0] == "--cost";
bool fillCost = args.Length > 0 && args[0] == "--native-heap-cost";
bool fillWrap = args.Length > 0 && args[0] == "--native-fill-wrap";
bool stack = args.Length > 0 && args[0] == "--native-stack";
bool frame = args.Length > 0 && args[0] == "--native-frame";
bool oamMode = args.Length > 0 && args[0] == "--native-oam";
bool rawOam = args.Length > 0 && args[0] == "--native-oam-trigger";
bool maps = args.Length > 0 && args[0] == "--native-maps";
bool cpu = args.Length > 0 && args[0] == "--native-cpu";
bool arena = args.Length > 0 && args[0] == "--native-arena";
bool video = frame || oamMode || rawOam || maps || (args.Length > 0 && args[0] == "--native-video");
bool heap =
    video
    || stack
    || arena
    || fillCost
    || fillWrap
    || (args.Length > 0 && args[0] == "--native-heap");
bool native =
    args.Length > 0 && (args[0] == "--native" || args[0] == "--native-poison" || heap || cpu);
ushort stop = native ? Convert.ToUInt16(args[1], 16) : (ushort)0;
foreach (
    string path in args.Skip(
        native ? 2
        : cost ? 1
        : 0
    )
)
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
    var videoGuard = new Koh.Emulator.Debug.Mode3WriteGuard(gb);
    var timing = new VideoTiming(gb, oamMode);
    var fillWrites = new FillWriteTrace();
    if (video)
    {
        gb.Mmu.OamArray.AsSpan().Fill(0xC9);
        for (ushort address = 0xFE00; address < 0xFEA0; address++)
            if (gb.Mmu.DebugRead(address) != 0xC9)
                throw new InvalidOperationException("Cannot poison OAM test memory");
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
    if (video)
        gb.Mmu.Hook = new Koh.Emulator.Debug.CompositeMemoryHook(videoGuard, timing);
    if (fillWrap)
        gb.Mmu.Hook = fillWrites;
    int steps = 0;
    int dmaInstructions = 0;
    bool? controlIme = null;
    int cpuEnables = 0;
    int cpuHalts = 0;
    bool delayedEnable = false;
    bool Finished() =>
        rawOam ? timing.OamStarts != 0
        : native ? gb.Cpu.Registers.Pc == stop
        : gb.Mmu.DebugRead(0xC000) != 0;
    while (!Finished() && steps++ < (maps ? 8_000_000 : 2_000_000))
    {
        if (oamMode && gb.OamDma.IsBusLocking)
        {
            if (gb.Cpu.Ime || gb.Cpu.Registers.Pc < 0xff80 || gb.Cpu.Registers.Pc > 0xfffe)
                throw new InvalidOperationException(
                    "DMA execution outside HRAM or with IME enabled"
                );
            dmaInstructions++;
        }
        if (cpu && gb.Cpu.Halted)
        {
            cpuHalts++;
            if (cpuHalts != 1 || gb.Cpu.Ime)
                throw new InvalidOperationException("Invalid HALT state");
            gb.Mmu.DebugWrite(0xff0f, 1);
        }
        byte opcode = cpu && !gb.Cpu.Halted ? gb.Mmu.DebugRead(gb.Cpu.Registers.Pc) : (byte)0;
        gb.StepInstruction();
        if (cpu)
        {
            if ((opcode == 0xf3 && gb.Cpu.Ime) || (delayedEnable && opcode != 0xf3 && !gb.Cpu.Ime))
                throw new InvalidOperationException("Invalid DI/EI state");
            delayedEnable = opcode == 0xfb;
            if (delayedEnable)
            {
                cpuEnables++;
                if (gb.Cpu.Ime)
                    throw new InvalidOperationException(
                        "EI enabled before the following instruction"
                    );
            }
        }
        if (cpu && controlIme is null && gb.Mmu.DebugRead(0xff43) == 1)
            controlIme = gb.Cpu.Ime;
    }
    if (oamMode && (gb.OamDma.IsBusLocking || timing.OamStarts == 0 || dmaInstructions == 0))
        throw new InvalidOperationException("OAM DMA was not observed completing");
    if (rawOam)
    {
        for (int tick = 0; tick < 700; tick++)
            gb.OamDma.TickT();
        if (gb.OamDma.IsBusLocking)
            throw new InvalidOperationException("Raw OAM DMA did not complete");
    }
    if (!Finished())
        throw new InvalidOperationException(
            $"ROM did not finish: {path}, PC={gb.Cpu.Registers.Pc:X4}, C010={gb.Mmu.DebugRead(0xC010):X2}"
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
    if (videoGuard.Violations.Count != 0)
        throw new InvalidOperationException(
            $"Mode-3 VRAM writes: {videoGuard.Violations.Count}, first={videoGuard.Violations[0]}; {string.Join("; ", timing.Writes)}"
        );
    if (timing.UnsafeDma is not null)
        throw new InvalidOperationException(timing.UnsafeDma);
    byte[] result = new byte[8];
    for (int i = 0; i < result.Length; i++)
        result[i] = gb.Mmu.DebugRead((ushort)(0xC008 + i));
    bool guard = Enumerable
        .Range(0xC080, 0x80)
        .All(address => gb.Mmu.DebugRead((ushort)address) == 0x5A);
    if (native)
    {
        string snapshot = "";
        if (cpu)
            snapshot =
                $" {(controlIme is false ? 0 : 1)}:{(gb.KeyOne.DoubleSpeed ? 1 : 0)}:{(gb.KeyOne.SwitchArmed ? 1 : 0)}:{(gb.Cpu.Stopped ? 1 : 0)}:{gb.Mmu.DebugRead(0xff4d):X2} {cpuEnables}:{cpuHalts}";
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
            byte[] oam = new byte[160];
            for (int i = 0; i < oam.Length; i++)
                oam[i] = gb.Mmu.DebugRead((ushort)(0xFE00 + i));
            snapshot += " " + Convert.ToHexString(oam);
            snapshot +=
                " "
                + Convert.ToHexString(
                    new byte[]
                    {
                        gb.Mmu.DebugRead(0xFF40),
                        gb.Mmu.DebugRead(0xFF47),
                        gb.Mmu.DebugRead(0xFF48),
                        gb.Mmu.DebugRead(0xFF49),
                    }
                );
        }
        if (oamMode || rawOam)
            snapshot += $" {timing.OamStarts}:{dmaInstructions}:{gb.Mmu.DebugRead(0xff46):X2}";
        if (arena)
            snapshot += $" {gb.Mmu.DebugRead(0xff47):X2}";
        if (fillCost)
            snapshot += $" {gb.Cpu.TotalTCycles}";
        if (fillWrap)
            snapshot += " " + string.Join(',', fillWrites.Writes);
        Console.WriteLine(
            $"{gb.Cpu.Registers.A:X2} {gb.Cpu.Registers.DE:X4}{gb.Cpu.Registers.HL:X4} {gb.Cpu.Registers.Sp:X4} {(guard ? "OK" : "CORRUPT")}{snapshot}"
        );
        continue;
    }
    Console.WriteLine(
        $"{gb.Mmu.DebugRead(0xC000):X2} {gb.Mmu.DebugRead(0xC001):X2} {Convert.ToHexString(result)} {gb.Cpu.Registers.Sp:X4} {(guard ? "OK" : "CORRUPT")}"
            + (cost ? $" {gb.Cpu.TotalTCycles}" : "")
    );
}

sealed class FillWriteTrace : Koh.Emulator.Debug.MemoryHook
{
    bool active;
    public List<string> Writes { get; } = [];

    public override void OnRead(ushort address, byte value) { }

    public override void OnWrite(ushort address, byte value)
    {
        if (address == 0xFF47)
        {
            if (value == 0x42)
                active = true;
            if (value == 0x43)
                active = false;
        }
        if (active && (address >= 0xFFFE || address <= 2))
            Writes.Add($"{address:X4}:{value:X2}");
    }
}

sealed class VideoTiming(GameBoySystem system, bool guardOam) : Koh.Emulator.Debug.MemoryHook
{
    ulong lastBlank;
    public List<string> Writes { get; } = [];
    public string? UnsafeDma { get; private set; }
    public int OamStarts { get; private set; }

    public override void OnRead(ushort address, byte value)
    {
        if (address == 0xFF44 && value == 144)
            lastBlank = system.Clock.SystemTicks;
    }

    public override void OnWrite(ushort address, byte value)
    {
        if (address == 0xff46)
        {
            if (
                guardOam
                && (
                    system.Cpu.Ime
                    || system.Cpu.Registers.Pc < 0xff80
                    || system.Cpu.Registers.Pc > 0xfffe
                )
            )
                throw new InvalidOperationException(
                    "OAM DMA trigger outside HRAM or with IME enabled"
                );
            OamStarts++;
        }
        if (address == 0xFF55 && (value & 0x80) == 0 && (system.Ppu.LCDC & 0x80) != 0)
        {
            int dots = ((value & 0x7F) + 1) * 32;
            int remaining = (154 - system.Ppu.LY) * 456 - system.Ppu.Dot;
            if (system.Ppu.LY < 144 || dots > remaining)
                UnsafeDma ??=
                    $"GDMA outside safe vblank: LY={system.Ppu.LY},dot={system.Ppu.Dot},duration={dots},after144={system.Clock.SystemTicks - lastBlank}";
        }
        if (
            address >= 0x8000
            && address < 0xA000
            && (system.Ppu.LCDC & 0x80) != 0
            && Writes.Count < 64
        )
            Writes.Add(
                $"{address:X4}@LY{system.Ppu.LY}/mode{system.Ppu.Mode}/after144={system.Clock.SystemTicks - lastBlank}"
            );
    }
}
