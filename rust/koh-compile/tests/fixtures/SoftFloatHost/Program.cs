using System.Globalization;
using System.Reflection;
using Koh.GameBoy;

foreach (string request in args)
{
    string[] fields = request.Split(':');
    MethodInfo method =
        typeof(SoftFloat).GetMethod(fields[0], BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("Unknown runtime method");
    ParameterInfo[] parameters = method.GetParameters();
    if (parameters.Length != fields.Length - 1)
        throw new InvalidOperationException("Wrong runtime argument count");
    object[] values = new object[parameters.Length];
    for (int index = 0; index < values.Length; index++)
    {
        ulong value = ulong.Parse(
            fields[index + 1],
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture
        );
        values[index] = Type.GetTypeCode(parameters[index].ParameterType) switch
        {
            TypeCode.Byte => unchecked((byte)value),
            TypeCode.Int32 => unchecked((int)value),
            TypeCode.UInt32 => unchecked((uint)value),
            TypeCode.Int64 => unchecked((long)value),
            TypeCode.UInt64 => value,
            _ => throw new InvalidOperationException("Unsupported runtime argument type"),
        };
    }
    ulong result = method.Invoke(null, values) switch
    {
        uint value => value,
        ulong value => value,
        int value => unchecked((uint)value),
        long value => unchecked((ulong)value),
        byte value => value,
        bool value => value ? 1UL : 0UL,
        _ => throw new InvalidOperationException("Unsupported runtime result type"),
    };
    Console.WriteLine(result.ToString("X16", CultureInfo.InvariantCulture));
}
