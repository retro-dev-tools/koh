namespace Koh.GameBoy.Framework;

/// <summary>
/// The frame clock: a 32-bit counter ticked once per <c>Game.EndFrame</c>. Exists because
/// <c>Video.FrameCount</c> is deliberately a byte (cheap animation phase) and wraps in 4.3 seconds —
/// game-logic timing wants a counter that effectively never wraps (~2.3 years at 60 fps).
/// </summary>
public static class Clock
{
    public static uint Frames;

    internal static void Tick() => Frames++;
}

/// <summary>
/// A countdown timer in frames, embeddable as a field or static (2 bytes of WRAM, zero = idle).
/// The game ticks it explicitly — <c>if (spawner.Tick()) { ... spawner.Start(40); }</c> — so there
/// is no hidden registry or per-frame framework cost for timers that don't exist.
/// </summary>
public struct Timer
{
    private ushort _remaining;

    public void Start(ushort frames) => _remaining = frames;

    public void Stop() => _remaining = 0;

    public bool Running => _remaining != 0;

    /// <summary>Advance one frame. True exactly on the tick that reaches zero — the fire-once
    /// signal to act and (optionally) restart. False while idle.</summary>
    public bool Tick()
    {
        if (_remaining == 0)
            return false;
        _remaining--;
        return _remaining == 0;
    }
}
