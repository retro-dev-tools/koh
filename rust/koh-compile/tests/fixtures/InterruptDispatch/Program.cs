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

    [Interrupt("VBlank")]
    static void OnVBlank() => frames++;

    [Interrupt("Stat")]
    static void OnStat() => stat++;

    [Interrupt("Timer")]
    static void OnTimer() => timer++;

    [Interrupt("Serial")]
    static void OnSerial() => serial++;

    [Interrupt("Joypad")]
    static void OnJoypad() => joypad++;

    static void Calls() => Main();

    static void Nested() => Hardware.EnableInterrupts();

    static void Allocates() => current = new Doubler();

    static void RawWrite() => Hardware.IE = 0;

    static void Recursive() => OnVBlank();

    static int Deep(int depth)
    {
        int value = Deep(depth + 1);
        Hardware.Nop();
        return value + depth;
    }

    public static unsafe int Stack()
    {
        Hardware.DisableInterrupts();
        Hardware.LCDC = 0;
        *(byte*)0xFF07 = 0;
        Hardware.IF = 0;
        Hardware.IE = 4;
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
        return frames | (stat << 4) | (timer << 8) | (serial << 12) | (joypad << 16);
    }

    public static int Main() => Real(2);

    public static int Count3() => Real(3);

    public static int Count5() => Real(5);

    static int Real(byte count)
    {
        Hardware.DisableInterrupts();
        Hardware.LCDC = 0;
        Hardware.IF = 0;
        current = new Doubler();
        int sum = current.Step(7);
        current = new Tripler();
        sum += current.Step(7);
        Hardware.IE = 1;
        Hardware.LCDC = 0x91;
        Hardware.EnableInterrupts();
        while (frames < count) { }
        Hardware.DisableInterrupts();
        return (frames << 16) | (sum + current.Step(7));
    }
}
