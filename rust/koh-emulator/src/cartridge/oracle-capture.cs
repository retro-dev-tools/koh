using Koh.Emulator.Cartridge;
using Koh.Emulator.State;

using var output = new BinaryWriter(File.Create(args[0]));
output.Write(4u);
foreach (byte kind in new byte[] { 0, 3, 0x13, 0x1b })
{
    int banks = kind == 0 ? 4 : 512;
    var rom = new byte[banks * 0x4000];
    rom[0x147] = kind;
    rom[0x148] = kind == 0 ? (byte)1 : (byte)8;
    rom[0x149] = 4;
    for (int bank = 0; bank < banks; bank++)
    {
        rom[bank * 0x4000] = (byte)bank;
        rom[bank * 0x4000 + 1] = (byte)(bank >> 8);
    }
    var cart = CartridgeFactory.Load(rom);
    output.Write(kind);
    output.Write(1024u);
    ushort[] addresses =
    [
        0,
        0x1fff,
        0x2000,
        0x2fff,
        0x3000,
        0x3fff,
        0x4000,
        0x5fff,
        0x6000,
        0x7fff,
    ];
    for (int i = 0; i < 1024; i++)
    {
        ushort address = addresses[i % addresses.Length];
        byte value = (byte)(i * 37 + i / 10);
        if (kind == 0x13 && address >= 0x6000 && value == 1)
            value = 2;
        ushort ramAddress = (ushort)(0xa000 + (i * 131 % 0x2000));
        byte ramValue = (byte)(i * 19);
        output.Write(address);
        output.Write(value);
        output.Write(ramAddress);
        output.Write(ramValue);
        cart.WriteRom(address, value);
        cart.WriteRam(ramAddress, ramValue);
        foreach (ushort a in new ushort[] { 0, 1, 0x4000, 0x4001 })
            output.Write(cart.ReadRom(a));
        output.Write(cart.ReadRam(ramAddress));
        output.Write(cart.ReadRam(0xbfff));
        output.Write(cart.CurrentRomBank);
    }
    using var state = new MemoryStream();
    using (var writer = new StateWriter(state))
        cart.WriteState(writer);
    var bytes = state.ToArray();
    output.Write(bytes.Length);
    output.Write(bytes);
}
output.Write(128u);
for (int i = 0; i < 128; i++)
{
    var rtc = new Rtc
    {
        Seconds = (byte)(i * 13),
        Minutes = (byte)(i * 7),
        Hours = (byte)(i * 3),
        DayLow = (byte)(i * 17),
        DayHighAndFlags = (byte)(i * 5),
        BaseUnixSeconds = i % 11 == 0 ? 0 : 100000,
    };
    using var before = new MemoryStream();
    using (var writer = new StateWriter(before))
        rtc.WriteState(writer);
    output.Write(before.ToArray());
    long now =
        100000
        + (
            i
            % 4 switch
            {
                0 => -86400L,
                1 => 0,
                2 => 86400,
                _ => 86400L * 1025,
            }
        );
    output.Write(now);
    rtc.AdvanceFromHost(now);
    rtc.Latch();
    using var after = new MemoryStream();
    using (var writer = new StateWriter(after))
        rtc.WriteState(writer);
    output.Write(after.ToArray());
}
Console.WriteLine("Captured 4096 cartridge operations, four raw states, 128 RTC cases");
