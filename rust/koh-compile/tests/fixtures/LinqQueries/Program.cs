#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Koh.GameBoy;

public static unsafe class Program
{
    private static int Project(int value)
    {
        return value * 2;
    }

    public static int DebugQuery()
    {
        int result = new[] { 1, 2, 3, 4, 5 }.Where(x => x > 2).Select(Project).Sum();
        Hardware.BGP = (byte)result;
        return result + 18;
    }

    public static int Pipeline()
    {
        int[] values = { 1, 2, 3, 4, 5 };
        int result = values.Where(x => x > 2).Select(x => x * 2).Sum();
        Hardware.BGP = (byte)result;
        return result;
    }

    public static int Max()
    {
        int result = new[] { 3, 9, 1, 7 }.Max();
        Hardware.BGP = (byte)result;
        return result;
    }

    public static int PipelineMax()
    {
        int result = new[] { 1, 2, 3 }.Where(x => x > 0).Max();
        Hardware.BGP = (byte)result;
        return result;
    }

    private static IEnumerable<int>? saved;

    private static int Finish(int value)
    {
        *(int*)0xDF00 = value;
        return 42;
    }

    public static int NullSource() => Finish(((int[])null!).Sum());

    public static int NullPredicate()
    {
        _ = new int[0].Where((Func<int, bool>)null!);
        return Finish(1);
    }

    public static int NullSelector()
    {
        _ = new int[0].Select((Func<int, int>)null!);
        return Finish(1);
    }

    public static int EmptyMin() => Finish(new int[0].Min());

    public static int EmptyMaxQuery() =>
        Finish(
            new int[0]
                .Where(x => true)
                .Max()
        );

    public static int OverflowI32() => Finish(new[] { int.MaxValue, 1 }.Sum());

    public static int OverflowI64() =>
        Finish((int)new[] { long.MinValue, -1L }.Where(x => true).Sum());

    public static int IndexedWhere() => Finish(new[] { 1, 2 }.Where((x, i) => i > 0).Sum());

    public static int IndexedSelect() => Finish(new[] { 1, 2 }.Select((x, i) => x + i).Sum());

    public static int Materialize() => Finish(new[] { 1, 2 }.Where(x => true).ToArray().Length);

    public static int CustomComparer() => Finish(new[] { 1, 2 }.Max((IComparer<int>)null!));

