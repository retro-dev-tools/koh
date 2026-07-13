namespace Koh.Emulator.Core.Apu;

/// <summary>
/// CH1's frequency sweep unit. Confirmed against Blargg's dmg_sound
/// 04-sweep/05-sweep-details source (retrio/gb-test-roms): only the shadow
/// frequency and the "enabled"/timer state are latched at trigger. Period,
/// shift and direction are re-read LIVE from NR10 every time the sweep
/// timer fires (or the trigger-time immediate check runs) — a plain NR10
/// write with no retrigger visibly changes subsequent sweep behavior
/// ("period and shift can be changed without channel disabling").
/// </summary>
public sealed class FrequencySweep
{
    public int ShadowFrequency;
    public bool Enabled;
    private int _period;

    // Last direction seen by Calculate()/OnNr10Write(), used only to detect
    // a negate->positive transition for the "exiting negate mode" quirk.
    private bool _lastIncreaseDirection;

    // Tracks whether a subtraction (negate-mode) calculation has run since
    // the last trigger. Clearing the negate bit in NR10 after that point
    // disables the channel immediately (Pan Docs "Obscure Behavior").
    private bool _negatedSinceTrigger;

    private static int PeriodOf(byte nr10) => (nr10 >> 4) & 0x07;

    private static bool IncreaseOf(byte nr10) => (nr10 & 0x08) == 0;

    private static int ShiftOf(byte nr10) => nr10 & 0x07;

    public void Trigger(byte nr10, int currentFreq, Action disableChannel)
    {
        ShadowFrequency = currentFreq;
        int period = PeriodOf(nr10);
        int shift = ShiftOf(nr10);
        Enabled = period != 0 || shift != 0;
        // The sweep timer treats a period of 0 as 8, from the very first reload.
        _period = period == 0 ? 8 : period;
        _negatedSinceTrigger = false;
        _lastIncreaseDirection = IncreaseOf(nr10);

        // If the individual step is non-zero, frequency calculation and the
        // overflow check run immediately on trigger (the result is discarded;
        // only the possible overflow-disable takes effect).
        if (shift != 0)
            Calculate(nr10, disableChannel);
    }

    /// <summary>
    /// NR10 writes take effect immediately for period/shift/direction (no
    /// retrigger needed) EXCEPT for one obscure case: clearing the negate
    /// (direction) bit after a subtraction calculation has already run since
    /// the last trigger disables the channel immediately, to prevent
    /// lowering then raising the frequency without an intervening trigger.
    /// </summary>
    public void OnNr10Write(byte nr10, Action disableChannel)
    {
        bool newIncreaseDirection = IncreaseOf(nr10);
        if (newIncreaseDirection && !_lastIncreaseDirection && _negatedSinceTrigger)
            disableChannel();
        _lastIncreaseDirection = newIncreaseDirection;
    }

    public int? Tick(byte nr10, Action disableChannel)
    {
        if (!Enabled)
            return null;
        _period--;
        if (_period > 0)
            return null;
        int period = PeriodOf(nr10);
        _period = period == 0 ? 8 : period;

        // Per Pan Docs: the periodic calculation only runs "if the enabled
        // flag is set AND the sweep pace is not zero." A period of 0 still
        // clocks the timer (treated as 8, above) but performs no calculation
        // or overflow check while it does.
        if (period == 0)
            return null;

        int shift = ShiftOf(nr10);
        int newFreq = Calculate(nr10, disableChannel);
        if (newFreq <= 2047 && shift > 0)
        {
            ShadowFrequency = newFreq;
            Calculate(nr10, disableChannel);
            return newFreq;
        }
        return null;
    }

    private int Calculate(byte nr10, Action disableChannel)
    {
        int shift = ShiftOf(nr10);
        bool increase = IncreaseOf(nr10);
        _lastIncreaseDirection = increase;

        int delta = ShadowFrequency >> shift;
        int newFreq;
        if (increase)
        {
            newFreq = ShadowFrequency + delta;
        }
        else
        {
            newFreq = ShadowFrequency - delta;
            _negatedSinceTrigger = true;
        }
        if (newFreq > 2047)
        {
            Enabled = false;
            disableChannel();
        }
        return newFreq;
    }
}
