// Structural sanity checks for a rendered cube frame. The emulator is deterministic, so these checks are
// deliberately *not* a golden-image comparison (too brittle across future rendering tweaks); instead they
// check the shape a real rendered, animating, centered cube must have — a shape a blank, garbage, or
// frozen framebuffer cannot satisfy by accident:
//
//   1. at least two distinct non-background shades are on screen (the dithered cube faces);
//   2. every non-background pixel falls inside one bounding box, comfortably clear of the screen edge
//      (a centered cube, not full-screen noise) and of a plausible size (not a few stray pixels, not
//      the whole screen);
//   3. the non-background pixels form exactly one 8-connected region (a solid cube silhouette, not
//      stray/garbage pixels scattered outside it — the bounding box alone can't catch this, since it is
//      by construction the bbox of every non-background pixel, so nothing can ever fall outside it);
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

        var regions = CountNonBackgroundRegions(first, width, height, background);
        if (regions != 1)
            failures.Add(
                $"non-background pixels form {regions} disjoint 8-connected region(s), expected exactly "
                    + "1 (a solid cube silhouette): stray or garbage pixels are present outside the cube"
            );

        if (first.AsSpan().SequenceEqual(second))
            failures.Add("frame is byte-identical to a later frame: the cube is not animating");

        return failures;
    }

    /// <summary>Counts connected components (8-connectivity, so triangles/wireframe touching only at a
    /// corner still count as one region) among the non-background pixels. A real rendered cube — filled
    /// triangles plus the wireframe edges that border them — is always a single solid blob; any stray or
    /// garbage pixel elsewhere on screen shows up as an extra, disconnected region.</summary>
    private static int CountNonBackgroundRegions(byte[] rgb, int width, int height, int background)
    {
        var visited = new bool[width * height];
        var stack = new Stack<int>();
        var count = 0;
        for (var start = 0; start < width * height; start++)
        {
            if (visited[start])
                continue;
            var sx = start % width;
            var sy = start / width;
            if (PixelAt(rgb, width, sx, sy) == background)
                continue;

            count++;
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var idx = stack.Pop();
                var x = idx % width;
                var y = idx / width;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                        continue;
                    var nIdx = ny * width + nx;
                    if (visited[nIdx] || PixelAt(rgb, width, nx, ny) == background)
                        continue;
                    visited[nIdx] = true;
                    stack.Push(nIdx);
                }
            }
        }
        return count;
    }

    private static int PixelAt(byte[] rgb, int width, int x, int y)
    {
        var i = (y * width + x) * 3;
        return (rgb[i] << 16) | (rgb[i + 1] << 8) | rgb[i + 2];
    }
}
