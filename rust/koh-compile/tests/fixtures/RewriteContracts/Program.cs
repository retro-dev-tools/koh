public static unsafe class Program
{
    private static byte IncrementOne(byte value) => (byte)(value + 1);

    public static ulong FlowCopy(int count, int other)
    {
        byte* source = (byte*)0xC100;
        byte* destination = (byte*)0xC200;
        *source = 0;
        for (byte i = 0; i < count; i++)
            source[i] = i;
        byte left = (byte)count;
        while (left != 0)
        {
            *destination++ = *source++;
            left--;
        }
        return ((ulong)(ushort)source << 32) | (ushort)destination;
    }

    public static ulong FlowCall(int count, int other)
    {
        byte* source = (byte*)0xC100;
        byte* destination = (byte*)0xC200;
        for (byte i = 0; i < count; i++)
            source[i] = i;
        byte left = (byte)count;
        while (left != 0)
        {
            *destination++ = IncrementOne(*source++);
            left--;
        }
        return ((ulong)(ushort)source << 32) | (ushort)destination;
    }

    public static int FlowFill(int count, int other)
    {
        byte* destination = (byte*)0xC100;
        byte left = (byte)count;
        while (left != 0)
        {
            *destination++ = 0xAA;
            left--;
        }
        return (ushort)destination;
    }

    public static int FlowFillExit(int count, int other)
    {
        byte* destination = (byte*)0xC100;
        byte left = (byte)count;
        while (left != 0)
        {
            *destination++ = 0xAA;
            left--;
        }
        *destination = 0xFF;
        return (ushort)destination;
    }

    public static ulong FlowOverlap(int count, int offset)
    {
        byte* source = (byte*)0xC100;
        for (int i = 0; i < 20; i++)
            source[i] = (byte)(10 * (i + 1));
        byte* destination = source + offset;
        byte left = (byte)count;
        while (left != 0)
        {
            *destination++ = *source++;
            left--;
        }
        return ((ulong)(ushort)source << 32) | (ushort)destination;
    }

    public static int FlowCounters(int count, int other)
    {
        byte first = 0;
        for (byte i = 0; i < count; i++)
            first = (byte)(first + 1);
        *(byte*)0xC100 = first;
        byte second = 0;
        for (byte i = 0; i < other; i++)
            second = (byte)(second + 1);
        *(byte*)0xC200 = second;
        return (first << 16) | second;
    }

    public static int FlowCounterChoice(int count, int pick)
    {
        *(byte*)0xC020 = (byte)pick;
        byte total = 0;
        if (*(byte*)0xC020 != 0)
        {
            for (byte i = 0; i < count; i++)
                total = (byte)(total + 1);
        }
        else
        {
            for (byte i = 0; i < count * 2; i++)
                total = (byte)(total + 1);
        }
        *(byte*)0xC300 = total;
        return total;
    }

    public static ulong FlowFills(int count, int other)
    {
        byte* first = (byte*)0xC100;
        byte left = (byte)count;
        while (left != 0)
        {
            *first++ = 0x11;
            left--;
        }
        byte* second = (byte*)0xC200;
        byte remaining = (byte)other;
        while (remaining != 0)
        {
            *second++ = 0x22;
            remaining--;
        }
        return ((ulong)(ushort)first << 32) | (ushort)second;
    }

    public static ulong FlowCopyChoice(int count, int pick)
    {
        *(byte*)0xC020 = (byte)pick;
        if (*(byte*)0xC020 != 0)
        {
            byte* source = (byte*)0xC100;
            byte* destination = (byte*)0xC110;
            for (byte i = 0; i < count; i++)
                source[i] = (byte)(0x80 + i);
            byte left = (byte)count;
            while (left != 0)
            {
                *destination++ = *source++;
                left--;
            }
            return ((ulong)(ushort)source << 32) | (ushort)destination;
        }
        else
        {
            byte* source = (byte*)0xC200;
            byte* destination = (byte*)0xC210;
            for (byte i = 0; i < count; i++)
                source[i] = (byte)(i + 1);
            byte left = (byte)count;
            while (left != 0)
            {
                *destination++ = *source++;
                left--;
            }
            return ((ulong)(ushort)source << 32) | (ushort)destination;
        }
    }

    public static ulong FlowCopies(int count, int other)
    {
        byte* firstSource = (byte*)0xC100;
        byte* firstDestination = (byte*)0xC200;
        for (byte i = 0; i < count; i++)
            firstSource[i] = i;
        byte left = (byte)count;
        while (left != 0)
        {
            *firstDestination++ = *firstSource++;
            left--;
        }
        byte* secondSource = (byte*)0xC300;
        byte* secondDestination = (byte*)0xC400;
        for (byte i = 0; i < other; i++)
            secondSource[i] = (byte)(i + 100);
        byte remaining = (byte)other;
        while (remaining != 0)
        {
            *secondDestination++ = *secondSource++;
            remaining--;
        }
        return ((ulong)(ushort)firstDestination << 32) | (ushort)secondDestination;
    }

    public static ulong FlowCopyFill(int count, int other)
    {
        byte* source = (byte*)0xC100;
        byte* destination = (byte*)0xC200;
        for (byte i = 0; i < count; i++)
            source[i] = i;
        byte left = (byte)count;
        while (left != 0)
        {
            *destination++ = *source++;
            left--;
        }
        byte* fill = (byte*)0xC300;
        byte remaining = (byte)other;
        while (remaining != 0)
        {
            *fill++ = 0x33;
            remaining--;
        }
        return ((ulong)(ushort)destination << 32) | (ushort)fill;
    }

    public static int EmptyCost()
    {
        *(byte*)0xFF81 = 1;
        *(byte*)0xFF81 = 2;
        return 0;
    }

    public static int CountedCost()
    {
        *(byte*)0xFF81 = 1;
        for (byte i = 0; i < 100; i++)
            *(byte*)0xC100 = i;
        *(byte*)0xFF81 = 2;
        return *(byte*)0xC100;
    }

    public static int CopyCost()
    {
        *(byte*)0xFF81 = 1;
        byte* source = (byte*)0xC100;
        byte* destination = (byte*)0xC200;
        for (byte i = 0; i < 32; i++)
            source[i] = i;
        byte count = 32;
        while (count != 0)
        {
            *destination = *source;
            destination++;
            source++;
            count--;
        }
        *(byte*)0xFF81 = 2;
        int result = 0;
        for (int i = 0; i < 32; i++)
            result |= ((byte*)0xC200)[i] ^ i;
        return result;
    }

    private static byte Touch(byte value) => (byte)(value + 5);

    public static int LoopExit(int count, int other)
    {
        byte i = 0;
        while (i < count)
            i = (byte)(i + 1);
        *(byte*)0xC100 = i;
        return i;
    }

    public static int LoopCall(int count, int other)
    {
        byte i = 0;
        while (i < count)
            i = (byte)(i + 1);
        byte value = Touch(i);
        *(byte*)0xC100 = value;
        return (i << 16) | value;
    }

    public static int LoopNested(int count, int inner)
    {
        byte total = 0;
        for (byte i = 0; i < count; i++)
        for (byte j = 0; j < inner; j++)
            total = (byte)(total + 1);
        *(byte*)0xC100 = total;
        return total;
    }

    public static int LoopBreak(int count, int stop)
    {
        byte i = 0;
        while (i < count)
        {
            if (i == stop)
                break;
            i = (byte)(i + 1);
        }
        *(byte*)0xC100 = i;
        return i;
    }

    public static int LoopOld(int count, int other)
    {
        byte i = 0;
        while (i < count)
        {
            byte old = i;
            i = (byte)(i + 1);
            *(byte*)0xC100 = old;
        }
        return i;
    }

    public static int LoopLast(int count, int other)
    {
        byte i = 0;
        byte last = 0;
        while (i < count)
        {
            byte old = i;
            i = (byte)(i + 1);
            last = old;
        }
        *(byte*)0xC101 = last;
        return (i << 16) | last;
    }

    private static ushort Drip(byte* dst, byte* src, ushort count, byte gate)
    {
        byte* start = dst;
        byte* end = dst + count;
        while (dst != end)
        {
            if ((byte)(gate - 144) > 8)
                return (ushort)(dst - start);
            *dst++ = *src++;
        }
        return count;
    }

    public static int GuardedCopy(int gate, int count)
    {
        for (int i = 0; i < 8; i++)
        {
            ((byte*)0xC100)[i] = (byte)(0x40 + i);
            ((byte*)0xC200)[i] = 0xEE;
        }
        return Drip((byte*)0xC200, (byte*)0xC100, (ushort)count, (byte)gate);
    }

    public static int SequentialCopy(int gate, int count)
    {
        for (int i = 0; i < 6; i++)
        {
            ((byte*)0xC100)[i] = (byte)(0x10 + i);
            ((byte*)0xC140)[i] = (byte)(0x80 + i);
        }
        ushort first = Drip((byte*)0xC200, (byte*)0xC100, 6, (byte)gate);
        ushort second = Drip((byte*)0xC220, (byte*)0xC140, 6, (byte)count);
        return first | (second << 16);
    }

    public static ulong Carry(ulong x, ulong y) => unchecked(x + y);

    public static ulong Borrow(ulong x, ulong y) => unchecked(x - y);

    public static ulong Shifts(ulong x, int count) => (x << count) ^ (x >> count);

    public static int Adjacent(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        *(byte*)0xc020 = (byte)y;
        return *(byte*)0xc020;
    }

    public static int AcrossArithmetic(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        int value = unchecked(y + 1);
        *(byte*)0xc020 = (byte)value;
        return *(byte*)0xc020;
    }

    public static int Different(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        *(byte*)0xc021 = (byte)y;
        return (*(byte*)0xc020 << 8) | *(byte*)0xc021;
    }

    public static int InterveningRead(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        int before = *(byte*)0xc020;
        *(byte*)0xc020 = (byte)y;
        return (before << 8) | *(byte*)0xc020;
    }

    public static int AliasWrite(int x, int y)
    {
        byte* pointer = (x & 1) == 0 ? (byte*)0xc020 : (byte*)0xc021;
        *(byte*)0xc020 = 0x11;
        *(byte*)0xc021 = 0x22;
        *pointer = (byte)y;
        return (*(byte*)0xc020 << 8) | *(byte*)0xc021;
    }

    public static int Hram(int x, int y)
    {
        *(byte*)0xff80 = (byte)x;
        *(byte*)0xff80 = (byte)y;
        return *(byte*)0xff80;
    }

    public static int Vram(int x, int y)
    {
        *(byte*)0xff40 = 0;
        *(byte*)0x9800 = (byte)x;
        *(byte*)0x9800 = (byte)y;
        return *(byte*)0x9800;
    }

    public static int Join(int x, int y)
    {
        *(byte*)0xc020 = 0x11;
        if (x != 0)
            *(byte*)0xc020 = (byte)y;
        return *(byte*)0xc020;
    }

    public static int ReloadAfterAlu(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        int first = *(byte*)0xc020;
        int value = unchecked(first + y);
        return (*(byte*)0xc020 << 16) | (value & 0xffff);
    }

    public static int ReadPair(int x, int y)
    {
        int first = *(byte*)0xff44;
        int second = *(byte*)0xff44;
        return x;
    }

    public static int Call(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        Change(y);
        return *(byte*)0xc020;
    }

    private static void Change(int value)
    {
        *(byte*)0xc020 = (byte)value;
    }

    public static int Tail(int x, int y) => Leaf(x);

    public static int NotTail(int x, int y) => unchecked(Leaf(x) + y);

    public static int ReturnJoin(int x, int y) => x == 0 ? Leaf(y) : unchecked(Leaf(x) + 3);

    private static int Leaf(int value) => unchecked(value + 3);

    public static int PostIncrement(int x, int y)
    {
        byte* pointer = (byte*)0xc020;
        pointer[0] = (byte)x;
        pointer[1] = (byte)y;
        int first = *pointer++;
        return (first << 8) | *pointer;
    }

    public static int IncrementEntry(int x, int y)
    {
        byte* pointer = (byte*)0xc020;
        pointer[0] = (byte)x;
        pointer[1] = (byte)y;
        int first = 0;
        if ((x & 1) != 0)
            first = *pointer;
        pointer++;
        return (first << 8) | *pointer;
    }

    public static int PostDecrement(int x, int y)
    {
        byte* pointer = (byte*)0xc021;
        pointer[-1] = (byte)x;
        pointer[0] = (byte)y;
        *pointer-- = 0x66;
        return *pointer | (pointer[1] << 8);
    }

    public static int BranchStores(int x, int y)
    {
        if (x == 0)
            *(byte*)0xc020 = (byte)y;
        else
            *(byte*)0xc020 = unchecked((byte)(y + 1));
        return *(byte*)0xc020;
    }

    public static int IndexedStore(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw;
        pointer[(short)index] = 77;
        return pointer[(short)index];
    }

    public static int IndexedLoad(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw + (short)index;
        *pointer = 200;
        return *((byte*)(ushort)raw + (short)index);
    }

    public static int IndexedTwice(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw + (short)index;
        *pointer = 9;
        return *pointer + 1;
    }

    public static ulong IndexedDelayed(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw + (short)index;
        *pointer = 41;
        int previous = *pointer;
        uint noise = unchecked((uint)Leaf(raw ^ index));
        *pointer = (byte)(previous + 1);
        return ((ulong)noise << 32) | *pointer;
    }

    public static int IndexedReuse(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw;
        pointer[(short)index] = 1;
        byte* other = pointer + (short)index;
        *other = 2;
        return *other;
    }

    private static void SeedWalk(int first)
    {
        for (int i = 0; i < 20; i++)
        {
            ((byte*)0xC100)[i] = (byte)(first + i);
            ((byte*)0xC200)[i] = (byte)(0x40 + i);
        }
    }

    public static ulong WalkChoice(int pick, int count)
    {
        SeedWalk(0x10);
        byte* pointer;
        if (pick != 0)
            pointer = (byte*)0xC100;
        else
            pointer = (byte*)0xC200;
        byte left = (byte)count;
        *(byte*)0xC300 = 0;
        while (left != 0)
        {
            *(byte*)0xC300 = *pointer;
            pointer++;
            left--;
        }
        return ((ulong)(ushort)pointer << 32) | ((ulong)left << 8) | *(byte*)0xC300;
    }

    public static ulong WalkLinear(int pick, int count)
    {
        SeedWalk(1);
        byte* pointer = (byte*)0xC100;
        byte left = (byte)count;
        *(byte*)0xC300 = 0;
        while (left != 0)
        {
            *(byte*)0xC300 = *pointer;
            pointer++;
            left--;
        }
        return ((ulong)(ushort)pointer << 32) | ((ulong)left << 8) | *(byte*)0xC300;
    }

    public static ulong WalkStore(int pick, int count)
    {
        SeedWalk(0x10);
        byte* pointer = pick != 0 ? (byte*)0xC100 : (byte*)0xC200;
        byte left = (byte)count;
        while (left != 0)
        {
            *pointer = 0xAA;
            pointer++;
            left--;
        }
        return ((ulong)(ushort)pointer << 32)
            | ((ulong)left << 8)
            | *(byte*)0xC100
            | ((ulong)*(byte*)0xC200 << 16);
    }
}
