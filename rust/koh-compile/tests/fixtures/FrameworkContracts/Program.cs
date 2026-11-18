using Koh.GameBoy;
using Koh.GameBoy.Framework;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    private static Timer timer;
    private static TileAsset tiles;
    private static MapAsset map;
    private static readonly byte[] art =
    {
        0x11,
        0x22,
        0x33,
        0x44,
        0x55,
        0x66,
        0x77,
        0x88,
        0x99,
        0xAA,
        0xBB,
        0xCC,
        0xDD,
        0xEE,
        0x0F,
        0xF0,
        0xA0,
        0xA1,
        0xA2,
        0xA3,
        0xA4,
        0xA5,
        0xA6,
        0xA7,
        0xA8,
        0xA9,
        0xAA,
        0xAB,
        0xAC,
        0xAD,
        0xAE,
        0xAF,
    };
    private static readonly byte[] cells = { 1, 2, 2, 1 };

    public static int Random()
    {
        byte* p = (byte*)0xDF00;
        Rng.Seed(0x1234);
        for (int i = 0; i < 8; i++)
            p[i] = Rng.Next();
        Rng.Seed(0x1234);
        p[8] = Rng.Next();
        Rng.Next();
        Rng.Next();
        p[9] = Rng.Next(10);
        Rng.Seed(0);
        ushort nonzero = Rng.Next16();
        p[10] = (byte)nonzero;
        p[11] = (byte)(nonzero >> 8);
        Rng.Seed(0x1234);
        Rng.Mix(0x5A);
        p[12] = Rng.Next();
        p[13] = Rng.Chance(0) ? (byte)1 : (byte)0;
        p[14] = Rng.Chance(255) ? (byte)1 : (byte)0;
        p[15] = Rng.Next(0);
        ushort last = Rng.Next16();
        p[16] = (byte)last;
        p[17] = (byte)(last >> 8);
        return 42;
    }

    public static int TimerContract()
    {
        byte* p = (byte*)0xDF00;
        p[0] = timer.Running ? (byte)1 : (byte)0;
        p[1] = timer.Tick() ? (byte)1 : (byte)0;
        timer.Start(3);
        p[2] = timer.Running ? (byte)1 : (byte)0;
        p[3] = timer.Tick() ? (byte)1 : (byte)0;
        p[4] = timer.Tick() ? (byte)1 : (byte)0;
        p[5] = timer.Tick() ? (byte)1 : (byte)0;
        p[6] = timer.Running ? (byte)1 : (byte)0;
        p[7] = timer.Tick() ? (byte)1 : (byte)0;
        timer.Start(1);
        p[8] = timer.Tick() ? (byte)1 : (byte)0;
        timer.Start(100);
        timer.Stop();
        p[9] = timer.Running ? (byte)1 : (byte)0;
        p[10] = timer.Tick() ? (byte)1 : (byte)0;
        timer.Start(0);
        p[11] = timer.Running ? (byte)1 : (byte)0;
        p[12] = timer.Tick() ? (byte)1 : (byte)0;
        return 42;
    }

    public static int Assets()
    {
        Game.Boot();
        tiles = TileAsset.Define(art);
        tiles.Load(4);
        map = MapAsset.Define(cells, 2, 2);
        map.Draw(3, 5);
        byte* p = (byte*)0xDF00;
        p[0] = tiles.TileCount;
        p[1] = tiles.BaseTile;
        p[2] = tiles.Tile(0);
        p[3] = tiles.Tile(1);
        p[4] = map.Width;
        p[5] = map.Height;
        Video.Start();
        Game.EndFrame();
        return 42;
    }

    public static int InputContract()
    {
        byte* p = (byte*)0xDF00;
        Input.Update();
        p[0] = Input.Pressed(Button.Right) ? (byte)1 : (byte)0;
        p[1] = Input.Held(Button.Right) ? (byte)1 : (byte)0;
        p[2] = Input.Pressed(Button.Right) ? (byte)1 : (byte)0;
        p[3] = Input.Released(Button.Right) ? (byte)1 : (byte)0;
        p[4] = (byte)Input.DpadX();
        Input.Update();
        p[5] = Input.Pressed(Button.Right) ? (byte)1 : (byte)0;
        p[6] = Input.Held(Button.Right) ? (byte)1 : (byte)0;
        Hardware.SCX = 1;
        Input.Update();
        p[7] = Input.Released(Button.Right) ? (byte)1 : (byte)0;
        p[8] = Input.Held(Button.Right) ? (byte)1 : (byte)0;
        p[9] = (byte)Input.DpadX();
        Hardware.SCX = 2;
        Input.SetRepeat(3, 2);
        Input.Update();
        p[10] = (byte)Input.DpadX();
        p[11] = Input.Repeated(Button.Left) ? (byte)1 : (byte)0;
        for (int i = 0; i < 5; i++)
        {
            Input.Update();
            p[12 + i] = Input.Repeated(Button.Left) ? (byte)1 : (byte)0;
        }
        return 42;
    }

    public static int GameContract()
    {
        Game.Boot();
        byte* p = (byte*)0xDF00;
        p[0] = (byte)Clock.Frames;
        p[1] = Rng.Next16() != 0 ? (byte)1 : (byte)0;
        Video.Start();
        Game.EndFrame();
        Game.EndFrame();
        Game.EndFrame();
        p[2] = (byte)Clock.Frames;
        p[3] = Input.Held(Button.A) ? (byte)1 : (byte)0;
        return 42;
    }
}

