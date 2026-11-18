using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GamePayload
{
    public int Count;
    public long Wide;
}

public sealed class GameNode
{
    public int Value;
}

public sealed class LocalCallbackBox<T>
{
    public Func<T, T> Callback;

    public LocalCallbackBox(Func<T, T> callback) => Callback = callback;

    public T Read(T value) => Callback(value);
}

public static unsafe class Program
{
    private static void Packet(int count, long wide)
    {
        *(int*)0xDF00 = count;
        *(long*)0xDF04 = wide;
    }

    private static GamePayload Increment(GamePayload value)
    {
        value.Count += 5;
        value.Wide += 5;
        return value;
    }

    private static RemotePayload IncrementRemote(RemotePayload value)
    {
        value.Count += 5;
        value.Wide += 5;
        return value;
    }

    private static RemoteCode IdentityCode(RemoteCode value) => value;

    private static long IdentityLong(long value) => value;

    public static int LocalValue()
    {
        var value = new GamePayload { Count = 37, Wide = 0x1234567800000025L };
        var box = new ForeignCallbackBox<GamePayload>(Increment);
        var result = box.Read(value);
        Packet(result.Count, result.Wide);
        return value.Count == 37 && value.Wide == 0x1234567800000025L ? result.Count : 0;
    }

    public static int LocalReference()
    {
        var value = new GameNode { Value = 37 };
        var box = new ForeignCallbackBox<GameNode>(x =>
        {
            x.Value += 5;
            return x;
        });
        var result = box.Read(value);
        return result == value && value.Value == 42 ? result.Value : 0;
    }

    public static int RemoteValue()
    {
        var value = new RemotePayload { Count = 37, Wide = 0x1234567800000025L };
        var box = new LocalCallbackBox<RemotePayload>(IncrementRemote);
        var result = box.Read(value);
        Packet(result.Count, result.Wide);
        return value.Count == 37 && value.Wide == 0x1234567800000025L ? result.Count : 0;
    }

    public static int Enum()
    {
        var box = new LocalCallbackBox<RemoteCode>(IdentityCode);
        var result = box.Read(RemoteCode.Value);
        *(long*)0xDF00 = (long)result;
        return result == RemoteCode.Value ? 42 : 0;
    }

    public static int Arrays()
    {
        var values = new GamePayload[2];
        values[1] = new GamePayload { Count = 42, Wide = 0x123456780000002AL };
        var box = new ForeignCallbackBox<GamePayload[]>(x => x);
        var alias = box.Read(values);
        alias[0] = alias[1];
        Packet(values[0].Count, values[0].Wide);
        return values[0].Count;
    }

    public static int Nested()
    {
        Func<GamePayload, RemotePayload> convert = x => new RemotePayload
        {
            Count = x.Count + 5,
            Wide = x.Wide + 5,
        };
        var box = new ForeignCallbackBox<Func<GamePayload, RemotePayload>>(x => x);
        var callback = box.Read(convert);
        var result = callback(new GamePayload { Count = 37, Wide = 0x1234567800000025L });
        Packet(result.Count, result.Wide);
        return result.Count;
    }

    public static int Captured()
    {
        var box = new ForeignCallbackBox<GameNode>(x => x);
        var value = new GameNode { Value = 37 };
        var callback = box.Capture(value);
        value.Value = 42;
        return callback(new GameNode { Value = 7 }).Value;
    }

    public static int MethodScopes()
    {
        var box = new ForeignCallbackBox<GamePayload>(x => x);
        var result = box.Select<RemotePayload>(
            new GamePayload { Count = 37, Wide = 0x1234567800000025L },
            x => new RemotePayload { Count = x.Count + 5, Wide = x.Wide + 5 }
        );
        Packet(result.Count, result.Wide);
        return result.Count;
    }

    public static int Constraint()
    {
        var box = new ForeignConstrainedBox<GameNode>(x => x);
        return box.Read(new GameNode { Value = 42 }).Value;
    }

    public static int Factory()
    {
        ForeignFactory<GameNode> callback = () => new GameNode { Value = 42 };
        return callback().Value;
    }

    private static int Probe<T>() => 42;

    public static int UnusedFactory() => Probe<ForeignFactory<GameNode>>();
}
