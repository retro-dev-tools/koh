// All-phases sweep of the shared CubeRenderer on the managed (desktop) build. The ROM checks in
// Program.cs sample two frame counts per ROM, which covers two rotation phases out of 256; this
// sweep drives the real shared renderer (compiled into this project, see Cube3dVerify.csproj)
// across every phase the demos can ever show (phase is a byte stepping by 3: gcd(3, 256) = 1, so
// all 256 values occur) on each sample's viewport geometry, and asserts the projected cube never
// degenerates: it must keep a clearance from every viewport edge (the historical failure — the
// small-viewport projection scale only budgeted for the average perspective distance, so near
// phases overflowed the 32x32 viewport and clipped flat against its edges) and its lit bounding
// box must stay in a plausible size band (neither collapsed nor blown up to the full surface).
internal static class PhaseSweepCheck
{
    /// <summary>Pixels of clearance the lit bounding box must keep from every viewport edge.</summary>
    private const int EdgeMargin = 2;

    /// <summary>Lit bounding-box area bounds as percent of the surface, across all phases.</summary>
    private const int MinAreaPercent = 10;
    private const int MaxAreaPercent = 75;

    /// <summary>Runs the sweep for all three sample viewports; prints one PASS/FAIL line each.</summary>
    public static bool Run()
    {
        var ok = true;
        foreach (
            var (name, width, height) in new[]
            {
                ("racing-beam", (byte)64, (byte)64),
                ("double-buffered", (byte)96, (byte)80),
                ("full-frame", (byte)128, (byte)120),
            }
        )
        {
            var failures = Sweep(width, height);
            Console.WriteLine(
                failures.Count == 0
                    ? $"phase-sweep {name} ({width}x{height}): PASS (256 phases)"
                    : $"phase-sweep {name} ({width}x{height}): FAIL ({string.Join("; ", failures.Take(3))}"
                        + (failures.Count > 3 ? $"; +{failures.Count - 3} more)" : ")")
            );
            ok &= failures.Count == 0;
        }
        return ok;
    }

    private static List<string> Sweep(byte width, byte height)
    {
        Surface.Configure(width, height);
        var failures = new List<string>();
        for (var phase = 0; phase < 256; phase++)
        {
            Surface.Clear();
            CubeRenderer.Render((byte)phase);
            int minX = width,
                minY = height,
                maxX = -1,
                maxY = -1;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (Surface.Pixel(x, y) == 0)
                    continue;
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
                failures.Add($"phase {phase}: nothing rendered");
                continue;
            }
            if (
                minX < EdgeMargin
                || minY < EdgeMargin
                || maxX > width - 1 - EdgeMargin
                || maxY > height - 1 - EdgeMargin
            )
                failures.Add(
                    $"phase {phase}: bbox [{minX},{minY}]-[{maxX},{maxY}] touches the "
                        + $"{width}x{height} viewport edge (< {EdgeMargin}px margin): projection overflow"
                );
            var area = (maxX - minX + 1) * (maxY - minY + 1);
            var surface = width * height;
            if (area * 100 < surface * MinAreaPercent || area * 100 > surface * MaxAreaPercent)
                failures.Add(
                    $"phase {phase}: bbox area {area}px^2 outside the plausible "
                        + $"{MinAreaPercent}%..{MaxAreaPercent}% band of {surface}px^2"
                );
        }
        return failures;
    }
}

// Managed stand-in for the per-sample Surface classes, just wide enough for CubeRenderer: the
// shared renderer only calls Width()/Height()/SetPixel(). Reconfigurable so one sweep binary
// covers all three sample viewport geometries.
internal static class Surface
{
    private static byte width = 96;
    private static byte height = 80;
    private static readonly byte[,] pixels = new byte[128, 128];

    internal static void Configure(byte w, byte h)
    {
        width = w;
        height = h;
    }

    internal static byte Width() => width;

    internal static byte Height() => height;

    internal static byte Pixel(int x, int y) => pixels[x, y];

    internal static void Clear() => Array.Clear(pixels);

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x >= width || y >= height)
            return;
        pixels[x, y] = color;
    }

    /// <summary>Reference implementation of the byte-granular FillSpan the real Surface.cs samples now
    /// ship: the naive per-pixel loop applying the identical ordered-dither rule FillTriangle used to
    /// apply directly (`((x ^ y) & 3) == 0 &amp;&amp; color > 1` draws `color - 1`, else `color`). Kept
    /// deliberately dumb (not span/byte-optimized) so this sweep exercises the shared renderer's new
    /// FillSpan call site meaningfully without duplicating the optimized bit-twiddling under test.</summary>
    internal static void FillSpan(byte y, byte xa, byte xb, byte color)
    {
        for (int x = xa; x <= xb; x++)
        {
            byte shaded = ((x ^ y) & 3) == 0 && color > 1 ? (byte)(color - 1) : color;
            SetPixel((byte)x, y, shaded);
        }
    }
}
