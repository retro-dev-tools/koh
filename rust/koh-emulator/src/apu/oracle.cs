using Koh.Emulator;
using Koh.Emulator.Apu;
using Koh.Emulator.State;

foreach (var mode in new[] { HardwareMode.Dmg, HardwareMode.Cgb })
{
    var a = new Apu(mode);
    using var output = File.Create(
        Path.Combine(args[0], mode == HardwareMode.Dmg ? "dmg.bin" : "cgb.bin")
    );
    using var writer = new BinaryWriter(output);
    a.DivApuBitHighProvider = () => true;
    a.Write(0xFF26, 0x80);
    for (int i = 0; i < 16; i++)
        a.Write((ushort)(0xFF30 + i), (byte)(i * 17));
    foreach (
        var (address, value) in new (ushort, byte)[]
        {
            (0xFF10, 0x19),
            (0xFF11, 0x80),
            (0xFF12, 0xF3),
            (0xFF13, 0xF8),
            (0xFF14, 0x83),
            (0xFF16, 0xC0),
            (0xFF17, 0xA9),
            (0xFF18, 0xF8),
            (0xFF19, 0x87),
            (0xFF1A, 0x80),
            (0xFF1C, 0x20),
            (0xFF1D, 0xFF),
            (0xFF1E, 0x87),
            (0xFF21, 0xD2),
            (0xFF22, 0x08),
            (0xFF23, 0x80),
            (0xFF24, 0x77),
            (0xFF25, 0xFF),
        }
    )
        a.Write(address, value);
    for (int block = 0; block < 16; block++)
    {
        if (block == 3)
        {
            a.Write(0xFF10, 0x11);
            a.Write(0xFF1E, 0x87);
        }
        if (block == 6)
        {
            using var state = new MemoryStream();
            using (var w = new StateWriter(state))
                a.WriteState(w);
            a = new Apu(mode);
            state.Position = 0;
            using var r = new StateReader(state);
            a.ReadState(r);
        }
        if (block == 8)
            a.Write(0xFF26, 0);
        if (block == 9)
        {
            a.Write(0xFF11, 63);
            a.Write(0xFF26, 0x80);
            a.Write(0xFF12, 0xF0);
            a.Write(0xFF14, 0xC7);
        }
        if (block == 12)
        {
            a.Write(0xFF21, 0xF0);
            a.Write(0xFF22, 0);
            a.Write(0xFF23, 0x80);
        }
        for (int tick = 0; tick < 4096; tick++)
        {
            a.TickT();
            if (((block * 4096 + tick + 1) % 8192) == 0)
                a.FrameSequencer.Advance();
            if (tick == 23)
                a.Write(0xFF35, (byte)(block * 9));
        }
        using (var w = new StateWriter(output))
            a.WriteState(w);
        for (ushort address = 0xFF10; address <= 0xFF3F; address++)
            writer.Write(a.Read(address));
        var pcm = new short[a.SampleBuffer.Available];
        a.SampleBuffer.Drain(pcm);
        writer.Write(pcm.Length);
        foreach (short sample in pcm)
            writer.Write(sample);
    }
}
