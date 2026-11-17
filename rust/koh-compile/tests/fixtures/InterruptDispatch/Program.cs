using Koh.GameBoy;

abstract class Counter
{
    public abstract byte Step(byte value);
}

sealed class Doubler : Counter
{
    public override byte Step(byte value) => (byte)(value * 2);
}

sealed class Tripler : Counter
{
    public override byte Step(byte value) => (byte)(value * 3);
}

public static class Program
{
    static Counter current = null!;
    static byte frames;
    static byte stat;
    static byte timer;
    static byte serial;
    static byte joypad;
    static byte wrapping;
    static ulong checksum;
    static nuint nativeProof;
    static bool bankZero;
    static uint referencedProof;

    [Interrupt("VBlank")]
    static void OnVBlank()
    {
        frames = Increment(frames);
        wrapping = Increment(wrapping);
        checksum = AddWide(checksum, 0x8000000100000002UL);
        nativeProof = AddNative((nuint)(frames + 0xFF), 0x101);
        referencedProof = SoftFloat.NegF32(0x8000002Au);
        Hardware.SCY = Advance(Hardware.SCY, 3);
        Hardware.SCX = (byte)(0xED + (Hardware.IE & 1));
        Hardware.BGP = (byte)(0xE0 | frames);
        Hardware.LYC = wrapping;
    }

    [Interrupt("Stat")]
    static void OnStat() => stat++;

    [Interrupt("Timer")]
    static void OnTimer() => timer++;

    [Interrupt("Serial")]
    static void OnSerial() => serial++;

    [Interrupt("Joypad")]
    static void OnJoypad() => joypad++;

    static byte Increment(byte value) => (byte)AddNative(value, 1);

    static nuint AddNative(nuint left, nuint right) => unchecked(left + right);

    static byte Advance(byte value, byte amount) => (byte)SharedAdd(value, amount);

    static int SharedAdd(int left, int right) => left + right;

    static ulong AddWide(ulong left, ulong right) => unchecked(left + right);

    static void Calls() => Main();

    static void Nested() => Hardware.EnableInterrupts();

    static void Allocates() => current = new Doubler();

    static unsafe void UnsafeDma() => *(byte*)0xFF46 = 0xC0;

    static unsafe void UnsafeVramBank() => *(byte*)0xFF4F = 1;

    static unsafe void UnsafeWramBank() => *(byte*)0xFF70 = 2;

    static unsafe void UnsafeSpeed() => *(byte*)0xFF4D = 1;

    static unsafe void UnsafeBoot() => *(byte*)0xFF50 = 1;

    static unsafe void UnsafeHdma() => *(byte*)0xFF55 = 0x80;

    static unsafe void UnsafeWideWrite() => *(ushort*)0xFF42 = 0x0102;

    static unsafe void UnsafeWideRead() => frames = (byte)*(ushort*)0xFF42;

    static unsafe void UnsafeRam() => *(byte*)0xC010 = 1;

    static unsafe void UnsafeRom() => *(byte*)0x2000 = 1;

    static unsafe void UnsafeHram() => *(byte*)0xFF80 = 1;

    static unsafe void UnsafeWrappedBank() => *(byte*)0x1FF4F = 1;

    static unsafe void UnsafeDynamic() => *(byte*)(0xFF00 + frames) = 1;

    static void SafeMask()
    {
        Hardware.IE = 0;
        joypad++;
    }

    static void Recursive() => OnVBlank();

    static void RecursiveHelper() => Cycle(0);

    static int Cycle(int value)
    {
        int result = Cycle(value + 1);
        Hardware.SCY++;
        return result;
    }

    static int Deep(int depth)
    {
        int value = Deep((int)AddNative((nuint)depth, 1));
        Hardware.Nop();
        return value + depth;
    }

    public static unsafe int Stack()
    {
        Hardware.DisableInterrupts();
        Hardware.LCDC = 0;
        *(byte*)0xFF07 = 0;
        Hardware.IF = 0;
        Hardware.IE = 1;
        Hardware.EnableInterrupts();
        return Deep(1);
    }

    public static int Software()
    {
        Hardware.DisableInterrupts();
        Hardware.LCDC = 0;
        Hardware.IE = 0x1F;
        Hardware.IF = 0x1F;
        Hardware.EnableInterrupts();
        while (frames == 0 || stat == 0 || timer == 0 || serial == 0 || joypad == 0) { }
        Hardware.DisableInterrupts();
        return frames
            | (stat << 4)
            | (timer << 8)
            | (serial << 12)
            | (joypad << 16)
            | ((Hardware.IE & 0x1F) << 20);
    }

    public static int Main() => Real(2);

    public static int Count3() => Real(3);

    public static int Count5() => Real(5);

    public static int BankZero()
    {
        bankZero = true;
        return Real(2);
    }

    static unsafe int Real(byte count)
    {
        Hardware.DisableInterrupts();
        Hardware.LCDC = 0;
        Hardware.IF = 0;
        Hardware.SCY = 0;
        Hardware.SCX = 0;
        Hardware.BGP = 0;
        wrapping = (byte)AddNative(0xFC, 2);
        current = new Doubler();
        int sum = current.Step(7);
        current = new Tripler();
        sum = SharedAdd(sum, current.Step(7));
        if (bankZero)
        {
            *(byte*)0x2000 = 0;
            *(byte*)0x3000 = 0;
        }
        Hardware.IE = 1;
        Hardware.LCDC = 0x91;
        Hardware.EnableInterrupts();
        while (frames < count) { }
        Hardware.DisableInterrupts();
        *(ulong*)0xC008 = AddWide(checksum, 0);
        *(ushort*)0xC020 = (ushort)nativeProof;
        *(uint*)0xC022 = referencedProof;
        return (frames << 16) | (sum + current.Step(7));
    }
}
