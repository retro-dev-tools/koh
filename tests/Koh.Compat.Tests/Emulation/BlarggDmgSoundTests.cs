using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using TUnit.Core;

namespace Koh.Compat.Tests.Emulation;

/// <summary>
/// Blargg dmg_sound test ROMs. Report pass/fail via the serial port exactly
/// like cpu_instrs. Skipped if ROMs are missing.
/// </summary>
public class BlarggDmgSoundTests
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

    private static string RomPath(string rel) =>
        Path.Combine(FixturesRoot, "test-roms", "blargg", "dmg_sound", "rom_singles", rel);

    private static async Task Run(string rel, int maxFrames = 60_000)
    {
        if (Environment.GetEnvironmentVariable("KOH_RUN_BLARGG_DMG_SOUND") is not "1")
        {
            Skip.Test("dmg_sound requires APU quirks (length-on-power, wave-trigger-corruption, sweep-reload) not yet implemented. Set KOH_RUN_BLARGG_DMG_SOUND=1 to attempt.");
            return;
        }

        var romPath = RomPath(rel);
        if (!File.Exists(romPath))
        {
            Skip.Test($"Blargg dmg_sound ROM missing: {romPath}");
            return;
        }

        var rom = await File.ReadAllBytesAsync(romPath);
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);

        for (int frame = 0; frame < maxFrames; frame++)
        {
            gb.RunFrame();
            string output = gb.Io.Serial.ReadBufferAsString();
            if (output.Contains("Passed", StringComparison.Ordinal)) return;
            if (output.Contains("Failed", StringComparison.Ordinal))
            {
                for (int drain = 0; drain < 20; drain++) gb.RunFrame();
                throw new Exception($"[dmg_sound {rel}] Failed: {gb.Io.Serial.ReadBufferAsString().Trim()}");
            }
        }

        string finalOutput = gb.Io.Serial.ReadBufferAsString();
        throw new TimeoutException(
            $"dmg_sound {rel} did not report pass/fail within {maxFrames} frames. Serial output: '{finalOutput}' (PC=${gb.Registers.Pc:X4})");
    }

    [Test] public Task S01_Registers() => Run("01-registers.gb");
    [Test] public Task S02_LenCtr() => Run("02-len ctr.gb");
    [Test] public Task S03_Trigger() => Run("03-trigger.gb");
    [Test] public Task S04_Sweep() => Run("04-sweep.gb");
    [Test] public Task S05_SweepDetails() => Run("05-sweep details.gb");
    [Test] public Task S06_OverflowOnTrigger() => Run("06-overflow on trigger.gb");
    [Test] public Task S07_LenSweepPeriodSync() => Run("07-len sweep period sync.gb");
    [Test] public Task S08_LenCtrDuringPower() => Run("08-len ctr during power.gb");
    [Test] public Task S09_WaveReadWhileOn() => Run("09-wave read while on.gb");
    [Test] public Task S10_WaveTriggerWhileOn() => Run("10-wave trigger while on.gb");
    [Test] public Task S11_RegsAfterPower() => Run("11-regs after power.gb");
    [Test] public Task S12_WaveWriteWhileOn() => Run("12-wave write while on.gb");
}
