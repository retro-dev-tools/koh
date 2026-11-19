using var output = new BinaryWriter(Console.OpenStandardOutput());
for (int code = 0; code <= char.MaxValue; code++)
{
    output.Write((ushort)char.ToUpperInvariant((char)code));
    output.Write(char.IsDigit((char)code));
}
