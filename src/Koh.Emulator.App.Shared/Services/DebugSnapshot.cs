using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Assembles a human-readable markdown snapshot of emulator runtime state —
/// CPU, PPU, interrupts, CGB banking / HDMA / palettes, cartridge header,
/// and some app-level counters. Designed to be copied to the clipboard and
/// pasted into a debugging conversation: focused on the state that usually
/// matters for diagnosing rendering / timing / audio issues, without
/// dumping entire VRAM or the ROM itself.
/// </summary>
public static class DebugSnapshot
{
    public static string Build(EmulatorHost host, string? audioStatsJson = null)
    {
        var sb = new StringBuilder(4096);
        var invar = CultureInfo.InvariantCulture;

        sb.AppendLine("## Koh debug snapshot");
        sb.AppendLine();
        sb.Append("- Captured: ").AppendLine(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", invar));
        sb.Append("- Runtime: .NET ").Append(Environment.Version).Append(" / ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        sb.AppendLine();

        var sys = host.System;
        if (sys is null)
        {
            sb.AppendLine("_No ROM loaded._");
            return sb.ToString();
        }

        // ─── Cartridge / ROM ─────────────────────────────────────────────
        var header = sys.Cartridge.Header;
        sb.AppendLine("### Cartridge");
        sb.Append("- Title: `").Append(Sanitize(header.Title)).AppendLine("`");
        sb.Append("- Mapper: ").AppendLine(header.MapperKind.ToString());
        sb.Append("- ROM banks: ").Append(header.RomBanks).Append("  RAM banks: ").AppendLine(header.RamBanks.ToString(invar));
        sb.Append("- CGB flag: ").Append(header.CgbFlag).Append(" (CGB-only: ").Append(header.CgbOnly).AppendLine(")");
        if (host.OriginalRom is { } rom)
        {
            var hash = SHA256.HashData(rom);
            sb.Append("- ROM size: ").Append(rom.Length).Append(" bytes  SHA256: `").Append(Hex(hash.AsSpan(0, 8))).AppendLine("…`");
        }
        sb.AppendLine();

        // ─── Runtime counters ────────────────────────────────────────────
        sb.AppendLine("### Runtime");
        sb.Append("- Mode: ").AppendLine(host.IsPaused ? "Paused" : "Running");
        sb.Append("- Reported FPS: ").AppendLine(host.Fps.ToString("0.00", invar));
        sb.Append("- CPU total T-cycles: ").AppendLine(sys.Cpu.TotalTCycles.ToString(invar));
        if (!string.IsNullOrEmpty(audioStatsJson))
            sb.Append("- Audio bridge: ").AppendLine(audioStatsJson);
        sb.AppendLine();

        // ─── CPU ─────────────────────────────────────────────────────────
        var r = sys.Registers;
        sb.AppendLine("### CPU");
        sb.Append("- PC=").Append(H16(r.Pc)).Append("  SP=").AppendLine(H16(r.Sp));
        sb.Append("- AF=").Append(H16(r.AF)).Append("  BC=").Append(H16(r.BC));
        sb.Append("  DE=").Append(H16(r.DE)).Append("  HL=").AppendLine(H16(r.HL));
        sb.Append("- Flags: ")
            .Append(r.FlagSet(Core.Cpu.CpuRegisters.FlagZ) ? "Z" : "-")
            .Append(r.FlagSet(Core.Cpu.CpuRegisters.FlagN) ? "N" : "-")
            .Append(r.FlagSet(Core.Cpu.CpuRegisters.FlagH) ? "H" : "-")
            .Append(r.FlagSet(Core.Cpu.CpuRegisters.FlagC) ? "C" : "-")
            .AppendLine();
        sb.Append("- IME=").Append(sys.Cpu.Ime ? "1" : "0")
          .Append("  Halted=").Append(sys.Cpu.Halted ? "1" : "0")
          .Append("  Stopped=").AppendLine(sys.Cpu.Stopped ? "1" : "0");
        var ints = sys.Mmu.Io.Interrupts;
        sb.Append("- IF=").Append(H8(ints.IF)).Append("  IE=").AppendLine(H8(ints.IE));
        sb.AppendLine();

        // ─── PPU ─────────────────────────────────────────────────────────
        var p = sys.Ppu;
        sb.AppendLine("### PPU");
        sb.Append("- Mode: ").Append(p.Mode).AppendLine();
        sb.Append("- LY=").Append(H8(p.LY)).Append("  LYC=").Append(H8(p.LYC));
        sb.Append("  SCX=").Append(H8(p.SCX)).Append("  SCY=").Append(H8(p.SCY));
        sb.Append("  WX=").Append(H8(p.WX)).Append("  WY=").AppendLine(H8(p.WY));
        sb.Append("- LCDC=").Append(H8(p.LCDC)).Append("  STAT=").Append(H8(p.Stat.Read(p.Mode, p.LY == p.LYC))).AppendLine();
        sb.Append("- DMG palettes: BGP=").Append(H8(p.BGP))
            .Append("  OBP0=").Append(H8(p.OBP0)).Append("  OBP1=").AppendLine(H8(p.OBP1));
        if (sys.Mode == HardwareMode.Cgb)
        {
            sb.Append("- CGB BG palette (8×4×BGR555): ").AppendLine(Hex(p.BgPalette.RawData));
            sb.Append("- CGB OBJ palette (8×4×BGR555): ").AppendLine(Hex(p.ObjPalette.RawData));
            sb.Append("- OPRI: ").AppendLine(H8(p.OPRI));
        }
        sb.AppendLine();

        // ─── CGB banking + HDMA ──────────────────────────────────────────
        if (sys.Mode == HardwareMode.Cgb)
        {
            sb.AppendLine("### CGB banking");
            sb.Append("- VBK (VRAM bank): ").Append(sys.Mmu.Banking.VramBank).Append("  SVBK (WRAM bank): ").AppendLine(sys.Mmu.Banking.WramBank.ToString(invar));

            var h = sys.Hdma;
            sb.AppendLine("### HDMA");
            sb.Append("- Active: ").Append(h.Active).Append("  Mode: ").Append(h.IsHBlankMode ? "HBlank" : "General").AppendLine();
            sb.Append("- Src: ").Append(H8(h.Source1)).Append(H8(h.Source2))
              .Append("  Dst: ").Append(H8(h.Dest1)).Append(H8(h.Dest2))
              .AppendLine();
            sb.Append("- Length reg (FF55): ").AppendLine(H8(h.ReadLengthRegister()));
            sb.AppendLine();
        }

        // ─── Cartridge mapper registers ──────────────────────────────────
        var cart = sys.Cartridge;
        sb.AppendLine("### Mapper registers");
        sb.Append("- BankLow: ").Append(H8(cart.Mbc1_BankLow))
          .Append("  BankHigh: ").Append(H8(cart.Mbc1_BankHigh))
          .Append("  Mode: ").Append(cart.Mbc1_Mode)
          .Append("  RAM enabled: ").AppendLine(cart.Mbc1_RamEnabled.ToString());
        sb.Append("- Current ROM bank: ").AppendLine(cart.CurrentRomBank.ToString(invar));

        sb.AppendLine();
        sb.AppendLine("<!-- end snapshot -->");
        return sb.ToString();
    }

    private static string H8(byte v)   => "$" + v.ToString("X2", CultureInfo.InvariantCulture);
    private static string H16(ushort v) => "$" + v.ToString("X4", CultureInfo.InvariantCulture);

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        Span<char> buf = bytes.Length <= 256 ? stackalloc char[bytes.Length * 2] : new char[bytes.Length * 2];
        const string Hx = "0123456789abcdef";
        for (int i = 0; i < bytes.Length; i++)
        {
            buf[i * 2]     = Hx[bytes[i] >> 4];
            buf[i * 2 + 1] = Hx[bytes[i] & 0x0F];
        }
        return new string(buf);
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "<unnamed>";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(ch < 0x20 || ch > 0x7E ? '?' : ch);
        return sb.ToString();
    }
}
