namespace Koh.GameBoy.Graphics;

internal static unsafe class Vram
{
    [KohIntrinsic("vramwrite")]
    internal static void WriteByte(byte* address, byte value) => *address = value;
}
