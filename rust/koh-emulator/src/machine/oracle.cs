using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Emulator.Debug;
using Koh.Emulator.Ppu;
using Koh.Emulator.State;

Directory.CreateDirectory(args[0]);
foreach (
    var (mode, doubleSpeed) in new[]
    {
        (HardwareMode.Dmg, false),
        (HardwareMode.Cgb, false),
        (HardwareMode.Cgb, true),
    }
)
{
    var code = new List<byte>();
    void Emit(params byte[] bytes) => code.AddRange(bytes);
    void Io(byte address, byte value) => Emit(0x3e, value, 0xe0, address);
    void Store(ushort address, byte value) =>
        Emit(0x3e, value, 0xea, (byte)address, (byte)(address >> 8));
    Emit(0x31, 0xfe, 0xff);
    Io(0x40, 0);
    for (int i = 0; i < 128; i++)
        Store((ushort)(0x8000 + i), (byte)(i * 73 + 0x35));
    for (int i = 0; i < 32; i++)
        Store((ushort)(0x9800 + i), (byte)(i & 7));
    Io(0x4f, 1);
    for (int i = 0; i < 128; i++)
        Store((ushort)(0x8000 + i), (byte)(i * 29 + 0x63));
    for (int i = 0; i < 32; i++)
        Store((ushort)(0x9800 + i), (byte)(i * 17));
    Io(0x4f, 0);
    for (int i = 0; i < 160; i++)
        Store(
            (ushort)(0xc000 + i),
            (byte)(
                (i % 4) switch
                {
                    0 => 16 + i / 16 * 8,
                    1 => 8 + i / 4 * 7 % 160,
                    2 => i / 4 % 8,
                    _ => i * 13,
                }
            )
        );
    for (int i = 0; i < 64; i++)
        Store((ushort)(0xc100 + i), (byte)(i * 31));
    Io(0x51, 0xc1);
    Io(0x52, 0);
    Io(0x53, 0x80);
    Io(0x54, 0x80);
    Io(0x55, 1);
    Io(0x68, 0x80);
    Io(0x6a, 0x80);
    for (int i = 0; i < 64; i++)
    {
        Io(0x69, (byte)(i * 23));
        Io(0x6b, (byte)(i * 41));
    }
    Io(0x26, 0x80);
    Io(0x24, 0x77);
    Io(0x25, 0xff);
    foreach (
        var (address, value) in new (byte, byte)[]
        {
            (0x10, 0x16),
            (0x11, 0x80),
            (0x12, 0xf3),
            (0x13, 0xc0),
            (0x14, 0x87),
            (0x16, 0x40),
            (0x17, 0xa2),
            (0x18, 0x80),
            (0x19, 0x87),
            (0x1a, 0x80),
            (0x1c, 0x20),
            (0x1d, 0xd0),
            (0x1e, 0x87),
            (0x21, 0x92),
            (0x22, 0x35),
            (0x23, 0x80),
        }
    )
        Io(address, value);
    for (int i = 0; i < 16; i++)
        Io((byte)(0x30 + i), (byte)(i * 17));
    Io(0x04, 0);
    Io(0x05, 0xfd);
    Io(0x06, 0x42);
    Io(0x07, 5);
    Io(0x01, 0x4b);
    Io(0x02, 0x81);
    if (doubleSpeed)
    {
        Io(0x4d, 1);
        Emit(0x10, 0);
    }
    int returnPc = 0x150 + code.Count + 12 * 5 + 3;
    byte[] hram =
    {
        0x3e,
        0xc0,
        0xe0,
        0x46,
        0x06,
        40,
        0x05,
        0x20,
        0xfd,
        0xc3,
        (byte)returnPc,
        (byte)(returnPc >> 8),
    };
    for (int i = 0; i < hram.Length; i++)
        Store((ushort)(0xff80 + i), hram[i]);
    Emit(0xc3, 0x80, 0xff);
    Io(0x47, 0xe4);
    Io(0x48, 0xd8);
    Io(0x49, 0x6c);
    Io(0x43, 3);
    Io(0x42, 2);
    Io(0x4a, 5);
    Io(0x4b, 54);
    Io(0x41, 0x78);
    Io(0x40, 0xf7);
    Io(0x51, 0xc1);
    Io(0x52, 0);
    Io(0x53, 0x80);
    Io(0x54, 0xc0);
    Io(0x55, 0x82);
    int loop = 0x150 + code.Count;
    Emit(
        0xfa,
        0x00,
        0xc1,
        0x3c,
        0xea,
        0x00,
        0xc1,
        0xea,
        0x00,
        0x90,
        0xc3,
        (byte)loop,
        (byte)(loop >> 8)
    );
    byte[] rom = new byte[0x8000];
    rom[0] = 0xc3;
    rom[1] = 0x50;
    rom[2] = 1;
    rom[0x143] = mode == HardwareMode.Cgb ? (byte)0x80 : (byte)0;
    code.CopyTo(rom, 0x150);
    string name = $"{mode}-{(doubleSpeed ? 1 : 0)}";
    File.WriteAllBytes(Path.Combine(args[0], name + ".gb"), rom);
    var gb = new GameBoySystem(CartridgeFactory.Load(rom), mode);
    var hook = new TraceHook(gb);
    gb.Mmu.Hook = hook;
    var hblanks = new List<(ulong Ticks, byte Ly, int Dot, byte Scx)>();
    gb.Ppu.HBlankEntered += () =>
        hblanks.Add((gb.Clock.SystemTicks, gb.Ppu.LY, gb.Ppu.Dot, gb.Ppu.SCX));
    using var output = new BinaryWriter(File.Create(Path.Combine(args[0], name + ".bin")));
    void Capture()
    {
        using var state = new MemoryStream();
        using (var writer = new StateWriter(state))
            gb.WriteState(writer);
        output.Write((int)state.Length);
        output.Write(state.ToArray());
    }
    int previous = 0;
    foreach (int target in new[] { 0, 1, 10, 100, 1000, 10000 })
    {
        for (int i = previous; i < target; i++)
            gb.StepInstruction();
        previous = target;
        Capture();
    }
    gb.RunFrame();
    Capture();
    gb.RunFrame();
    Capture();
    output.Write(gb.Framebuffer.Front);
    output.Write(gb.Framebuffer.Back);
    short[] pcm = new short[8192];
    int count = gb.Apu.SampleBuffer.Drain(pcm);
    output.Write(count);
    for (int i = 0; i < count; i++)
        output.Write(pcm[i]);
    output.Write(hook.Events.Count);
    foreach (var e in hook.Events)
    {
        output.Write(e.Address);
        output.Write(e.Value);
        output.Write(e.Pc);
        output.Write(e.Ly);
        output.Write(e.Dot);
        output.Write(e.Mode);
        output.Write(e.Ime);
        output.Write(e.TCycles);
        output.Write(e.SystemTicks);
        output.Write(e.Lcdc);
        output.Write(e.Scx);
        output.Write(e.BootMapped);
        output.Write(e.RomBank);
    }
    output.Write(hblanks.Count);
    foreach (var h in hblanks)
    {
        output.Write(h.Ticks);
        output.Write(h.Ly);
        output.Write(h.Dot);
        output.Write(h.Scx);
    }
    Console.WriteLine(
        $"{name}: {gb.Clock.SystemTicks} dots, {gb.Cpu.TotalTCycles} T-cycles, {count} samples, PC={gb.Registers.Pc:X4}"
    );
}

sealed class TraceHook(GameBoySystem gb) : MemoryHook
{
    public readonly List<(
        ushort Address,
        byte Value,
        ushort Pc,
        byte Ly,
        int Dot,
        int Mode,
        bool Ime,
        ulong TCycles,
        ulong SystemTicks,
        byte Lcdc,
        byte Scx,
        bool BootMapped,
        byte RomBank
    )> Events = new();

    public override void OnRead(ushort address, byte value) { }

    public override void OnWrite(ushort address, byte value)
    {
        if (
            address >= 0x8000
            && address < 0xa000
            && gb.Ppu.Mode == PpuMode.Drawing
            && (gb.Ppu.LCDC & 0x80) != 0
        )
            Events.Add(
                (
                    address,
                    value,
                    gb.Registers.Pc,
                    gb.Ppu.LY,
                    gb.Ppu.Dot,
                    (int)gb.Ppu.Mode,
                    gb.Cpu.Ime,
                    gb.Cpu.TotalTCycles,
                    gb.Clock.SystemTicks,
                    gb.Ppu.LCDC,
                    gb.Ppu.SCX,
                    gb.BootRomMapped,
                    gb.Cartridge.CurrentRomBank
                )
            );
    }
}
