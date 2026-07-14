using Koh.Emulator.Core;
using Koh.Verify;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
var output = Path.Combine(root, "samples", "gb-3d", "verify", "out");
Directory.CreateDirectory(output);

// Frame counts to boot to, per ROM x hardware mode. The first is the steady-state snapshot everything
// gets checked against; the second is later so the two snapshots can be compared and must differ (the
// cube is animating, not a frozen/garbage framebuffer) — so the gap doubles as an ANIMATION-RATE
// assertion: it must clear one full render+present cycle, and tightening it pins how fast the demo has
// to flip.
//
// Both columns are sized as first ~= boot + 3x-cadence, gap (second - first) ~= 2.5x-cadence, from a
// frame-by-frame framebuffer diff against these exact ROMs (2000 frames each, both modes): "boot" is
// the frame of the first observed content change; "cadence" is the steady-state interval between
// render+present cycles (the interval between framebuffer changes, once past startup — for
// full-frame/CGB, whose two-vblank GDMA halves each show up as a change, cadence is the sum of both
// half-transfers' deltas; racing-beam's per-scanline SCX wobble shows up as extra 1-frame deltas within
// a cycle and is excluded from "cadence"). Both budgets carry headroom above that formula (not tuned
// to the exact minimum) since a too-tight budget trades a faster CI run for flakiness the first time a
// phase lands on a slower-than-typical render — not worth it. Per ROM x mode, measured cadence ranged
// (min..max over the 2000-frame sample, not the full 256-phase cycle):
//   - double-buffered: cgb 19-47 frames/flip (one GDMA transfer per vblank flip, rasterization-bound);
//     dmg 340-344 frames/flip (the vblank-chunked Mem.Copy upload, ~275 frames/page at
//     PixelChunkSize=7, plus render). Kept at the already-generous 300/500 (cgb) and 1100/2000 (dmg).
//   - full-frame: cgb 23-51 frames/cycle (two-vblank GDMA halves, seam included); dmg 59-114
//     frames/cycle (one Lcd-off Mem.Copy(3840) present, ~16.5 frames of that, plus render).
//   - racing-beam: cgb 17-41 frames/cycle; dmg 33-80 frames/cycle (one Lcd-off Mem.Copy(1024) present,
//     plus render, plus the SCX wobble sequence which is itself paced to real hblank timing).
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
        450,
        450 + 300,
        300,
        300 + 200
    ),
    (
        "racing-beam",
        Path.Combine(root, "samples", "gb-3d", "racing-beam", "cube-racing-beam.gb"),
        400,
        400 + 300,
        300,
        300 + 150
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

failed |= !PhaseSweepCheck.Run();

return failed ? 1 : 0;
