namespace Koh.GameBoy.Framework;

/// <summary>
/// Frame-latched input over <see cref="Joypad.ReadAll"/>: <see cref="Update"/> reads the pad ONCE
/// per frame (called by <c>Game.EndFrame</c>, or directly by a game that owns its loop) and derives
/// held/pressed/released masks that every query then reads without touching hardware — fixing
/// <see cref="Joypad.Pressed"/>'s consume-once hazard (its second call in a frame loses the edges;
/// <see cref="Pressed"/> here answers consistently all frame). Queries are single-block leaf
/// methods, so the optimizer inlines them to a static-byte read + AND.
///
/// Repeat uses ONE shared timer over the whole held mask (reset whenever the mask changes), not a
/// timer per button — one WRAM byte instead of eight, and exactly right for the menu/d-pad use it
/// exists for. After <see cref="SetRepeat"/>(delay, interval): a button's first
/// <see cref="Repeated"/> is its press edge, the next comes <c>delay</c> frames later, then every
/// <c>interval</c> frames while the mask stays unchanged.
///
/// Internal per-frame flags are <c>byte</c>, not <c>bool</c> — the SM83 backend emits a measurably
/// worse branch sequence for <c>bool</c> negation (see <c>MapWriter</c>'s documented finding); the
/// public surface still returns <c>bool</c> (that is a branch either way). All state is mutable
/// statics (WRAM, zero at boot); zero means "defaults" for the repeat config so no <c>.cctor</c> is
/// needed.
/// </summary>
public static class Input
{
    private static byte _held;
    private static byte _pressed;
    private static byte _released;
    private static byte _lastHeld;
    private static byte _repeatTimer;
    private static byte _repeatFire; // 1 on the frames the shared repeat timer fires
    private static byte _repeatDelay; // 0 = default (15)
    private static byte _repeatInterval; // 0 = default (4)

    private const byte DefaultDelay = 15;
    private const byte DefaultInterval = 4;

    /// <summary>Latch this frame's pad state. Call exactly once per frame (<c>Game.EndFrame</c>
    /// does; don't also call it yourself when using the Game loop).</summary>
    public static void Update()
    {
        byte current = Joypad.ReadAll();
        byte previous = _held;
        _pressed = (byte)(current & (byte)~previous);
        _released = (byte)(previous & (byte)~current);
        _held = current;

        if (current == 0)
        {
            _lastHeld = 0;
            _repeatTimer = 0;
            _repeatFire = 0;
            return;
        }

        if (current != _lastHeld)
        {
            // New combination: Pressed() covers this frame's edge; arm the initial delay.
            _lastHeld = current;
            byte delay = _repeatDelay;
            _repeatTimer = delay != 0 ? delay : DefaultDelay;
            _repeatFire = 0;
            return;
        }

        _repeatTimer--;
        if (_repeatTimer == 0)
        {
            byte interval = _repeatInterval;
            _repeatTimer = interval != 0 ? interval : DefaultInterval;
            _repeatFire = 1;
        }
        else
        {
            _repeatFire = 0;
        }
    }

    /// <summary>Configure auto-repeat: first repeat <paramref name="delayFrames"/> after the press,
    /// then every <paramref name="intervalFrames"/>. Zero is coerced to 1.</summary>
    public static void SetRepeat(byte delayFrames, byte intervalFrames)
    {
        _repeatDelay = delayFrames != 0 ? delayFrames : (byte)1;
        _repeatInterval = intervalFrames != 0 ? intervalFrames : (byte)1;
    }

    public static bool Held(Button b) => (_held & (byte)b) != 0;

    /// <summary>True the one frame the button went down. Query as often as you like.</summary>
    public static bool Pressed(Button b) => (_pressed & (byte)b) != 0;

    /// <summary>True the one frame the button came up.</summary>
    public static bool Released(Button b) => (_released & (byte)b) != 0;

    /// <summary>The press edge, plus auto-repeats while held (see class remarks for the cadence).
    /// The one to use for menu navigation and 2048-style sliding.</summary>
    public static bool Repeated(Button b) =>
        (_pressed & (byte)b) != 0 || (_repeatFire != 0 && (_held & (byte)b) != 0);

    /// <summary>-1, 0, or +1 from the held Left/Right bits — movement code's natural shape.</summary>
    public static int DpadX()
    {
        int x = 0;
        if ((_held & (byte)Button.Right) != 0)
            x = 1;
        if ((_held & (byte)Button.Left) != 0)
            x -= 1;
        return x;
    }

    /// <summary>-1 (Up), 0, or +1 (Down) from the held Up/Down bits.</summary>
    public static int DpadY()
    {
        int y = 0;
        if ((_held & (byte)Button.Down) != 0)
            y = 1;
        if ((_held & (byte)Button.Up) != 0)
            y -= 1;
        return y;
    }

    /// <summary>Forget all latched state (boot / hard scene reset).</summary>
    internal static void Reset()
    {
        _held = 0;
        _pressed = 0;
        _released = 0;
        _lastHeld = 0;
        _repeatTimer = 0;
        _repeatFire = 0;
    }
}
