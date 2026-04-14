using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using TUnit.Core;

namespace Koh.Compat.Tests.Emulation;

/// <summary>
/// Mooneye acceptance test runner. Mooneye ROMs signal pass/fail via a
/// Fibonacci register pattern after executing a LD B,B breakpoint opcode
/// ($40). On success the registers hold B=3, C=5, D=8, E=13, H=21, L=34.
/// Skipped when the corresponding ROM is missing.
/// </summary>
public class MooneyeTests
{
    private static readonly string FixturesRoot = LocateFixturesRoot();
    private static readonly string MtsRoot = LocateMtsRoot(FixturesRoot);

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

    private static string LocateMtsRoot(string fixturesRoot)
    {
        var mooneyeDir = Path.Combine(fixturesRoot, "test-roms", "mooneye");
        if (!Directory.Exists(mooneyeDir)) return mooneyeDir;
        // Mooneye releases extract to a versioned subdirectory; pick the first one.
        var versioned = Directory.EnumerateDirectories(mooneyeDir, "mts-*").FirstOrDefault();
        return versioned ?? mooneyeDir;
    }

    /// <summary>
    /// Tests known to fail pending per-M-cycle memory timing (same refactor
    /// that gates the Blargg timing suite). Skipped by default; opt in with
    /// KOH_RUN_MOONEYE_TIMING=1 to see their current state.
    /// </summary>
    private static readonly HashSet<string> KnownTimingFailures = new(StringComparer.OrdinalIgnoreCase)
    {
        "acceptance/bits/unused_hwio-GS.gb",
        "acceptance/oam_dma/reg_read.gb",
        "acceptance/oam_dma/sources-GS.gb",
        "acceptance/interrupts/ie_push.gb",
        "acceptance/timer/tima_write_reloading.gb",
        "acceptance/timer/tma_write_reloading.gb",
    };

    private static async Task RunMooneyeTest(string relPath, int maxFrames = 4_000)
    {
        if (KnownTimingFailures.Contains(relPath) &&
            Environment.GetEnvironmentVariable("KOH_RUN_MOONEYE_TIMING") is not "1")
        {
            Skip.Test("Pending per-M-cycle memory timing refactor. Set KOH_RUN_MOONEYE_TIMING=1 to run.");
            return;
        }

        var romPath = Path.Combine(MtsRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(romPath))
        {
            Skip.Test($"Mooneye ROM missing: {romPath}");
            return;
        }

        var rom = await File.ReadAllBytesAsync(romPath);
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);

        // Mooneye ROMs use "LD B,B" ($40) as a soft breakpoint to signal the end
        // of a test. We emulate by running frames until registers carry the
        // Fibonacci pattern or the deadline expires.
        for (int frame = 0; frame < maxFrames; frame++)
        {
            gb.RunFrame();
            if (IsPass(gb)) return;
            if (IsFail(gb))
                throw new Exception($"[Mooneye {relPath}] Failed: B={gb.Registers.B:X2} C={gb.Registers.C:X2} D={gb.Registers.D:X2} E={gb.Registers.E:X2} H={gb.Registers.H:X2} L={gb.Registers.L:X2}");
        }

        throw new TimeoutException($"Mooneye test {relPath} timed out at PC=${gb.Registers.Pc:X4}, regs B={gb.Registers.B:X2} C={gb.Registers.C:X2} D={gb.Registers.D:X2} E={gb.Registers.E:X2} H={gb.Registers.H:X2} L={gb.Registers.L:X2}");
    }

    private static bool IsPass(GameBoySystem gb) =>
        gb.Registers.B == 3 && gb.Registers.C == 5 &&
        gb.Registers.D == 8 && gb.Registers.E == 13 &&
        gb.Registers.H == 21 && gb.Registers.L == 34;

    private static bool IsFail(GameBoySystem gb) =>
        gb.Registers.B == 0x42 && gb.Registers.C == 0x42 &&
        gb.Registers.D == 0x42 && gb.Registers.E == 0x42 &&
        gb.Registers.H == 0x42 && gb.Registers.L == 0x42;

    [Test] public Task Bits_Mem_Oam() => RunMooneyeTest("acceptance/bits/mem_oam.gb");
    [Test] public Task Bits_Reg_F() => RunMooneyeTest("acceptance/bits/reg_f.gb");
    [Test] public Task Bits_Unused_Hwio_GS() => RunMooneyeTest("acceptance/bits/unused_hwio-GS.gb");

    [Test] public Task OamDma_Basic() => RunMooneyeTest("acceptance/oam_dma/basic.gb");
    [Test] public Task OamDma_Reg_Read() => RunMooneyeTest("acceptance/oam_dma/reg_read.gb");
    [Test] public Task OamDma_Sources() => RunMooneyeTest("acceptance/oam_dma/sources-GS.gb");

    [Test] public Task Interrupts_IePush() => RunMooneyeTest("acceptance/interrupts/ie_push.gb");

    [Test] public Task Timer_DivWrite() => RunMooneyeTest("acceptance/timer/div_write.gb");
    [Test] public Task Timer_RapidToggle() => RunMooneyeTest("acceptance/timer/rapid_toggle.gb");
    [Test] public Task Timer_Tim00() => RunMooneyeTest("acceptance/timer/tim00.gb");
    [Test] public Task Timer_Tim00_DivTrigger() => RunMooneyeTest("acceptance/timer/tim00_div_trigger.gb");
    [Test] public Task Timer_Tim01() => RunMooneyeTest("acceptance/timer/tim01.gb");
    [Test] public Task Timer_Tim01_DivTrigger() => RunMooneyeTest("acceptance/timer/tim01_div_trigger.gb");
    [Test] public Task Timer_Tim10() => RunMooneyeTest("acceptance/timer/tim10.gb");
    [Test] public Task Timer_Tim10_DivTrigger() => RunMooneyeTest("acceptance/timer/tim10_div_trigger.gb");
    [Test] public Task Timer_Tim11() => RunMooneyeTest("acceptance/timer/tim11.gb");
    [Test] public Task Timer_Tim11_DivTrigger() => RunMooneyeTest("acceptance/timer/tim11_div_trigger.gb");
    [Test] public Task Timer_TimaReload() => RunMooneyeTest("acceptance/timer/tima_reload.gb");
    [Test] public Task Timer_TimaWriteReloading() => RunMooneyeTest("acceptance/timer/tima_write_reloading.gb");
    [Test] public Task Timer_TmaWriteReloading() => RunMooneyeTest("acceptance/timer/tma_write_reloading.gb");
}
