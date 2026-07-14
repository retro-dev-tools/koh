namespace Koh.Emulator.Core.Apu;

public sealed class FrameSequencer
{
    public int Step;

    // Armed by Apu when NR52 powers the APU on while the DIV-APU tap bit is
    // already high (see Timer.DivApuBitHigh): real hardware then skips the
    // very next DIV-APU falling edge entirely -- no Step advance, no
    // Length/Sweep/Envelope dispatch -- effectively pushing the next frame-
    // sequencer event out by a full extra DIV-APU period (Pan Docs / SameBoy
    // `GB_apu_init`'s "APU glitch"). Consumed by the next Advance() call.
    public bool SkipNext;

    public event Action? LengthClock;
    public event Action? SweepClock;
    public event Action? EnvelopeClock;

    public void Advance()
    {
        if (SkipNext)
        {
            SkipNext = false;
            return;
        }

        Step = (Step + 1) & 7;

        bool len = Step is 0 or 2 or 4 or 6;
        bool sweep = Step is 2 or 6;
        bool env = Step == 7;

        if (len)
            LengthClock?.Invoke();
        if (sweep)
            SweepClock?.Invoke();
        if (env)
            EnvelopeClock?.Invoke();
    }

    public void Reset() => Step = 0;
}
