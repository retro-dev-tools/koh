namespace KohUI.Theme;

/// <summary>
/// Packed 24-bit color. Ordered R-G-B so both backends can trivially
/// lift it to their target format (CSS <c>#rrggbb</c> hex for the DOM,
/// byte triple for the OpenGL vertex colour attribute).
/// </summary>
public readonly record struct KohColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
}

/// <summary>
/// Single source of truth for KohUI's Windows-98 look. Both backends
/// consume this record:
///
/// <list type="bullet">
///   <item><b>DomBackend</b> emits the fields as CSS custom properties
///         on <c>:root</c> so 98.css rules continue to read
///         <c>var(--win98-bg)</c> and friends — keeping the hand-written
///         subset in sync with the theme.</item>
///   <item><b>GlBackend</b> maps the fields to RGB byte triples and
///         feeds them into the per-vertex colour attribute of a shared
///         OpenGL quad batch — bevels, panels, text tint all share the
///         same one-shader pipeline.</item>
/// </list>
///
/// Colour names follow Win98's own palette tokens from the Display
/// Properties / Appearance tab, which is what both 98.css and our
/// historical SDK-header references use.
/// </summary>
public sealed record Win98Theme
{
    /// <summary>Panel / window chrome body. "ButtonFace" / "3dFace" in Win98.</summary>
    public required KohColor Background { get; init; }

    /// <summary>Top-left inner highlight on bevels. "ButtonHilight" / "3dHilight".</summary>
    public required KohColor BevelHilite { get; init; }

    /// <summary>Bottom-right inner shadow. "ButtonShadow" / "3dShadow".</summary>
    public required KohColor BevelShadow { get; init; }

    /// <summary>Outer darkest line on raised/sunken bevels. "ButtonDkShadow" / "3dDkShadow".</summary>
    public required KohColor BevelDarkShadow { get; init; }

    /// <summary>Default text on panel backgrounds. "ButtonText" / "WindowText".</summary>
    public required KohColor Text { get; init; }

    /// <summary>Text-input background ("Window" in Win98 — always pure white on the default scheme).</summary>
    public required KohColor InputBackground { get; init; }

    /// <summary>Text on disabled controls.</summary>
    public required KohColor DisabledText { get; init; }

    /// <summary>Active title-bar gradient start (classic Win98 deep blue).</summary>
    public required KohColor TitleBarStart { get; init; }

    /// <summary>Active title-bar gradient end.</summary>
    public required KohColor TitleBarEnd { get; init; }

    /// <summary>Title-bar text colour.</summary>
    public required KohColor TitleBarText { get; init; }

    /// <summary>Desktop backdrop (classic teal); shown behind windowless chrome for preview purposes.</summary>
    public required KohColor Desktop { get; init; }

    /// <summary>Point size of the default UI font ("MS Sans Serif 8pt" in Win98).</summary>
    public required float UiFontSize { get; init; }

    /// <summary>Face name of the default UI font. Falls back to platform defaults if absent.</summary>
    public required string UiFontFamily { get; init; }

    // ─── Layout metrics ──────────────────────────────────────────────
    // Values shared by both backends so a number-change in one place
    // propagates to the GL backend's layout pass AND the CSS variable
    // block that drives the DomBackend. Historical Win98 defaults
    // where applicable.

    /// <summary>Inner content padding for panels / windows / containers.</summary>
    public required int Padding { get; init; }

    /// <summary>Gap inserted between children of a Stack.</summary>
    public required int Gap { get; init; }

    /// <summary>Bevel ring thickness (outer hilite/shadow + inner hilite/shadow = 2 rings × 1 px).</summary>
    public required int BevelWidth { get; init; }

    /// <summary>Minimum button width in px. Win98 standard: 75.</summary>
    public required int ButtonMinWidth { get; init; }

    /// <summary>Minimum button height in px. Win98 standard: 23.</summary>
    public required int ButtonMinHeight { get; init; }

    /// <summary>Glyph box size for CheckBox / RadioButton in px. Win98 standard: 13.</summary>
    public required int CheckRadioSize { get; init; }

    /// <summary>
    /// The canonical Win98 palette. Values lifted from the Display
    /// Properties &gt; Appearance &gt; Windows Standard scheme, matched
    /// against 98.css (jdan/98.css) for byte-exact consistency with the
    /// DomBackend's hand-maintained subset.
    /// </summary>
    public static Win98Theme Default { get; } = new()
    {
        Background       = new(0xc0, 0xc0, 0xc0),
        BevelHilite      = new(0xff, 0xff, 0xff),
        BevelShadow      = new(0x80, 0x80, 0x80),
        BevelDarkShadow  = new(0x00, 0x00, 0x00),
        Text             = new(0x00, 0x00, 0x00),
        InputBackground  = new(0xff, 0xff, 0xff),
        DisabledText     = new(0x7e, 0x7e, 0x7e),
        TitleBarStart    = new(0x00, 0x00, 0x80),
        TitleBarEnd      = new(0x10, 0x84, 0xd0),
        TitleBarText     = new(0xff, 0xff, 0xff),
        Desktop          = new(0x00, 0x80, 0x80),
        UiFontSize       = 11f,
        UiFontFamily     = "MS Sans Serif",
        Padding          = 4,
        Gap              = 4,
        BevelWidth       = 2,
        ButtonMinWidth   = 75,
        ButtonMinHeight  = 23,
        CheckRadioSize   = 13,
    };
}
