using Koh.GameBoy.Graphics;

namespace Koh.GameBoy.Framework;

/// <summary>
/// The game shell. The ideal entry point is <c>Game.Run(new TitleScene())</c> — the framework owns
/// the loop: boot, enter the first scene, screen on, then forever <c>scene.Update()</c> +
/// <see cref="EndFrame"/> (frame flush, input latch, clock tick, deferred scene commit). The
/// per-frame <c>scene.Update()</c> is a closed-world tag dispatch (compiler enabler E2 — see
/// <c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>).
///
/// A game may instead own its loop — <c>Game.Boot(); /* author */ Video.Start();
/// while (true) { /* update */ Game.EndFrame(); }</c> — <see cref="Boot"/>/<see cref="EndFrame"/>
/// are public precisely for that; scenes are a convenience, never a requirement.
/// </summary>
public static class Game
{
    private static Scene? _current;
    private static Scene? _next;

    /// <summary>Boot, enter <paramref name="first"/>, turn the screen on, and run the frame loop
    /// forever. The first scene's <see cref="Scene.Enter"/> runs with the LCD still OFF — load
    /// tiles/palettes and draw the initial layout there, free of vblank budgets.</summary>
    public static void Run(Scene first)
    {
        Boot();
        _current = first;
        first.Enter();
        Video.Start();
        while (true)
        {
            _current.Update();
            EndFrame();
        }
    }

    /// <summary>Request a scene change. Deferred: it commits inside the next <see cref="EndFrame"/>
    /// (current scene's <see cref="Scene.Exit"/>, then the new scene's <see cref="Scene.Enter"/>),
    /// so a change requested mid-<see cref="Scene.Update"/> is atomic at the frame boundary and the
    /// rest of the frame's code still runs against the old scene. The last request in a frame wins.</summary>
    public static void ChangeScene(Scene next) => _next = next;

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
    /// <see cref="Input"/> for the coming frame (right after vblank — the classic moment), tick
    /// <see cref="Clock"/>, and commit any pending <see cref="ChangeScene"/>.</summary>
    public static void EndFrame()
    {
        Video.EndFrame();
        Input.Update();
        Clock.Tick();
        CommitScene();
    }

    private static void CommitScene()
    {
        var next = _next;
        if (next == null)
            return;
        _next = null;
        var old = _current;
        _current = next;
        if (old != null)
            old.Exit();
        next.Enter();
    }
}
