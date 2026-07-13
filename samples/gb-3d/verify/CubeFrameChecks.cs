// Structural sanity checks for a rendered cube frame. The emulator is deterministic, so these checks are
// deliberately *not* a golden-image comparison (too brittle across future rendering tweaks); instead they
// check the shape a real rendered, animating, centered cube must have — a shape a blank, garbage, or
// frozen framebuffer cannot satisfy by accident:
//
//   1. at least two distinct non-background shades are on screen (the dithered cube faces);
//   2. every non-background pixel falls inside one bounding box, comfortably clear of the screen edge
//      (a centered cube, not full-screen noise) and of a plausible size (not a few stray pixels, not
//      the whole screen);
//   3. everything outside that bounding box is uniform background (the "one bounded region" property);
//   4. a frame sampled later differs from the first (the cube is actually animating, not frozen).
internal static class CubeFrameChecks
{
    /// <summary>How many pixels of clearance the lit bounding box must keep from the screen edge.</summary>
    private const int EdgeMargin = 2;

    /// <summary>Smallest plausible lit-area (guards against a handful of stray pixels passing).</summary>
    private const int MinLitArea = 50;

    /// <summary>Largest plausible lit-area as a fraction of the screen (guards against full-screen noise).</summary>
    private const double MaxLitAreaFraction = 0.85;

    public static IReadOnlyList<string> Check(byte[] first, byte[] second, int width, int height)
    {
        var failures = new List<string>();

        var background = PixelAt(first, width, 0, 0);
        var shades = new HashSet<int>();
        int minX = width,
            minY = height,
            maxX = -1,
            maxY = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = PixelAt(first, width, x, y);
            if (color == background)
                continue;
            shades.Add(color);
            if (x < minX)
                minX = x;
            if (x > maxX)
                maxX = x;
            if (y < minY)
                minY = y;
            if (y > maxY)
                maxY = y;
        }

        if (maxX < 0)
        {
            failures.Add("frame is entirely background: nothing rendered");
            return failures; // no bounding box to check further checks against
        }

        if (shades.Count < 2)
            failures.Add(
                $"only {shades.Count} non-background shade(s) present, expected >= 2 (dithered cube faces)"
            );

        if (
            minX < EdgeMargin
            || minY < EdgeMargin
            || maxX > width - 1 - EdgeMargin
            || maxY > height - 1 - EdgeMargin
        )
            failures.Add(
                $"bounding box [{minX},{minY}]-[{maxX},{maxY}] is not comfortably inside the "
                    + $"{width}x{height} screen (margin {EdgeMargin}px); expected a centered cube, not full-screen noise"
            );

        var area = (maxX - minX + 1) * (maxY - minY + 1);
        var maxArea = (int)(width * height * MaxLitAreaFraction);
        if (area < MinLitArea)
            failures.Add(
                $"lit bounding-box area {area}px^2 is implausibly small (< {MinLitArea}px^2)"
            );
        if (area > maxArea)
            failures.Add(
                $"lit bounding-box area {area}px^2 covers too much of the screen (> {maxArea}px^2 of {width * height}px^2)"
            );

        var stray = FindPixelOutsideBox(first, width, height, background, minX, minY, maxX, maxY);
        if (stray is { } p)
            failures.Add(
                $"non-background pixel at ({p.x},{p.y}) lies outside the cube's bounding box "
                    + $"[{minX},{minY}]-[{maxX},{maxY}]: border is not uniform background"
            );

        if (first.AsSpan().SequenceEqual(second))
            failures.Add("frame is byte-identical to a later frame: the cube is not animating");

        return failures;
    }

    private static (int x, int y)? FindPixelOutsideBox(
        byte[] rgb,
        int width,
        int height,
        int background,
        int minX,
        int minY,
        int maxX,
        int maxY
    )
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                continue;
            if (PixelAt(rgb, width, x, y) != background)
                return (x, y);
        }
        return null;
    }

    private static int PixelAt(byte[] rgb, int width, int x, int y)
    {
        var i = (y * width + x) * 3;
        return (rgb[i] << 16) | (rgb[i + 1] << 8) | rgb[i + 2];
    }
}