public static unsafe class ReferenceRules
{
    public static int BclMax() => System.Math.Max(3, 5);

    public static int BclNative() => (int)System.Math.Sqrt(4);

    public static int BclMaxEdges()
    {
        int* packet = (int*)0xDF00;
        packet[0] = System.Math.Max(-5, -3);
        packet[1] = System.Math.Max(int.MinValue, int.MaxValue);
        packet[2] = System.Math.Max(int.MaxValue, int.MinValue);
        packet[3] = System.Math.Max(0, 0);
        packet[4] = System.Math.Max(-1, 0);
        return 42;
    }

    public static int Memory()
    {
        Lcd.SetPalette(0x1B);
        byte* source = stackalloc byte[8];
        byte* target = stackalloc byte[8];
        byte* fill = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
            source[i] = (byte)(i + 1);
        Mem.Copy(target, source, 8);
        Mem.Fill(fill, 0x7A, 8);
        byte* packet = (byte*)0xDF00;
        for (int i = 0; i < 8; i++)
        {
            packet[i] = target[i];
            packet[8 + i] = fill[i];
        }
        packet[16] = Hardware.BGP;
        return 42;
    }

    public static int SingleCell()
    {
        Lcd.Off();
        Tilemap.Clear(0);
        Tilemap.SetTile(5, 3, 9);
        return 42;
    }

    public static int Clear()
    {
        Lcd.Off();
        Tilemap.Clear(7);
        return 42;
    }

    public static void Unreached() => System.Console.WriteLine("unreached");
}

public static unsafe class StringTables
{
    private static readonly string[] lines = { "HELLO", "WORLD!", "GB" };

    private static byte First(string[] values, int i) => (byte)values[i][0];

    private static int Count(string[] values) => values.Length;

    public static int Values()
    {
        byte* p = (byte*)0xDF00;
        p[0] = (byte)Count(lines);
        p[1] = First(lines, 0);
        p[2] = First(lines, 1);
        p[3] = First(lines, 2);
        p[4] = (byte)lines[1].Length;
        string[] local = new string[2];
        local[0] = "AB";
        local[1] = "CDE";
        p[5] = (byte)local[0].Length;
        p[6] = First(local, 1);
        p[7] = (byte)local[1].Length;
        string[] alias = lines;
        alias[1] = "Z";
        p[8] = First(lines, 1);
        p[9] = (byte)lines[1].Length;
        return 42;
    }

    public static int NullElement() => First(new string[1], 0);

    public static int EndIndex() => First(lines, 3);

    public static int NegativeIndex() => First(lines, -1);
}

public static class IteratorRules
{
    private static System.Collections.Generic.IEnumerable<byte> Steps(byte n)
    {
        for (byte i = 0; i < n; i++)
            yield return i;
    }

    private static bool IsNull(System.IDisposable value) => value is null;

    public static int InterfaceNull() => IsNull(null) ? 42 : 0;

    public static int Empty()
    {
        int sum = 0;
        foreach (byte value in Steps(0))
            sum += value;
        return sum;
    }

    private static System.Collections.Generic.IEnumerable<long> Wide()
    {
        yield return 0x123456780000002AL;
        yield return -7;
    }

    public static unsafe int WideValues()
    {
        int count = 0;
        foreach (long value in Wide())
            ((long*)0xDF00)[count++] = value;
        return count + 40;
    }

    private sealed class ObservedSequence
        : System.Collections.Generic.IEnumerable<byte>,
            System.Collections.Generic.IEnumerator<byte>
    {
        private int current = -1;
        public static int Disposals;

        public System.Collections.Generic.IEnumerator<byte> GetEnumerator() => this;

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        public bool MoveNext() => ++current < 3;

        public byte Current => (byte)current;
        object System.Collections.IEnumerator.Current => Current;

        public void Dispose() => Disposals++;

        public void Reset() => current = -1;
    }

    public static unsafe int DisposeNormal()
    {
        ObservedSequence.Disposals = 0;
        int sum = 0;
        foreach (byte value in new ObservedSequence())
            sum += value;
        *(int*)0xDF00 = sum;
        return ObservedSequence.Disposals;
    }

