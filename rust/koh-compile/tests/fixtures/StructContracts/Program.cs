using Koh.GameBoy;

public struct Point
{
    public byte X;
    public byte Y;
}

public enum Level : byte
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public static unsafe class Program
{
    private static int Finish(byte value)
    {
        Hardware.BGP = value;
        return value;
    }

    public static int Copy()
    {
        Lcd.Off();
        Point a;
        a.X = 10;
        a.Y = 20;
        Point b = a;
        b.X += 5;
        byte* p = (byte*)0xDF00;
        p[0] = a.X;
        p[1] = a.Y;
        p[2] = b.X;
        p[3] = b.Y;
        return Finish((byte)(a.X + a.Y + b.X + b.Y));
    }

    public static int Pointer()
    {
        Lcd.Off();
        byte* vram = Gb.Vram;
        *vram = 42;
        return Finish(*vram);
    }

    public static int Offsets()
    {
        Lcd.Off();
        byte* vram = Gb.Vram;
        byte* p = vram + 3;
        *p = 0x11;
        *(p + 1) = 0x22;
        return Finish((byte)(vram[3] + vram[4]));
    }

    public static int Stack()
    {
        Lcd.Off();
        byte* p = stackalloc byte[4];
        p[0] = 1;
        p[1] = 2;
        p[2] = 3;
        p[3] = 4;
        for (int i = 0; i < 4; i++)
            ((byte*)0xDF00)[i] = p[i];
        return Finish((byte)(p[0] + p[1] + p[2] + p[3]));
    }

    private static void Increment(ref byte value) => value++;

    private static void Move(ref Point point, byte dx) => point.X += dx;

    private static byte MoveCopy(Point point, byte dx)
    {
        point.X += dx;
        return (byte)(point.X + point.Y);
    }

    public static int RefByte()
    {
        Lcd.Off();
        byte value = 41;
        Increment(ref value);
        *(byte*)0xDF00 = value;
        return Finish(value);
    }

    public static int RefStruct()
    {
        Lcd.Off();
        Point a;
        a.X = 10;
        a.Y = 20;
        Move(ref a, 7);
        ((byte*)0xDF00)[0] = a.X;
        ((byte*)0xDF00)[1] = a.Y;
        return Finish((byte)(a.X + a.Y));
    }

    public static int ByValue()
    {
        Lcd.Off();
        Point a;
        a.X = 10;
        a.Y = 20;
        byte changed = MoveCopy(a, 7);
        byte* p = (byte*)0xDF00;
        p[0] = a.X;
        p[1] = a.Y;
        p[2] = changed;
        return Finish((byte)(a.X + a.Y));
    }

    public static byte Dispatch(int selector, bool special)
    {
        switch (selector)
        {
            case 0:
                return 10;
            case 1:
                return 11;
            case 2:
                return 12;
            case 3:
                return 13;
            case 4:
                return 14;
            case 5:
                return 15;
            case 6:
                return special ? (byte)66 : (byte)16;
            case 7:
                return 17;
            case 8:
                return 18;
            case 9:
                return 19;
            default:
                return special ? (byte)99 : (byte)77;
        }
    }

    public static int Selected()
    {
        Lcd.Off();
        Hardware.SCX = 6;
        return Finish(Dispatch(Hardware.SCX, true));
    }

    public static int Default()
    {
        Lcd.Off();
        Hardware.SCX = 42;
        return Finish(Dispatch(Hardware.SCX, false));
    }

    public static int SwitchCoverage()
    {
        Lcd.Off();
        byte* p = (byte*)0xDF00;
        for (int selector = -1; selector <= 10; selector++)
        {
            p[selector + 1] = Dispatch(selector, true);
            p[selector + 17] = Dispatch(selector, false);
        }
        p[12] = Dispatch(int.MinValue, true);
        p[13] = Dispatch(int.MaxValue, true);
        p[28] = Dispatch(int.MinValue, false);
        p[29] = Dispatch(int.MaxValue, false);
        return Finish(42);
    }

    public static int Enum()
    {
        Lcd.Off();
        Level value = Level.Medium;
        return Finish(value == Level.Medium ? (byte)200 : (byte)0);
    }
}
