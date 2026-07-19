using Koh.GameBoy.Graphics;

namespace Koh.GameBoy.Framework;

/// <summary>
/// The game shell: one boot call and one required per-frame call, composing the Graphics frame
/// lifecycle with the framework's own frame state. Stage 0 (see
/// <c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>): games own their loop —
/// <c>Game.Boot(); /* author */ Video.Start(); while (true) { /* update */ Game.EndFrame(); }</c>.
/// Milestone M3 adds <c>Run(Scene)</c>/<c>ChangeScene</c> on top (the framework can compose these
/// fixed calls because every target is statically known; it cannot call game-supplied hooks until
/// closed-world devirtualization lands — and <see cref="Boot"/>/<see cref="EndFrame"/> stay public
/// for loop-owning games after that too).
/// </summary>
public static class Game
{
    /// <summary>Boot the machine into an authorable state: <see cref="Video.Init"/> (LCD off, maps
    /// and shadow OAM cleared, palettes defaulted, CGB detected), input forgotten, the frame clock
    /// zeroed, and <see cref="Rng"/> seeded from two DIV reads (a free-running 16 KiHz counter, so
    /// the pair carries boot-timing entropy; mix again on the first keypress for human timing).
    /// The screen stays off — load tiles and draw your first frame, then <see cref="Video.Start"/>.</summary>
    public static void Boot()
    {
        Video.Init();
        Input.Reset();
        Clock.Frames = 0;
        byte hi = Hardware.DIV;
        byte lo = Hardware.DIV;
        Rng.Seed((ushort)((hi << 8) | lo));
    }

    /// <summary>THE per-frame call, replacing a bare <see cref="Video.EndFrame"/> in the loop:
    /// flush the frame (vblank wait + OAM DMA + tilemap shadow flush), then latch
    /// <see cref="Input"/> for the coming frame (right after vblank — the classic moment), then
    /// tick <see cref="Clock"/>.</summary>
    public static void EndFrame()
    {
        Video.EndFrame();
        Input.Update();
        Clock.Tick();
    }
}
