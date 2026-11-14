using System;
using System.Reflection.Emit;

foreach (var field in typeof(OpCodes).GetFields())
{
    if (field.GetValue(null) is OpCode opcode)
    {
        var value = unchecked((ushort)opcode.Value);
        if (value < 0xF8 || value > 0xFF)
            Console.WriteLine($"{value:X4} {opcode.OperandType}");
    }
}
