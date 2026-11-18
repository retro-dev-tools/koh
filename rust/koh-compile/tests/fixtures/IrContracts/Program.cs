public static unsafe class Program
{
    static byte counter;
    static readonly byte[] table = [1, 2, 3];

    static ushort Touch(ushort value)
    {
        counter++;
        return value;
    }

    static ushort Compute(byte a, ushort b)
    {
        ushort sum = (ushort)(a + b);
        return sum > 100 ? Touch(sum) : b;
    }

    static ushort Select(ushort value) =>
        value switch
        {
            0 => 11,
            1 => 22,
            _ => 33,
        };

    public static int Main()
    {
        counter = 0;
        ushort* packet = (ushort*)0xdfe0;
        packet[0] = Compute(5, 10);
        packet[1] = Compute(250, 10);
        packet[2] = counter;
        packet[3] = table[0];
        packet[4] = table[1];
        packet[5] = table[2];
        packet[6] = Select(0);
        packet[7] = Select(1);
        packet[8] = Select(2);
        return (ushort)(nuint)packet;
    }

    static ushort PointerBits(ushort value)
    {
        byte* address = (byte*)(nuint)value;
        return (ushort)(nuint)address;
    }

    public static int Pointers()
    {
        ushort* packet = (ushort*)0xdfe0;
        packet[0] = PointerBits(0);
        packet[1] = PointerBits(1);
        packet[2] = PointerBits(0x7fff);
        packet[3] = PointerBits(0x8000);
        packet[4] = PointerBits(0xc000);
        packet[5] = PointerBits(0xffff);
        return (ushort)(nuint)packet;
    }
}