    public static int FloatEdges()
    {
        uint firstBits = 0x7FC00011,
            secondBits = 0xFFC00022,
            zeroBits = 0x80000000;
        float first = *(float*)&firstBits,
            second = *(float*)&secondBits,
            negativeZero = *(float*)&zeroBits;
        float[] values = { -7f, first, 42f, second };
        float[] nan = { first, second };
        float[] zeros = { negativeZero, 0f };
        float* p = (float*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Where(x => true).Min();
        p[3] = values.Where(x => true).Max();
        p[4] = nan.Min();
        p[5] = nan.Max();
        p[6] = nan.Where(x => true).Min();
        p[7] = nan.Where(x => true).Max();
        p[8] = zeros.Min();
        p[9] = zeros.Max();
        p[10] = zeros.Min(x => x);
        p[11] = zeros.Max(x => x);
        return 42;
    }

    public static int DoubleEdges()
    {
        ulong firstBits = 0x7FF8000000000011,
            secondBits = 0xFFF8000000000022,
            zeroBits = 0x8000000000000000;
        double first = *(double*)&firstBits,
            second = *(double*)&secondBits,
            negativeZero = *(double*)&zeroBits;
        double[] values = { -7d, first, 42d, second };
        double[] nan = { first, second };
        double[] zeros = { negativeZero, 0d };
        double* p = (double*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Where(x => true).Min();
        p[3] = values.Where(x => true).Max();
        p[4] = nan.Min();
        p[5] = nan.Max();
        p[6] = nan.Where(x => true).Min();
        p[7] = nan.Where(x => true).Max();
        p[8] = zeros.Min();
        p[9] = zeros.Max();
        p[10] = zeros.Min(x => x);
        p[11] = zeros.Max(x => x);
        return 42;
    }

    public static int NaNDisposal()
    {
        uint bits = 0x7FC00011;
        float nan = *(float*)&bits;
        var source = new ProbeSequence<float>(new[] { -7f, nan, 42f });
        int calls = 0;
        ProbeSequence<float>.ResetCounts();
        float* f = (float*)0xDF00;
        int* p = (int*)0xDF00;
        f[0] = source.Min(x =>
        {
            calls++;
            return x;
        });
        p[1] = calls;
        p[2] = ProbeSequence<float>.Moved;
        p[3] = ProbeSequence<float>.Closed;
        calls = 0;
        ProbeSequence<float>.ResetCounts();
        f[4] = source.Max(x =>
        {
            calls++;
            return x;
        });
        p[5] = calls;
        p[6] = ProbeSequence<float>.Moved;
        p[7] = ProbeSequence<float>.Closed;
        return 42;
    }

    private static IEnumerable<int> Build(int[] values) => values.Where(x => x > 2);

    public static int Escaped()
    {
        int[] values = { 1, 2, 3, 4 };
        saved = Build(values);
        values[2] = 9;
        int* p = (int*)0xDF00;
        p[0] = saved.Sum();
        p[1] = saved.Count();
        p[2] = saved.Max();
        return 42;
    }

    public static int Deferred()
    {
        ProbeSequence<int>.ResetCounts();
        var source = new ProbeSequence<int>(new[] { 1, 2, 3, 4 });
        int threshold = 1;
        int predicates = 0;
        int selectors = 0;
        var query = source
            .Where(x =>
            {
                predicates++;
                return x > threshold;
            })
            .Select(x =>
            {
                selectors++;
                return x * 2;
            });
        int* p = (int*)0xDF00;
        p[0] = ProbeSequence<int>.Opened;
        p[1] = predicates;
        p[2] = selectors;
        threshold = 2;
        p[3] = query.Sum();
        p[4] = predicates;
        p[5] = selectors;
        p[6] = ProbeSequence<int>.Opened;
        p[7] = ProbeSequence<int>.Moved;
        p[8] = ProbeSequence<int>.Closed;
        threshold = 3;
        p[9] = query.Sum();
        p[10] = predicates;
        p[11] = selectors;
        p[12] = ProbeSequence<int>.Opened;
        p[13] = ProbeSequence<int>.Moved;
        p[14] = ProbeSequence<int>.Closed;
        threshold = 0;
        foreach (int value in query)
        {
            p[15] = value;
            break;
        }
        p[16] = predicates;
        p[17] = selectors;
        p[18] = ProbeSequence<int>.Opened;
        p[19] = ProbeSequence<int>.Moved;
        p[20] = ProbeSequence<int>.Closed;
        var iterator = query.GetEnumerator();
        iterator.Dispose();
        p[21] = iterator.MoveNext() ? 1 : 0;
        p[22] = ProbeSequence<int>.Opened;
        p[23] = ProbeSequence<int>.Closed;
        return 42;
    }

    public static int ShortCircuit()
    {
        var source = new ProbeSequence<int>(new[] { 1, 2, 3, 4 });
        int* p = (int*)0xDF00;
        int calls = 0;
        ProbeSequence<int>.ResetCounts();
        p[0] = source.Any(x =>
        {
            calls++;
            return x == 2;
        })
            ? 1
            : 0;
        p[1] = calls;
        p[2] = ProbeSequence<int>.Moved;
        p[3] = ProbeSequence<int>.Closed;
        calls = 0;
        ProbeSequence<int>.ResetCounts();
        p[4] = source.All(x =>
        {
            calls++;
            return x < 3;
        })
            ? 1
            : 0;
        p[5] = calls;
        p[6] = ProbeSequence<int>.Moved;
        p[7] = ProbeSequence<int>.Closed;
        calls = 0;
        ProbeSequence<int>.ResetCounts();
        p[8] = source.Count(x =>
        {
            calls++;
            return x % 2 == 0;
        });
        p[9] = calls;
        p[10] = ProbeSequence<int>.Moved;
        p[11] = ProbeSequence<int>.Closed;
        ProbeSequence<int>.ResetCounts();
        p[12] = source.Count();
        p[13] = ProbeSequence<int>.Read;
        p[14] = ProbeSequence<int>.Closed;
        ProbeSequence<int>.ResetCounts();
        p[15] = source.Any() ? 1 : 0;
        p[16] = ProbeSequence<int>.Read;
        p[17] = ProbeSequence<int>.Closed;
        int[] empty = new int[0];
        p[18] = empty.Count();
        p[19] = empty.Any() ? 1 : 0;
        p[20] = empty.All(x => false) ? 1 : 0;
        p[21] = empty.Any(x => true) ? 1 : 0;
        p[22] = new[] { 1, 2, 3 }.Count(x => x > 1);
        p[23] = new[] { 1, 2, 3 }.Any(x => x == 2) ? 1 : 0;
        p[24] = new[] { 1, 2, 3 }.All(x => x < 4) ? 1 : 0;
        return 42;
    }

    public static int Foreign()
    {
        GameRecord[] values =
        {
            new GameRecord { Value = 3, Wide = 10000000000L },
            new GameRecord { Value = 7, Wide = 20000000000L },
        };
        var query = Library
            .QueryForward(values.Where(x => x.Value > 3))
            .Select(x => x.Wide + x.Value);
        *(long*)0xDF00 = query.Sum();
        *(int*)0xDF08 = query.Count();
        return 42;
    }

    public static int MixedSelectors()
    {
        byte[] bytes = { 250, 1, 2 };
        *(long*)0xDF00 = bytes.Sum(x => (long)x * 1000000000L);
        *(double*)0xDF08 = bytes.Sum(x => x * 0.5);
        *(int*)0xDF10 = bytes.Sum(x => (int)x);
        GameRecord[] records =
        {
            new GameRecord { Value = 3, Wide = 10000000000L },
            new GameRecord { Value = 7, Wide = 20000000000L },
        };
        *(long*)0xDF14 = records.Sum(x => x.Wide);
        *(int*)0xDF1C = records.Max(x => x.Value);
        *(long*)0xDF20 = records.Min(x => x.Wide);
        return 42;
    }

    public static int SumsI32()
    {
        int[] values = { -3, 7, 20, 18 };
        var query = values.Where(x => true);
        int* p = (int*)0xDF00;
        p[0] = values.Sum();
        p[1] = query.Sum();
        p[2] = values.Sum(x => x + 1);
        p[3] = query.Sum(x => x + 1);
        p[4] = new int[0].Sum();
        p[5] = new int[0]
            .Where(x => true)
            .Sum();
        return 42;
    }

    public static int SumsI64()
    {
        long[] values = { 0x123456780000002AL, -7, 7 };
        var query = values.Where(x => true);
        long* p = (long*)0xDF00;
        p[0] = values.Sum();
        p[1] = query.Sum();
        p[2] = values.Sum(x => x + 1);
        p[3] = query.Sum(x => x + 1);
        p[4] = new long[0].Sum();
        p[5] = new long[0]
            .Where(x => true)
            .Sum();
        return 42;
    }

    public static int SumsF32()
    {
        float[] values = { 16777216f, 1f, -16777216f };
        var query = values.Where(x => true);
        float* p = (float*)0xDF00;
        p[0] = values.Sum();
        p[1] = query.Sum();
        p[2] = values.Sum(x => x);
        p[3] = query.Sum(x => x);
        p[4] = new float[0].Sum();
        p[5] = new float[0]
            .Where(x => true)
            .Sum();
        return 42;
    }

    public static int SumsF64()
    {
        double[] values = { 0.5, 0.25, -0.125 };
        var query = values.Where(x => true);
        double* p = (double*)0xDF00;
        p[0] = values.Sum();
        p[1] = query.Sum();
        p[2] = values.Sum(x => x * 0.5);
        p[3] = query.Sum(x => x * 0.5);
        p[4] = new double[0].Sum();
        p[5] = new double[0]
            .Where(x => true)
            .Sum();
        return 42;
    }

    public static int ExtremaI8()
    {
        sbyte[] values = { -100, -7, 42 };
        var query = values.Where(x => true);
        sbyte* p = (sbyte*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaU8()
    {
        byte[] values = { 1, 42, 250 };
        var query = values.Where(x => true);
        byte* p = (byte*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaI16()
    {
        short[] values = { -30000, -7, 42 };
        var query = values.Where(x => true);
        short* p = (short*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaU16()
    {
        ushort[] values = { 1, 42, 60000 };
        var query = values.Where(x => true);
        ushort* p = (ushort*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaI32()
    {
        int[] values = { int.MinValue, -7, int.MaxValue };
        var query = values.Where(x => true);
        int* p = (int*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaU32()
    {
        uint[] values = { 0u, 42u, uint.MaxValue };
        var query = values.Where(x => true);
        uint* p = (uint*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaI64()
    {
        long[] values = { long.MinValue, -7L, long.MaxValue };
        var query = values.Where(x => true);
        long* p = (long*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaU64()
    {
        ulong[] values = { 0UL, 42UL, ulong.MaxValue };
        var query = values.Where(x => true);
        ulong* p = (ulong*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaNative()
    {
        nint[] values = { unchecked((nint)0x8000), (nint)(-7), (nint)0x7FFF };
        var query = values.Where(x => true);
        nint* p = (nint*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaUNative()
    {
        nuint[] values = { (nuint)0, (nuint)42, unchecked((nuint)0xFFFF) };
        var query = values.Where(x => true);
        nuint* p = (nuint*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaF32()
    {
        float[] values = { -7f, 0f, 42f };
        var query = values.Where(x => true);
        float* p = (float*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }

    public static int ExtremaF64()
    {
        double[] values = { -7d, 0d, 42d };
        var query = values.Where(x => true);
        double* p = (double*)0xDF00;
        p[0] = values.Min();
        p[1] = values.Max();
        p[2] = values.Min(x => x);
        p[3] = values.Max(x => x);
        p[4] = query.Min();
        p[5] = query.Max();
        p[6] = query.Min(x => x);
        p[7] = query.Max(x => x);
        return 42;
    }
}

public struct GameRecord
{
    public int Value;
    public long Wide;
}

public sealed class ProbeSequence<T>(T[] values) : IEnumerable<T>
{
    public static int Opened,
        Moved,
        Read,
        Closed;

    public static void ResetCounts()
    {
        Opened = 0;
        Moved = 0;
        Read = 0;
        Closed = 0;
    }

    public IEnumerator<T> GetEnumerator()
    {
        Opened++;
        return new ProbeIterator<T>(values);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}

public sealed class ProbeIterator<T>(T[] values) : IEnumerator<T>
{
    private int index = -1;
    private bool disposed;
    public T Current
    {
        get
        {
            ProbeSequence<T>.Read++;
            return values[index];
        }
    }
    object System.Collections.IEnumerator.Current => throw new NotSupportedException();

    public bool MoveNext()
    {
        ProbeSequence<T>.Moved++;
        return ++index < values.Length;
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            ProbeSequence<T>.Closed++;
        }
    }

    public void Reset() => throw new NotSupportedException();
}
