// double-buffered/Game.cs and double-buffered/CubeRenderer.cs are LOCAL overrides of the shared
// shared/Game.cs and shared/CubeRenderer.cs used by full-frame/racing-beam — this variant is retrofitted
// onto Koh.GameBoy.Graphics (docs/superpowers/specs/2026-07-15-graphics-library-design.md §5, item 2),
// replacing the hand-rolled Surface.cs page-flip surface with Canvas.Init(..., CanvasMode.DoubleBuffered)
// + Canvas.Present(). See CubeDoubleBuffered.csproj, which Excludes the two shared files this overrides,
// and Cube3dTests.ReadDemo, which skips a shared file when a same-named local file shadows it.
using Koh.GameBoy.Graphics;

static class Game
{
    // Rotation phase step per render+present cycle — see shared/Game.cs's remarks for the full
    // derivation (gcd(step, 256) = 1, tuned for the CGB cadences). Kept identical here; this variant's
    // own cadence figures (documented in samples/gb-3d/verify/Program.cs) are unaffected by the port.
    const byte PhaseStep = 1;

    static void Main()
    {
        Video.Init(); // LCD off; both tilemaps + OAM cleared; all sprites hidden; CGB detected/cached
        if (Video.IsCgb)
            Cgb.TryEnableDoubleSpeed();

        // Dual-authored: the same CGB gradient the original hand-rolled Cgb.SetBackgroundColor(0, ...)
        // boot dance used, and the original Lcd.SetPalette(0xE4) identity ramp on DMG.
        Palettes.SetBg(0, 0x7FFF, 0x5AD6, 0x318C, 0x1084, 0xE4);

        // Replaces Surface.Initialize()'s ~15 lines (VRAM tile-window clear, tilemap layout, blank
        // border tile, initial hidden page) with one call — same 12x10 grid Surface.cs hand-picked.
        Canvas.Init(12, 10, CanvasMode.DoubleBuffered);

        Video.Start();

        byte phase = 24;
        while (true)
        {
            Canvas.Clear(0);
            CubeRenderer.Render(phase);
            Canvas.Present();
            phase += PhaseStep;
        }
    }
}
