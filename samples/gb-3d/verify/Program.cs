using Koh.Emulator.Core;
using Koh.Verify;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
var output = Path.Combine(root, "samples", "gb-3d", "verify", "out");
Directory.CreateDirectory(output);

// Frame counts to boot to, per ROM x hardware mode. The first is the steady-state snapshot everything
// gets checked against; the second is later so the two snapshots can be compared and must differ (the
// cube is animating, not a frozen/garbage framebuffer) — so the gap doubles as an ANIMATION-RATE
// assertion: it must clear one full render+present cycle, and tightening it pins how fast the demo has
// to flip. The software rasterizer is slow relative to hardware vblank (a full render+present pass
// takes well over 100 real frames for the larger viewports, measured up to ~180 in DMG single speed),
// so the gap has to clear that or two samples can land in the same still-rendering frame and the
// animation check would false-fail. double-buffered's flip rate differs per mode, so it gets per-mode
// counts (measured by frame-by-frame framebuffer diffing against these exact ROMs):
//   - cgb: Surface.Present() moves the whole 1920-byte page with one general-purpose DMA inside a
//     single vblank and page-flips via LCDC.4, so a flip lands every ~62-91 frames (rasterization-
//     bound; first content at ~frame 115). first=300 is steady-state; gap=200 asserts a flip happens
//     at least every ~3.3 s — a regression back to chunked CPU uploads would fail it.
//   - dmg: no DMA to VRAM exists, so the vblank-chunked CPU upload stands (3 bytes/vblank = 640
//     frames/page, plus ~150 render frames: a flip every ~790 frames, first at ~836). Slow is
//     accepted for the monochrome fallback; gap=900 clears one full cycle.
// full-frame/racing-beam stay at the original, fast-settling counts in both modes (a larger gap for
// them just burns test time for no reason, and — found empirically — runs the cube through enough of
// its rotation to hit CubeRenderer's near-camera perspective-divide singularity, an unrelated existing
// issue in the shared renderer, not something the VRAM-timing work touches).
var roms = new (string Name, string Rom, int DmgFirst, int DmgSecond, int CgbFirst, int CgbSecond)[]
{
    (
        "double-buffered",
        Path.Combine(root, "samples", "gb-3d", "double-buffered", "cube-double-buffered.gb"),
        1100,
        1100 + 900,
        300,
        300 + 200
    ),
    (
        "full-frame",
        Path.Combine(root, "samples", "gb-3d", "full-frame", "cube-full-frame.gb"),
        600,
        600 + 240,
        600,
        600 + 240
    ),
    (
        "racing-beam",
        Path.Combine(root, "samples", "gb-3d", "racing-beam", "cube-racing-beam.gb"),
        600,
        600 + 240,
        600,
        600 + 240
    ),
};

var failed = false;
foreach (var (name, rom, dmgFirst, dmgSecond, cgbFirst, cgbSecond) in roms)
foreach (var (modeName, mode) in new[] { ("dmg", HardwareMode.Dmg), ("cgb", HardwareMode.Cgb) })
{
    var (firstFrameCount, secondFrameCount) =
        mode == HardwareMode.Cgb ? (cgbFirst, cgbSecond) : (dmgFirst, dmgSecond);
    if (args.Length != 0 && !args.Contains(name, StringComparer.OrdinalIgnoreCase))
        continue;

    var harness = new RomHarness(rom, mode);
    var guard = new Mode3WriteGuard(harness.System);
    harness.System.Mmu.Hook = guard;
    harness.Frames(firstFrameCount);
    var first = harness.CaptureRgb();
    harness.SaveScreenshotPng(Path.Combine(output, $"{name}-{modeName}.png"), 3);
    harness.Frames(secondFrameCount - firstFrameCount);
    var second = harness.CaptureRgb();

    var failures = new List<string>(
        CubeFrameChecks.Check(
            first,
            second,
            Koh.Emulator.Core.Ppu.Framebuffer.Width,
            Koh.Emulator.Core.Ppu.Framebuffer.Height
        )
    );
    if (guard.Violations.Count != 0)
    {
        var (addr, val, ly) = guard.Violations[0];
        failures.Add(
            $"{guard.Violations.Count} VRAM write(s) landed during PPU mode 3 (dropped on real "
                + $"hardware); first at ${addr:X4}=${val:X2} LY={ly}"
        );
    }
    var ok = failures.Count == 0;
    failed |= !ok;

    Console.WriteLine(
        ok ? $"{name}/{modeName}: PASS" : $"{name}/{modeName}: FAIL ({string.Join("; ", failures)})"
    );
}

return failed ? 1 : 0;
