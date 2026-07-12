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

var failed = false;
foreach (var (name, rom) in roms)
foreach (var (modeName, mode) in new[] { ("dmg", HardwareMode.Dmg), ("cgb", HardwareMode.Cgb) })
{
    if (args.Length != 0 && !args.Contains(name, StringComparer.OrdinalIgnoreCase))
        continue;
    var harness = new RomHarness(rom, mode);
    harness.Frames(600);
    var rgb = harness.CaptureRgb();
    var colors = new HashSet<int>();
    for (var i = 0; i < rgb.Length; i += 3)
        colors.Add((rgb[i] << 16) | (rgb[i + 1] << 8) | rgb[i + 2]);
    var visible = colors.Count >= 2;
    Console.WriteLine($"{name}/{modeName}: {colors.Count} colors, visible={visible}");
    harness.SaveScreenshotPng(Path.Combine(output, $"{name}-{modeName}.png"), 3);
    failed |= !visible;
}

return failed ? 1 : 0;
