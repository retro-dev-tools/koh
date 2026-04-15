namespace Koh.Emulator.Core.Apu;

public sealed class FrameSequencer
{
    public int Step;

    public event Action? LengthClock;
    public event Action? SweepClock;
    public event Action? EnvelopeClock;

    public void Advance()
    {
        Step = (Step + 1) & 7;

        bool len = Step is 0 or 2 or 4 or 6;
        bool sweep = Step is 2 or 6;
        bool env = Step == 7;

        if (len) LengthClock?.Invoke();
        if (sweep) SweepClock?.Invoke();
        if (env) EnvelopeClock?.Invoke();
    }

    public void Reset() => Step = 0;
}
