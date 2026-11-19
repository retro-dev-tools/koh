using Koh.Emulator;
using Koh.Emulator.Cpu;
using Koh.Emulator.Ppu;
using Koh.Emulator.State;

Directory.CreateDirectory(args[0]);
foreach (var mode in new[] { HardwareMode.Dmg, HardwareMode.Cgb })
foreach (byte opri in new byte[] { 0, 1 })
{
    var vram = new byte[0x4000];
    var oam = new byte[0xa0];
    for (int i = 0; i < vram.Length; i++)
        vram[i] = (byte)((i * 73 + (i >> 3) * 19 + 0x35) ^ (i >> 7));
    for (int i = 0; i < 40; i++)
    {
        oam[i * 4] = (byte)(16 + i % 10 * 13);
        oam[i * 4 + 1] = (byte)(4 + i * 19 % 164);
        oam[i * 4 + 2] = (byte)(i * 7);
        oam[i * 4 + 3] = (byte)(i * 29);
    }
    var ppu = new Ppu(mode, vram, oam)
    {
        LCDC = 0xf7,
        BGP = 0xe4,
        OBP0 = 0xd8,
        OBP1 = 0x6c,
        SCX = 3,
        SCY = 13,
        WX = 54,
        WY = 5,
        OPRI = opri,
    };
    ppu.Stat.Write(0x78);
    for (int palette = 0; palette < 8; palette++)
    for (int color = 0; color < 4; color++)
    {
        ppu.BgPalette.SetColor(palette, color, (ushort)((palette * 3751 + color * 9163) & 0x7fff));
        ppu.ObjPalette.SetColor(palette, color, (ushort)((palette * 2129 + color * 7391) & 0x7fff));
    }
    var interrupts = new Interrupts();
    int hblanks = 0;
    ppu.HBlankEntered += () => hblanks++;
    using var output = new BinaryWriter(File.Create(Path.Combine(args[0], $"{mode}-{opri}.bin")));
    int previous = 0;
    foreach (int target in new[] { 0, 79, 80, 81, 107, 128, 247, 455, 456, 1000, 65664, 70224 })
    {
        for (int dot = previous; dot < target; dot++)
            ppu.TickDot(ref interrupts);
        previous = target;
        output.Write(target);
        output.Write(interrupts.IF);
        output.Write(hblanks);
        using var state = new MemoryStream();
        using (var writer = new StateWriter(state))
            ppu.WriteState(writer);
        output.Write(state.ToArray());
    }
    output.Write(ppu.Framebuffer.Front);
    output.Write(ppu.Framebuffer.Back);
}
