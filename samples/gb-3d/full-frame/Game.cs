static class Game
{
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
        Benchmark.Begin(2);
        Lcd.On();
        byte phase = 24;
        while (true)
        {
            Surface.Clear();
            CubeRenderer.Render(phase);
            Surface.Present();
            phase += 3;
        }
    }
}
