using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using TUnit.Core;

namespace Koh.Compat.Tests.Emulation;

/// <summary>
/// Blargg test ROM runner. Most ROMs report pass/fail via the serial port
/// ($FF02 start-transfer triggers a byte-send that we buffer); a few builds
/// (e.g. halt_bug.gb, mem_timing-2) never call the serial-print routine at
/// all, so <see cref="ScreenText"/> also scans the BG tile maps, where the
/// shared test shell always renders its "Passed"/"Failed" verdict as plain
/// ASCII. The harness runs frames until either source contains "Passed" or
/// "Failed" or the wall-clock deadline expires. Tests are skipped if the
/// corresponding ROM is missing.
/// </summary>
public class BlarggTests
{
    private static readonly string FixturesRoot = LocateFixturesRoot();

    private static string LocateFixturesRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppContext.BaseDirectory, "tests", "fixtures");
    }

    private static string RomPath(params string[] parts) =>
        Path.Combine(
            FixturesRoot,
            Path.Combine(new[] { "test-roms", "blargg" }.Concat(parts).ToArray())
        );

    private static async Task RunBlarggTest(string romRelPath, int maxFrames = 80_000)
    {
        var romPath = RomPath(romRelPath.Split('/'));
        if (!File.Exists(romPath))
        {
            Skip.Test($"Blargg ROM missing: {romPath}");
            return;
        }

        var rom = await File.ReadAllBytesAsync(romPath);
        var cart = CartridgeFactory.Load(rom);
        // A handful of Blargg ROMs (e.g. interrupt_time.gb, which measures
        // CGB double-speed dispatch timing) are CGB-only ($0143 = $C0) and
        // never even reach their pass/fail check under DMG. Most Blargg ROMs
        // set only the "CGB-enhanced but DMG-compatible" flag ($80), so key
        // off CgbOnly specifically rather than CgbFlag — using CgbFlag here
        // would also flip every already-passing DMG-compatible ROM to CGB.
        var mode = cart.Header.CgbOnly ? HardwareMode.Cgb : HardwareMode.Dmg;
        var gb = new GameBoySystem(mode, cart);

        for (int frame = 0; frame < maxFrames; frame++)
        {
            gb.RunFrame();
            string output = gb.Io.Serial.ReadBufferAsString() + ScreenText(gb);
            if (output.Contains("Passed", StringComparison.Ordinal))
                return;
            if (output.Contains("Failed", StringComparison.Ordinal))
            {
                // Let the test drain for a moment to capture the full failure
                // detail — Blargg prints the test name and failed sub-tests.
                for (int drain = 0; drain < 20; drain++)
                    gb.RunFrame();
                throw new Exception(
                    $"[Blargg {romRelPath}] Failed: {(gb.Io.Serial.ReadBufferAsString() + ScreenText(gb)).Trim()}"
                );
            }
        }

        string finalOutput = gb.Io.Serial.ReadBufferAsString() + ScreenText(gb);
        throw new TimeoutException(
            $"Blargg test {romRelPath} did not report pass/fail within {maxFrames} frames. Output: '{finalOutput}' (PC=${gb.Registers.Pc:X4})"
        );
    }

    /// <summary>
    /// Blargg's shared test shell always renders its "Passed"/"Failed" verdict
    /// as plain ASCII tile indices in both BG tile maps ($9800/$9C00), even on
    /// ROMs (e.g. halt_bug.gb) whose build never exercises the serial-print
    /// routine at all — its result vector is a bare RET there, so the serial
    /// buffer stays empty forever. Scanning the tile maps directly makes
    /// detection work regardless of which channel a given ROM build uses.
    /// </summary>
    private static string ScreenText(GameBoySystem gb)
    {
        var sb = new System.Text.StringBuilder(2 * 32 * 32);
        for (ushort baseAddr = 0x9800; baseAddr < 0xA000; baseAddr += 0x0400)
        {
            for (int i = 0; i < 32 * 32; i++)
            {
                byte b = gb.DebugReadByte((ushort)(baseAddr + i));
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : ' ');
            }
        }
        return sb.ToString();
    }

    [Test]
    public Task CpuInstrs_01_Special() => RunBlarggTest("cpu_instrs/individual/01-special.gb");

    [Test]
    public Task CpuInstrs_02_Interrupts() =>
        RunBlarggTest("cpu_instrs/individual/02-interrupts.gb");

    [Test]
    public Task CpuInstrs_03_Op_Sp_Hl() => RunBlarggTest("cpu_instrs/individual/03-op sp,hl.gb");

    [Test]
    public Task CpuInstrs_04_Op_R_Imm() => RunBlarggTest("cpu_instrs/individual/04-op r,imm.gb");

    [Test]
    public Task CpuInstrs_05_Op_Rp() => RunBlarggTest("cpu_instrs/individual/05-op rp.gb");

    [Test]
    public Task CpuInstrs_06_Ld_R_R() => RunBlarggTest("cpu_instrs/individual/06-ld r,r.gb");

    [Test]
    public Task CpuInstrs_07_Jumps() =>
        RunBlarggTest("cpu_instrs/individual/07-jr,jp,call,ret,rst.gb");

    [Test]
    public Task CpuInstrs_08_Misc() => RunBlarggTest("cpu_instrs/individual/08-misc instrs.gb");

    [Test]
    public Task CpuInstrs_09_Op_R_R() => RunBlarggTest("cpu_instrs/individual/09-op r,r.gb");

    [Test]
    public Task CpuInstrs_10_Bit_Ops() => RunBlarggTest("cpu_instrs/individual/10-bit ops.gb");

    [Test]
    public Task CpuInstrs_11_Op_A_Hl() => RunBlarggTest("cpu_instrs/individual/11-op a,(hl).gb");

    // After the M-cycle refactor, these Blargg timing tests pass by default.
    [Test]
    public Task InstrTiming() => RunBlarggTest("instr_timing/instr_timing.gb");

    [Test]
    public Task MemTiming() => RunBlarggTest("mem_timing/mem_timing.gb");

    // HALT-bug semantics (§7.4) are implemented in Sm83 and verified here.
    [Test]
    public Task HaltBug() => RunBlarggTest("halt_bug.gb");

    [Test]
    public Task MemTiming2() => RunBlarggTest("mem_timing-2/mem_timing.gb");

    // CGB-only ROM (header $0143=$C0); RunBlarggTest now runs it in CGB mode.
    [Test]
    public Task InterruptTime() => RunBlarggTest("interrupt_time/interrupt_time.gb");
}