    public static unsafe int DisposeBreak()
    {
        ObservedSequence.Disposals = 0;
        int sum = 0;
        foreach (byte value in new ObservedSequence())
        {
            sum += value;
            if (value == 1)
                break;
        }
        *(int*)0xDF00 = sum;
        return ObservedSequence.Disposals;
    }

    public static int ThreadId() => System.Environment.CurrentManagedThreadId;

    public static int Twice()
    {
        int sum = 0;
        foreach (byte b in Steps(4))
            sum += b;
        foreach (byte b in Steps(4))
            sum += b;
        Hardware.BGP = (byte)sum;
        return Hardware.BGP;
    }

    public static int Repeat()
    {
        var values = Steps(4);
        int sum = 0;
        foreach (byte b in values)
            sum += b;
        foreach (byte b in values)
            sum += b;
        return sum;
    }

    public static int Nested()
    {
        int sum = 0;
        foreach (byte a in Steps(3))
        foreach (byte b in Steps(2))
            sum += a + b;
        return sum;
    }

    public static int EarlyBreak()
    {
        int sum = 0;
        foreach (byte b in Steps(5))
        {
            if (b == 2)
                break;
            sum += b;
        }
        return sum;
    }

    private static System.Collections.Generic.IEnumerable<T> Echo<T>(T value)
    {
        yield return value;
        yield return value;
    }

    public static int Generic()
    {
        int sum = 0;
        foreach (int b in Echo(21))
            sum += b;
        return sum;
    }

    public static int Sum()
    {
        int sum = 0;
        foreach (byte value in Steps(5))
            sum += value;
        Hardware.BGP = (byte)sum;
        return Hardware.BGP;
    }
}

public static unsafe class FinallyRules
{
    private static int trace;

    private static void Mark(int digit) => trace = trace * 10 + digit;

    private static int Finish(int result)
    {
        *(int*)0xDF00 = trace;
        return result;
    }

    public static int Single()
    {
        trace = 0;
        try
        {
            Mark(1);
        }
        finally
        {
            Mark(2);
        }
        return Finish(42);
    }

    private static int Early(int value)
    {
        try
        {
            Mark(1);
            if (value != 0)
                return 42;
            Mark(9);
            return 0;
        }
        finally
        {
            Mark(2);
        }
    }

    public static int EarlyReturn()
    {
        trace = 0;
        int result = Early(1);
        return Finish(result);
    }

    private static int NestedWork()
    {
        try
        {
            Mark(1);
            try
            {
                Mark(2);
                return 42;
            }
            finally
            {
                Mark(3);
            }
        }
        finally
        {
            Mark(4);
        }
    }

    public static int Nested()
    {
        trace = 0;
        int result = NestedWork();
        return Finish(result);
    }

    public static int Break()
    {
        trace = 0;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Mark(i + 1);
                if (i == 1)
                    break;
            }
            finally
            {
                Mark(7);
            }
        }
        return Finish(42);
    }

    public static int Continue()
    {
        trace = 0;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Mark(i + 1);
                continue;
            }
            finally
            {
                Mark(8);
            }
        }
        return Finish(42);
    }

    public static int CleanupInsideCleanup()
    {
        trace = 0;
        try
        {
            Mark(1);
        }
        finally
        {
            Mark(2);
            try
            {
                Mark(3);
            }
            finally
            {
                Mark(4);
            }
            Mark(5);
        }
        return Finish(42);
    }

    private static int Recur(int n)
    {
        try
        {
            Mark(n);
            if (n > 1)
                return Recur(n - 1) + 1;
            return 40;
        }
        finally
        {
            Mark(n);
        }
    }

    public static int Recursive()
    {
        trace = 0;
        int result = Recur(3);
        return Finish(result);
    }

    private static int SavedWork()
    {
        int value = 42;
        try
        {
            return value;
        }
        finally
        {
            value = 99;
            Mark(4);
        }
    }

    public static int SavedReturn()
    {
        trace = 0;
        int result = SavedWork();
        return Finish(result);
    }

    private static long WideWork()
    {
        try
        {
            return 0x123456780000002AL;
        }
        finally
        {
            Mark(7);
        }
    }

    public static int WideReturn()
    {
        trace = 0;
        long result = WideWork();
        *(long*)0xDF08 = result;
        return Finish((int)result);
    }

    public static int Sequential()
    {
        trace = 0;
        try
        {
            Mark(1);
        }
        finally
        {
            Mark(2);
        }
        try
        {
            Mark(3);
        }
        finally
        {
            Mark(4);
        }
        return Finish(42);
    }

    public static int Catch()
    {
        try
        {
            return 42;
        }
        catch
        {
            return 0;
        }
    }
}
