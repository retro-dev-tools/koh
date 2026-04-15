namespace Koh.Emulator.Core.Apu;

public sealed class LengthCounter
{
    public int Counter;
    public bool Enabled;
    public readonly int MaxLength;

    public LengthCounter(int maxLength) { MaxLength = maxLength; }

    public void Tick(Action disable)
    {
        if (!Enabled || Counter == 0) return;
        Counter--;
        if (Counter == 0) disable();
    }
}
