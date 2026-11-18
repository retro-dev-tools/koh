using System;
using System.Collections;
using System.Collections.Generic;

namespace Koh.GameBoy.Runtime;

internal static class NativeLinq
{
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [KohIntrinsic("failnull")]
    private static void FailNull() => throw new ArgumentNullException();

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [KohIntrinsic("failempty")]
    private static void FailEmpty() => throw new InvalidOperationException();

    private static unsafe bool IsNaN(float value) => (*(uint*)&value & 0x7FFFFFFFu) > 0x7F800000u;

    private static unsafe bool IsNaN(double value) =>
        (*(ulong*)&value & 0x7FFFFFFFFFFFFFFFUL) > 0x7FF0000000000000UL;

    private static void Require<T>(T[] source)
    {
        if (source is null)
            FailNull();
    }

    private static void Require<T>(IEnumerable<T> source)
    {
        if (source is null)
            FailNull();
    }

    [KohRuntime("linq.where.array")]
    internal static IEnumerable<T> WhereArray<T>(T[] source, Func<T, bool> predicate)
    {
        Require(source);
        if (predicate is null)
            FailNull();
        return new WhereSequence<T>(new ArraySequence<T>(source), predicate);
    }

    [KohRuntime("linq.where.sequence")]
    internal static IEnumerable<T> WhereSequenceCall<T>(
        IEnumerable<T> source,
        Func<T, bool> predicate
    )
    {
        Require(source);
        if (predicate is null)
            FailNull();
        return new WhereSequence<T>(source, predicate);
    }

    [KohRuntime("linq.select.array")]
    internal static IEnumerable<U> SelectArray<T, U>(T[] source, Func<T, U> selector)
    {
        Require(source);
        if (selector is null)
            FailNull();
        return new SelectSequence<T, U>(new ArraySequence<T>(source), selector);
    }

    [KohRuntime("linq.select.sequence")]
    internal static IEnumerable<U> SelectSequenceCall<T, U>(
        IEnumerable<T> source,
        Func<T, U> selector
    )
    {
        Require(source);
        if (selector is null)
            FailNull();
        return new SelectSequence<T, U>(source, selector);
    }

    private static U FoldArray<T, U>(T[] source, U value, Func<U, T, U> step)
    {
        Require(source);
        for (int i = 0; i < source.Length; i++)
            value = step(value, source[i]);
        return value;
    }

    private static U FoldSequence<T, U>(IEnumerable<T> source, U value, Func<U, T, U> step)
    {
        Require(source);
        foreach (T item in source)
            value = step(value, item);
        return value;
    }

