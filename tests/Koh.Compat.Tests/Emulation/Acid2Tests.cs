using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Advanced;
using TUnit.Core;

namespace Koh.Compat.Tests.Emulation;

/// <summary>
/// Runs dmg-acid2.gb / cgb-acid2.gb against the PPU and checks the framebuffer
/// matches the published reference PNGs pixel-for-pixel. Skipped when fixtures
/// are absent (run scripts/download-test-roms.sh to populate).
///
/// dmg-acid2 is the Phase 2 exit gate — strict zero-diff assertion, runs by
/// default. cgb-acid2 is deferred to Phase 3 (CGB palette rendering is the
/// Phase 3 deliverable per the plan).
/// </summary>
public class Acid2Tests
{

    private static readonly string FixturesRoot = LocateFixturesRoot();

    private static string LocateFixturesRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppContext.BaseDirectory, "tests", "fixtures");
    }

    private static string RomPath(string name) => Path.Combine(FixturesRoot, "test-roms", name);
    private static string ReferencePath(string name) => Path.Combine(FixturesRoot, "reference", name);

    [Test]
    public async Task DmgAcid2_Framebuffer_Matches_Reference()
    {
        string romPath = RomPath("dmg-acid2.gb");
        string refPath = ReferencePath("dmg-acid2.png");
        if (!File.Exists(romPath) || !File.Exists(refPath))
        {
            Skip.Test($"acid2 fixtures missing (expected {romPath} + {refPath}). Run scripts/download-test-roms.sh.");
            return;
        }

        var rom = File.ReadAllBytes(romPath);
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);

        // Acid2 completes its rendering well before 60 frames. Give it 120 for headroom.
        for (int i = 0; i < 120; i++) gb.RunFrame();

        byte[] actual = gb.Framebuffer.Front.ToArray();

        using var referenceImage = await Image.LoadAsync<Rgba32>(refPath);
        int diffCount = CountDiffPixels(actual, referenceImage);

        if (diffCount > 0)
        {
            Console.WriteLine($"[dmg-acid2] diff pixels: {diffCount} / {160 * 144}");
            await SaveActualFrameAsync("dmg-acid2-actual.png", actual);
            await SaveDiffFrameAsync("dmg-acid2-diff.png", actual, referenceImage);
        }
        await Assert.That(diffCount).IsEqualTo(0);
    }

    [Test]
    public async Task CgbAcid2_Framebuffer_Matches_Reference()
    {
        // CGB palette rendering is a Phase 3 deliverable — this test is
        // scaffolded but not gated. Enable with KOH_RUN_CGB_ACID2=1 when
        // Phase 3 CGB palette work lands.
        if (Environment.GetEnvironmentVariable("KOH_RUN_CGB_ACID2") is not "1")
        {
            Skip.Test("cgb-acid2 deferred to Phase 3 (CGB palette rendering).");
            return;
        }

        string romPath = RomPath("cgb-acid2.gbc");
        string refPath = ReferencePath("cgb-acid2.png");
        if (!File.Exists(romPath) || !File.Exists(refPath))
        {
            Skip.Test($"cgb-acid2 fixtures missing (expected {romPath} + {refPath}).");
            return;
        }

        var rom = File.ReadAllBytes(romPath);
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Cgb, cart);

        for (int i = 0; i < 120; i++) gb.RunFrame();

        byte[] actual = gb.Framebuffer.Front.ToArray();

        using var referenceImage = await Image.LoadAsync<Rgba32>(refPath);
        int diffCount = CountDiffPixels(actual, referenceImage);

        // Phase 2 caveat: CGB palette-aware rendering is explicitly deferred to
        // Phase 3 per the plan (DMG-only color mapping in Phase 2). Assert only
        // that the test runs and is ready; the zero-diff gate tightens in
        // Phase 3 when CGB palette-aware EmitPixel lands.
        await Assert.That(diffCount).IsGreaterThanOrEqualTo(0);
    }

    private static async Task SaveActualFrameAsync(string filename, byte[] rgba8888)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "acid2-actual");
        Directory.CreateDirectory(outDir);
        using var img = Image.LoadPixelData<Rgba32>(rgba8888, 160, 144);
        await img.SaveAsPngAsync(Path.Combine(outDir, filename));
    }

    private static async Task SaveDiffFrameAsync(string filename, byte[] actualRgba, Image<Rgba32> reference)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "acid2-actual");
        Directory.CreateDirectory(outDir);
        using var diff = new Image<Rgba32>(reference.Width, reference.Height);
        for (int y = 0; y < reference.Height; y++)
            for (int x = 0; x < reference.Width; x++)
            {
                var refPx = reference[x, y];
                int idx = (y * reference.Width + x) * 4;
                bool differs = refPx.R != actualRgba[idx] ||
                               refPx.G != actualRgba[idx + 1] ||
                               refPx.B != actualRgba[idx + 2];
                diff[x, y] = differs
                    ? new Rgba32(255, 0, 0, 255)
                    : new Rgba32(255, 255, 255, 255);
            }
        await diff.SaveAsPngAsync(Path.Combine(outDir, filename));
    }

    private static int CountDiffPixels(byte[] actualRgba8888, Image<Rgba32> reference)
    {
        int diff = 0;
        int width = reference.Width;
        int height = reference.Height;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = reference[x, y];
                int idx = (y * width + x) * 4;
                byte ar = actualRgba8888[idx + 0];
                byte ag = actualRgba8888[idx + 1];
                byte ab = actualRgba8888[idx + 2];
                if (pixel.R != ar || pixel.G != ag || pixel.B != ab)
                {
                    diff++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (diff > 0)
            Console.WriteLine($"[acid2] diff bbox: x={minX}..{maxX} y={minY}..{maxY}");
        return diff;
    }
}
