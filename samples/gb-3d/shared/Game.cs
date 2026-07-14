static class Game
{
    // Rotation phase step per render+present cycle. gcd(step, 256) = 1 so all 256 phase values occur
    // (required by verify/PhaseSweepCheck's exhaustive sweep). Shared across all three demos (this
    // file is compiled unmodified into each), so one value has to suit every demo/mode's cadence —
    // frame-diff-measured (see verify/Program.cs's frame-diff technique) real render+present cadence
    // ranges from ~17-48 frames/cycle on the CGB paths (double-buffered, full-frame, racing-beam) down
    // to ~33-114 frames/cycle for full-frame/racing-beam DMG, and ~311-344 for double-buffered DMG (its
    // vblank-chunked CPU upload, the slow monochrome fallback). Step=1 is tuned for the FASTEST paths
    // (the CGB cadences, mostly at or below the ~35-frame smooth/chunky threshold): a bigger step there
    // would jump too many degrees per visible flip to read as continuous rotation. Accepted tradeoff:
    // the slow DMG fallbacks (particularly double-buffered's ~340-frame flip) rotate very slowly in
    // real time with step=1 (a full 256-phase revolution takes several minutes) — deliberately not
    // compensated with a bigger shared step, since that would make the fast CGB paths look jerky
    // instead.
    const byte PhaseStep = 1;

    static void Main()
    {
        Surface.Initialize();
        Lcd.SetPalette(0xE4);
        if (Cgb.IsColor())
        {
            Cgb.TryEnableDoubleSpeed();
            Cgb.SetBackgroundColor(0, 0, 0x7FFF);
            Cgb.SetBackgroundColor(0, 1, 0x5AD6);
            Cgb.SetBackgroundColor(0, 2, 0x318C);
            Cgb.SetBackgroundColor(0, 3, 0x1084);
        }
        Lcd.On();
        byte phase = 24;
        while (true)
        {
            Surface.Clear();
            CubeRenderer.Render(phase);
            Surface.Present();
            phase += PhaseStep;
        }
    }
}
