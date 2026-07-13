using Koh.Emulator.Core;
using Koh.Verify;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
var output = Path.Combine(root, "samples", "gb-3d", "verify", "out");
Directory.CreateDirectory(output);

var roms = new[]
{
    (
        "double-buffered",
        Path.Combine(root, "samples", "gb-3d", "double-buffered", "cube-double-buffered.gb")
    ),
    ("full-frame", Path.Combine(root, "samples", "gb-3d", "full-frame", "cube-full-frame.gb")),
    ("racing-beam", Path.Combine(root, "samples", "gb-3d", "racing-beam", "cube-racing-beam.gb")),
};

// Frame counts to boot to. The first is the steady-state snapshot everything gets checked against; the
// second is later so the two snapshots can be compared and must differ (the cube is animating, not a
// frozen/garbage framebuffer). The software rasterizer is slow relative to hardware vblank (a full
// render+present pass takes well over 100 real frames for the larger viewports, measured up to ~180 in
// DMG single speed), so the gap has to clear that or two samples can land in the same still-rendering
// frame and the animation check would false-fail.
const int FirstFrameCount = 600;
const int SecondFrameCount = FirstFrameCount + 240;

var failed = false;
foreach (var (name, rom) in roms)
foreach (var (modeName, mode) in new[] { ("dmg", HardwareMode.Dmg), ("cgb", HardwareMode.Cgb) })
{
    if (args.Length != 0 && !args.Contains(name, StringComparer.OrdinalIgnoreCase))
        continue;

    var harness = new RomHarness(rom, mode);
    harness.Frames(FirstFrameCount);
    var first = harness.CaptureRgb();
    harness.SaveScreenshotPng(Path.Combine(output, $"{name}-{modeName}.png"), 3);
    harness.Frames(SecondFrameCount - FirstFrameCount);
    var second = harness.CaptureRgb();

    var failures = CubeFrameChecks.Check(
        first,
        second,
        Koh.Emulator.Core.Ppu.Framebuffer.Width,
        Koh.Emulator.Core.Ppu.Framebuffer.Height
    );
    var ok = failures.Count == 0;
    failed |= !ok;

    Console.WriteLine(
        ok ? $"{name}/{modeName}: PASS" : $"{name}/{modeName}: FAIL ({string.Join("; ", failures)})"
    );
}

return failed ? 1 : 0;
