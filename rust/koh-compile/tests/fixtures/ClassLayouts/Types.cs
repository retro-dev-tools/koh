using System.Runtime.InteropServices;

public class Empty { }

public sealed class CrossAssembly : ClassBase.ExternalRoot
{
    public new byte Hidden;
    public ClassBase.ExternalRoot? Link;
}

public enum Tiny : sbyte
{
    Zero,
}

public class Numbers
{
    public bool Flag;
    public char Letter;
    public nint Address;
    public float Single;
    public double Double;
    public Tiny Kind;
}

public class ArrayFields
{
    public Child[][]? Nested;
    public int[,]? Grid;
}

public class Root
{
    public byte A;
    public short B;
    public int C;
    public long D;
    public static int Shared;
}

public interface IPeer { }

public sealed class Child : Root, IPeer
{
    public new byte A;
    public Root? Link;
    public byte[]? Bytes;
    public string? Text;
    public object? Any;
    public IPeer? Peer;
    public int[,]? Grid;
}

public sealed class Self
{
    public Self? Next;
    public byte Value;
}

public struct Body
{
    public ushort A;
    public byte B;
}

public sealed class WithBody
{
    public byte Prefix;
    public Body Value;
    public byte Tail;
}

public abstract class Abstract
{
    public int State;
}

public class Generic<T>
{
    public T? Value;
}

[StructLayout(LayoutKind.Sequential)]
public class Sequential
{
    public int Value;
}

[StructLayout(LayoutKind.Explicit)]
public class Explicit
{
    [FieldOffset(0)]
    public int Value;
}

[StructLayout(LayoutKind.Sequential, Size = 4096)]
public struct Huge { }

public class TooLarge
{
    public Huge Body;
}

public static class Program
{
    public static int Main()
    {
        var first = new Child();
        var second = new Child();
        if (first == second || first.A != 0 || first.Link != null || first.Bytes != null)
            return 1;
        Root root = first;
        root.A = 40;
        first.A = 2;
        var cross = new CrossAssembly();
        ClassBase.ExternalRoot parent = cross;
        cross.Link = parent;
        parent.Hidden = 10;
        cross.Hidden = 20;
        if (cross.Link != parent || parent.Hidden != 10 || cross.Hidden != 20)
            return 1;
        return root.A + first.A;
    }
}
