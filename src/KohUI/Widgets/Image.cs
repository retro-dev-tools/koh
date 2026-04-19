namespace KohUI.Widgets;

/// <summary>
/// Raw-pixel display widget. Carries an RGBA8888 byte buffer and its
/// source dimensions; the backend blits the bytes onto a textured quad
/// without further processing.
///
/// <para>
/// The <paramref name="Pixels"/> array reference is expected to be
/// stable across frames — if the underlying source writes into the same
/// buffer each frame (as the emulator's <c>Framebuffer</c> does), the
/// reconciler won't emit per-pixel patches. The GL backend doesn't need
/// them: it re-uploads the texture on every paint pass regardless.
/// Higher-level DOM transport will need a frame-counter prop or a
/// binary side-channel — out of scope for v0.1 image support.
/// </para>
///
/// <para>
/// <paramref name="Scale"/> is an integer nearest-neighbor factor —
/// 2× for the Game Boy's 160×144 on a typical desktop, 3× for more
/// screen coverage. Fractional scales would smear the pixel grid, so
/// the type is deliberately <c>int</c>.
/// </para>
/// </summary>
public readonly struct Image<TMsg>(byte[] Pixels, int Width, int Height, int Scale = 1) : IView<TMsg>
{
    public readonly byte[] Pixels = Pixels;
    public readonly int Width = Width;
    public readonly int Height = Height;
    public readonly int Scale = Scale;

    public RenderNode Render() => RenderNode.Leaf("Image", Props.Of(
        ("pixels", (object)Pixels),
        ("width", Width),
        ("height", Height),
        ("scale", Scale)));
}
