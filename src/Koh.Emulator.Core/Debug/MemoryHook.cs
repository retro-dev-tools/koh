namespace Koh.Emulator.Core.Debug;

public abstract class MemoryHook
{
    public abstract void OnRead(ushort address, byte value);
    public abstract void OnWrite(ushort address, byte value);
}