    private static T ExtremaArray<T>(T[] source, Func<T, T, bool> better, Func<T, bool>? stop)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        T value = source[0];
        if (stop is not null && stop(value))
            return value;
        for (int i = 1; i < source.Length; i++)
        {
            T item = source[i];
            if (stop is not null && stop(item))
                return item;
            if (better(item, value))
                value = item;
        }
        return value;
    }

    private static T ExtremaSequence<T>(
        IEnumerable<T> source,
        Func<T, T, bool> better,
        Func<T, bool>? stop
    )
    {
        Require(source);
        using IEnumerator<T> iterator = source.GetEnumerator();
        if (!iterator.MoveNext())
            FailEmpty();
        T value = iterator.Current;
        if (stop is not null && stop(value))
            return value;
        while (iterator.MoveNext())
        {
            T item = iterator.Current;
            if (stop is not null && stop(item))
                return item;
            if (better(item, value))
                value = item;
        }
        return value;
    }

    [KohRuntime("linq.count.array")]
    internal static int CountArray<T>(T[] source)
    {
        Require(source);
        return source.Length;
    }

    [KohRuntime("linq.count.sequence")]
    internal static int CountSequence<T>(IEnumerable<T> source)
    {
        Require(source);
        int count = 0;
        using IEnumerator<T> iterator = source.GetEnumerator();
        while (iterator.MoveNext())
            count = checked(count + 1);
        return count;
    }

    [KohRuntime("linq.any.array")]
    internal static bool AnyArray<T>(T[] source)
    {
        Require(source);
        return source.Length != 0;
    }

    [KohRuntime("linq.any.sequence")]
    internal static bool AnySequence<T>(IEnumerable<T> source)
    {
        Require(source);
        using IEnumerator<T> iterator = source.GetEnumerator();
        return iterator.MoveNext();
    }

    [KohRuntime("linq.count.predicate.array")]
    internal static int CountPredicateArray<T>(T[] source, Func<T, bool> predicate) =>
        CountSequence(WhereArray(source, predicate));

    [KohRuntime("linq.count.predicate.sequence")]
    internal static int CountPredicateSequence<T>(IEnumerable<T> source, Func<T, bool> predicate) =>
        CountSequence(WhereSequenceCall(source, predicate));

    [KohRuntime("linq.any.predicate.array")]
    internal static bool AnyPredicateArray<T>(T[] source, Func<T, bool> predicate)
    {
        Require(source);
        if (predicate is null)
            FailNull();
        for (int i = 0; i < source.Length; i++)
            if (predicate(source[i]))
                return true;
        return false;
    }

    [KohRuntime("linq.any.predicate.sequence")]
    internal static bool AnyPredicateSequence<T>(IEnumerable<T> source, Func<T, bool> predicate)
    {
        Require(source);
        if (predicate is null)
            FailNull();
        foreach (T item in source)
            if (predicate(item))
                return true;
        return false;
    }

    [KohRuntime("linq.all.array")]
    internal static bool AllArray<T>(T[] source, Func<T, bool> predicate)
    {
        Require(source);
        if (predicate is null)
            FailNull();
        for (int i = 0; i < source.Length; i++)
            if (!predicate(source[i]))
                return false;
        return true;
    }

    [KohRuntime("linq.all.sequence")]
    internal static bool AllSequence<T>(IEnumerable<T> source, Func<T, bool> predicate)
    {
        Require(source);
        if (predicate is null)
            FailNull();
        foreach (T item in source)
            if (!predicate(item))
                return false;
        return true;
    }

    [KohRuntime("linq.sum.i32.array")]
    internal static int SumI32Array(int[] source)
    {
        Require(source);
        return FoldArray<int, int>(source, 0, static (a, b) => checked(a + b));
    }

    [KohRuntime("linq.sum.i32.select.array")]
    internal static int SumI32SelectArray<T>(T[] source, Func<T, int> selector) =>
        SumI32Sequence(SelectArray(source, selector));

    [KohRuntime("linq.sum.i32.sequence")]
    internal static int SumI32Sequence(IEnumerable<int> source)
    {
        Require(source);
        return FoldSequence<int, int>(source, 0, static (a, b) => checked(a + b));
    }

    [KohRuntime("linq.sum.i32.select.sequence")]
    internal static int SumI32SelectSequence<T>(IEnumerable<T> source, Func<T, int> selector) =>
        SumI32Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.sum.i64.array")]
    internal static long SumI64Array(long[] source)
    {
        Require(source);
        return FoldArray<long, long>(source, 0L, static (a, b) => checked(a + b));
    }

    [KohRuntime("linq.sum.i64.select.array")]
    internal static long SumI64SelectArray<T>(T[] source, Func<T, long> selector) =>
        SumI64Sequence(SelectArray(source, selector));

    [KohRuntime("linq.sum.i64.sequence")]
    internal static long SumI64Sequence(IEnumerable<long> source)
    {
        Require(source);
        return FoldSequence<long, long>(source, 0L, static (a, b) => checked(a + b));
    }

    [KohRuntime("linq.sum.i64.select.sequence")]
    internal static long SumI64SelectSequence<T>(IEnumerable<T> source, Func<T, long> selector) =>
        SumI64Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.sum.f32.array")]
    internal static float SumF32Array(float[] source)
    {
        Require(source);
        return (float)FoldArray<float, double>(source, 0.0, static (a, b) => a + b);
    }

    [KohRuntime("linq.sum.f32.select.array")]
    internal static float SumF32SelectArray<T>(T[] source, Func<T, float> selector) =>
        SumF32Sequence(SelectArray(source, selector));

    [KohRuntime("linq.sum.f32.sequence")]
    internal static float SumF32Sequence(IEnumerable<float> source)
    {
        Require(source);
        return (float)FoldSequence<float, double>(source, 0.0, static (a, b) => a + b);
    }

    [KohRuntime("linq.sum.f32.select.sequence")]
    internal static float SumF32SelectSequence<T>(IEnumerable<T> source, Func<T, float> selector) =>
        SumF32Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.sum.f64.array")]
    internal static double SumF64Array(double[] source)
    {
        Require(source);
        return FoldArray<double, double>(source, 0.0, static (a, b) => a + b);
    }

    [KohRuntime("linq.sum.f64.select.array")]
    internal static double SumF64SelectArray<T>(T[] source, Func<T, double> selector) =>
        SumF64Sequence(SelectArray(source, selector));

    [KohRuntime("linq.sum.f64.sequence")]
    internal static double SumF64Sequence(IEnumerable<double> source)
    {
        Require(source);
        return FoldSequence<double, double>(source, 0.0, static (a, b) => a + b);
    }

    [KohRuntime("linq.sum.f64.select.sequence")]
    internal static double SumF64SelectSequence<T>(
        IEnumerable<T> source,
        Func<T, double> selector
    ) => SumF64Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.i8.array")]
    internal static sbyte MinI8Array(sbyte[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.i8.select.array")]
    internal static sbyte MinI8SelectArray<T>(T[] source, Func<T, sbyte> selector) =>
        MinI8Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.i8.sequence")]
    internal static sbyte MinI8Sequence(IEnumerable<sbyte> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.i8.select.sequence")]
    internal static sbyte MinI8SelectSequence<T>(IEnumerable<T> source, Func<T, sbyte> selector) =>
        MinI8Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.i8.array")]
    internal static sbyte MaxI8Array(sbyte[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.i8.select.array")]
    internal static sbyte MaxI8SelectArray<T>(T[] source, Func<T, sbyte> selector) =>
        MaxI8Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.i8.sequence")]
    internal static sbyte MaxI8Sequence(IEnumerable<sbyte> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.i8.select.sequence")]
    internal static sbyte MaxI8SelectSequence<T>(IEnumerable<T> source, Func<T, sbyte> selector) =>
        MaxI8Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.u8.array")]
    internal static byte MinU8Array(byte[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.u8.select.array")]
    internal static byte MinU8SelectArray<T>(T[] source, Func<T, byte> selector) =>
        MinU8Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.u8.sequence")]
    internal static byte MinU8Sequence(IEnumerable<byte> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.u8.select.sequence")]
    internal static byte MinU8SelectSequence<T>(IEnumerable<T> source, Func<T, byte> selector) =>
        MinU8Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.u8.array")]
    internal static byte MaxU8Array(byte[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.u8.select.array")]
    internal static byte MaxU8SelectArray<T>(T[] source, Func<T, byte> selector) =>
        MaxU8Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.u8.sequence")]
    internal static byte MaxU8Sequence(IEnumerable<byte> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.u8.select.sequence")]
    internal static byte MaxU8SelectSequence<T>(IEnumerable<T> source, Func<T, byte> selector) =>
        MaxU8Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.i16.array")]
    internal static short MinI16Array(short[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.i16.select.array")]
    internal static short MinI16SelectArray<T>(T[] source, Func<T, short> selector) =>
        MinI16Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.i16.sequence")]
    internal static short MinI16Sequence(IEnumerable<short> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.i16.select.sequence")]
    internal static short MinI16SelectSequence<T>(IEnumerable<T> source, Func<T, short> selector) =>
        MinI16Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.i16.array")]
    internal static short MaxI16Array(short[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.i16.select.array")]
    internal static short MaxI16SelectArray<T>(T[] source, Func<T, short> selector) =>
        MaxI16Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.i16.sequence")]
    internal static short MaxI16Sequence(IEnumerable<short> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.i16.select.sequence")]
    internal static short MaxI16SelectSequence<T>(IEnumerable<T> source, Func<T, short> selector) =>
        MaxI16Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.u16.array")]
    internal static ushort MinU16Array(ushort[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.u16.select.array")]
    internal static ushort MinU16SelectArray<T>(T[] source, Func<T, ushort> selector) =>
        MinU16Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.u16.sequence")]
    internal static ushort MinU16Sequence(IEnumerable<ushort> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.u16.select.sequence")]
    internal static ushort MinU16SelectSequence<T>(
        IEnumerable<T> source,
        Func<T, ushort> selector
    ) => MinU16Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.u16.array")]
    internal static ushort MaxU16Array(ushort[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.u16.select.array")]
    internal static ushort MaxU16SelectArray<T>(T[] source, Func<T, ushort> selector) =>
        MaxU16Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.u16.sequence")]
    internal static ushort MaxU16Sequence(IEnumerable<ushort> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.u16.select.sequence")]
    internal static ushort MaxU16SelectSequence<T>(
        IEnumerable<T> source,
        Func<T, ushort> selector
    ) => MaxU16Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.i32.array")]
    internal static int MinI32Array(int[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.i32.select.array")]
    internal static int MinI32SelectArray<T>(T[] source, Func<T, int> selector) =>
        MinI32Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.i32.sequence")]
    internal static int MinI32Sequence(IEnumerable<int> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.i32.select.sequence")]
    internal static int MinI32SelectSequence<T>(IEnumerable<T> source, Func<T, int> selector) =>
        MinI32Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.i32.array")]
    internal static int MaxI32Array(int[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.i32.select.array")]
    internal static int MaxI32SelectArray<T>(T[] source, Func<T, int> selector) =>
        MaxI32Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.i32.sequence")]
    internal static int MaxI32Sequence(IEnumerable<int> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.i32.select.sequence")]
    internal static int MaxI32SelectSequence<T>(IEnumerable<T> source, Func<T, int> selector) =>
        MaxI32Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.u32.array")]
    internal static uint MinU32Array(uint[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.u32.select.array")]
    internal static uint MinU32SelectArray<T>(T[] source, Func<T, uint> selector) =>
        MinU32Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.u32.sequence")]
    internal static uint MinU32Sequence(IEnumerable<uint> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.u32.select.sequence")]
    internal static uint MinU32SelectSequence<T>(IEnumerable<T> source, Func<T, uint> selector) =>
        MinU32Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.u32.array")]
    internal static uint MaxU32Array(uint[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.u32.select.array")]
    internal static uint MaxU32SelectArray<T>(T[] source, Func<T, uint> selector) =>
        MaxU32Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.u32.sequence")]
    internal static uint MaxU32Sequence(IEnumerable<uint> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.u32.select.sequence")]
    internal static uint MaxU32SelectSequence<T>(IEnumerable<T> source, Func<T, uint> selector) =>
        MaxU32Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.i64.array")]
    internal static long MinI64Array(long[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.i64.select.array")]
    internal static long MinI64SelectArray<T>(T[] source, Func<T, long> selector) =>
        MinI64Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.i64.sequence")]
    internal static long MinI64Sequence(IEnumerable<long> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.i64.select.sequence")]
    internal static long MinI64SelectSequence<T>(IEnumerable<T> source, Func<T, long> selector) =>
        MinI64Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.i64.array")]
    internal static long MaxI64Array(long[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.i64.select.array")]
    internal static long MaxI64SelectArray<T>(T[] source, Func<T, long> selector) =>
        MaxI64Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.i64.sequence")]
    internal static long MaxI64Sequence(IEnumerable<long> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.i64.select.sequence")]
    internal static long MaxI64SelectSequence<T>(IEnumerable<T> source, Func<T, long> selector) =>
        MaxI64Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.u64.array")]
    internal static ulong MinU64Array(ulong[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.u64.select.array")]
    internal static ulong MinU64SelectArray<T>(T[] source, Func<T, ulong> selector) =>
        MinU64Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.u64.sequence")]
    internal static ulong MinU64Sequence(IEnumerable<ulong> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.u64.select.sequence")]
    internal static ulong MinU64SelectSequence<T>(IEnumerable<T> source, Func<T, ulong> selector) =>
        MinU64Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.u64.array")]
    internal static ulong MaxU64Array(ulong[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.u64.select.array")]
    internal static ulong MaxU64SelectArray<T>(T[] source, Func<T, ulong> selector) =>
        MaxU64Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.u64.sequence")]
    internal static ulong MaxU64Sequence(IEnumerable<ulong> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.u64.select.sequence")]
    internal static ulong MaxU64SelectSequence<T>(IEnumerable<T> source, Func<T, ulong> selector) =>
        MaxU64Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.isize.array")]
    internal static nint MinISIZEArray(nint[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.isize.select.array")]
    internal static nint MinISIZESelectArray<T>(T[] source, Func<T, nint> selector) =>
        MinISIZESequence(SelectArray(source, selector));

    [KohRuntime("linq.min.isize.sequence")]
    internal static nint MinISIZESequence(IEnumerable<nint> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.isize.select.sequence")]
    internal static nint MinISIZESelectSequence<T>(IEnumerable<T> source, Func<T, nint> selector) =>
        MinISIZESequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.isize.array")]
    internal static nint MaxISIZEArray(nint[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.isize.select.array")]
    internal static nint MaxISIZESelectArray<T>(T[] source, Func<T, nint> selector) =>
        MaxISIZESequence(SelectArray(source, selector));

    [KohRuntime("linq.max.isize.sequence")]
    internal static nint MaxISIZESequence(IEnumerable<nint> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.isize.select.sequence")]
    internal static nint MaxISIZESelectSequence<T>(IEnumerable<T> source, Func<T, nint> selector) =>
        MaxISIZESequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.usize.array")]
    internal static nuint MinUSIZEArray(nuint[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.usize.select.array")]
    internal static nuint MinUSIZESelectArray<T>(T[] source, Func<T, nuint> selector) =>
        MinUSIZESequence(SelectArray(source, selector));

    [KohRuntime("linq.min.usize.sequence")]
    internal static nuint MinUSIZESequence(IEnumerable<nuint> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, null);
    }

    [KohRuntime("linq.min.usize.select.sequence")]
    internal static nuint MinUSIZESelectSequence<T>(
        IEnumerable<T> source,
        Func<T, nuint> selector
    ) => MinUSIZESequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.usize.array")]
    internal static nuint MaxUSIZEArray(nuint[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.usize.select.array")]
    internal static nuint MaxUSIZESelectArray<T>(T[] source, Func<T, nuint> selector) =>
        MaxUSIZESequence(SelectArray(source, selector));

    [KohRuntime("linq.max.usize.sequence")]
    internal static nuint MaxUSIZESequence(IEnumerable<nuint> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b, null);
    }

    [KohRuntime("linq.max.usize.select.sequence")]
    internal static nuint MaxUSIZESelectSequence<T>(
        IEnumerable<T> source,
        Func<T, nuint> selector
    ) => MaxUSIZESequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.f32.array")]
    internal static float MinF32Array(float[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, static value => IsNaN(value));
    }

    [KohRuntime("linq.min.f32.select.array")]
    internal static float MinF32SelectArray<T>(T[] source, Func<T, float> selector) =>
        MinF32Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.f32.sequence")]
    internal static float MinF32Sequence(IEnumerable<float> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, static value => IsNaN(value));
    }

    [KohRuntime("linq.min.f32.select.sequence")]
    internal static float MinF32SelectSequence<T>(IEnumerable<T> source, Func<T, float> selector) =>
        MinF32Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.f32.array")]
    internal static float MaxF32Array(float[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b || IsNaN(b), null);
    }

    [KohRuntime("linq.max.f32.select.array")]
    internal static float MaxF32SelectArray<T>(T[] source, Func<T, float> selector) =>
        MaxF32Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.f32.sequence")]
    internal static float MaxF32Sequence(IEnumerable<float> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b || IsNaN(b), null);
    }

    [KohRuntime("linq.max.f32.select.sequence")]
    internal static float MaxF32SelectSequence<T>(IEnumerable<T> source, Func<T, float> selector) =>
        MaxF32Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.min.f64.array")]
    internal static double MinF64Array(double[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a < b, static value => IsNaN(value));
    }

    [KohRuntime("linq.min.f64.select.array")]
    internal static double MinF64SelectArray<T>(T[] source, Func<T, double> selector) =>
        MinF64Sequence(SelectArray(source, selector));

    [KohRuntime("linq.min.f64.sequence")]
    internal static double MinF64Sequence(IEnumerable<double> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a < b, static value => IsNaN(value));
    }

    [KohRuntime("linq.min.f64.select.sequence")]
    internal static double MinF64SelectSequence<T>(
        IEnumerable<T> source,
        Func<T, double> selector
    ) => MinF64Sequence(SelectSequenceCall(source, selector));

    [KohRuntime("linq.max.f64.array")]
    internal static double MaxF64Array(double[] source)
    {
        Require(source);
        if (source.Length == 0)
            FailEmpty();
        return ExtremaArray(source, static (a, b) => a > b || IsNaN(b), null);
    }

    [KohRuntime("linq.max.f64.select.array")]
    internal static double MaxF64SelectArray<T>(T[] source, Func<T, double> selector) =>
        MaxF64Sequence(SelectArray(source, selector));

    [KohRuntime("linq.max.f64.sequence")]
    internal static double MaxF64Sequence(IEnumerable<double> source)
    {
        Require(source);
        return ExtremaSequence(source, static (a, b) => a > b || IsNaN(b), null);
    }

    [KohRuntime("linq.max.f64.select.sequence")]
    internal static double MaxF64SelectSequence<T>(
        IEnumerable<T> source,
        Func<T, double> selector
    ) => MaxF64Sequence(SelectSequenceCall(source, selector));

    private sealed class ArraySequence<T>(T[] source) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => new ArrayIterator<T>(source);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ArrayIterator<T>(T[] source) : IEnumerator<T>
    {
        private int index = -1;
        public T Current => source[index];
        object IEnumerator.Current => throw new NotSupportedException();

        public bool MoveNext()
        {
            int next = index + 1;
            if (next >= source.Length)
            {
                index = source.Length;
                return false;
            }
            index = next;
            return true;
        }

        public void Dispose() => index = source.Length;

        public void Reset() => throw new NotSupportedException();
    }

    private sealed class WhereSequence<T>(IEnumerable<T> source, Func<T, bool> predicate)
        : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => new WhereIterator<T>(source, predicate);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class WhereIterator<T>(IEnumerable<T> source, Func<T, bool> predicate)
        : IEnumerator<T>
    {
        private IEnumerator<T>? iterator;
        private bool disposed;
        private T current = default!;
        public T Current => current;
        object IEnumerator.Current => throw new NotSupportedException();

        public bool MoveNext()
        {
            if (disposed)
                return false;
            iterator ??= source.GetEnumerator();
            while (iterator.MoveNext())
            {
                T item = iterator.Current;
                if (predicate(item))
                {
                    current = item;
                    return true;
                }
            }
            Dispose();
            return false;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (iterator is not null)
            {
                iterator.Dispose();
                iterator = null;
            }
        }

        public void Reset() => throw new NotSupportedException();
    }

    private sealed class SelectSequence<T, U>(IEnumerable<T> source, Func<T, U> selector)
        : IEnumerable<U>
    {
        public IEnumerator<U> GetEnumerator() => new SelectIterator<T, U>(source, selector);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SelectIterator<T, U>(IEnumerable<T> source, Func<T, U> selector)
        : IEnumerator<U>
    {
        private IEnumerator<T>? iterator;
        private bool disposed;
        private U current = default!;
        public U Current => current;
        object IEnumerator.Current => throw new NotSupportedException();

        public bool MoveNext()
        {
            if (disposed)
                return false;
            iterator ??= source.GetEnumerator();
            if (iterator.MoveNext())
            {
                current = selector(iterator.Current);
                return true;
            }
            Dispose();
            return false;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (iterator is not null)
            {
                iterator.Dispose();
                iterator = null;
            }
        }

        public void Reset() => throw new NotSupportedException();
    }
}
