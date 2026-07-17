using System.Text;
using Koh.Debugger.Session;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Debug;
using Koh.Emulator.Core.Joypad;
using Koh.Emulator.Core.Ppu;
using Koh.Linker.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Koh.Emulator.App;

/// <summary>
/// Headless ("no window") entry point for <c>Koh.Emulator.App</c>: load a ROM, run it for a fixed
/// number of frames with optional scripted joypad input, then optionally dump a screenshot and/or a
/// source-mapped mode-3 (VRAM write during PPU Drawing) violation report. Mirrors the existing
/// TUnit harness pattern (<c>Board2048RenderDiagnosticTests</c>/<c>Acid2Tests</c>) but as a reusable
/// CLI path instead of a throwaway test, per
/// <c>docs/superpowers/specs/2026-07-16-koh-debug-tooling-design.md</c> section 1.
///
/// Deliberately does NOT call <see cref="GameBoySystem.ArmBootAnimation"/> — like tests, the DAP
/// debugger, and every other headless caller, this expects PC=$0100 to execute starting on the very
/// first frame with no boot logo/chime.
/// </summary>
public static class HeadlessRunner
{
    public static int Run(
        string romPath,
        string? screenshotPath,
        int frames,
        string? inputScriptPath,
        bool mode3ReportRequested,
        string? mode3ReportPath
    )
    {
        if (!File.Exists(romPath))
        {
            Console.Error.WriteLine($"ROM not found: {romPath}");
            return 1;
        }

        var rom = File.ReadAllBytes(romPath);
        var cart = CartridgeFactory.Load(rom);
        // Same hardware-mode selection as EmulatorApp.LoadRomFromDisk: a cartridge whose header sets
        // the CGB flag boots in CGB mode regardless of file extension.
        var mode = cart.Header.CgbFlag ? HardwareMode.Cgb : HardwareMode.Dmg;
        var gb = new GameBoySystem(mode, cart);

        Mode3WriteGuard? guard = null;
        if (mode3ReportRequested)
        {
            guard = new Mode3WriteGuard(gb);
            gb.Mmu.Hook = guard;
        }

        var actions = ParseInputScript(inputScriptPath);
        int actionIndex = 0;

        for (int frame = 0; frame < frames; frame++)
        {
            while (actionIndex < actions.Count && actions[actionIndex].Frame == frame)
            {
                var action = actions[actionIndex];
                if (action.IsPress)
                    gb.JoypadPress(action.Button);
                else
                    gb.JoypadRelease(action.Button);
                actionIndex++;
            }
            gb.RunFrame();
        }

        if (screenshotPath is not null)
        {
            byte[] pixels = gb.Framebuffer.Front.ToArray();
            using var img = Image.LoadPixelData<Rgba32>(
                pixels,
                Framebuffer.Width,
                Framebuffer.Height
            );
            img.Save(screenshotPath);
        }

        if (mode3ReportRequested)
        {
            string report = BuildMode3Report(gb, guard!, romPath);
            if (mode3ReportPath is not null)
                File.WriteAllText(mode3ReportPath, report);
            else
                Console.WriteLine(report);
        }

        return 0;
    }

    private readonly record struct InputAction(int Frame, JoypadButton Button, bool IsPress);

    /// <summary>
    /// Parses "frame:button:press|release" lines (blank lines and lines starting with '#' ignored),
    /// sorted by frame so the run loop can apply same-frame actions in file order.
    /// </summary>
    private static List<InputAction> ParseInputScript(string? path)
    {
        var actions = new List<InputAction>();
        if (path is null)
            return actions;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            string[] parts = line.Split(':');
            if (parts.Length != 3)
                throw new FormatException($"Invalid input script line: '{rawLine}'");

            int frame = int.Parse(parts[0]);
            var button = Enum.Parse<JoypadButton>(parts[1], ignoreCase: true);
            string edge = parts[2].Trim();
            bool isPress = edge.Equals("press", StringComparison.OrdinalIgnoreCase);
            if (!isPress && !edge.Equals("release", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Invalid press/release token: '{rawLine}'");

            actions.Add(new InputAction(frame, button, isPress));
        }

        // Stable sort: file order is preserved for actions sharing a frame.
        return actions.OrderBy(a => a.Frame).ToList();
    }

    private static readonly string[] RegionOrder =
    [
        "$8000-$97FF (tile data)",
        "$9800-$9BFF (BG tilemap)",
        "$9C00-$9FFF (window tilemap)",
    ];

    private static string RegionOf(ushort address) =>
        address switch
        {
            >= 0x8000 and < 0x9800 => RegionOrder[0],
            >= 0x9800 and < 0x9C00 => RegionOrder[1],
            >= 0x9C00 and < 0xA000 => RegionOrder[2],
            _ => "other",
        };

    private static string BuildMode3Report(GameBoySystem gb, Mode3WriteGuard guard, string romPath)
    {
        var violations = guard.Violations;
        var sb = new StringBuilder();
        sb.AppendLine($"Mode-3 write violations: {violations.Count} total");
        sb.AppendLine();
        sb.AppendLine("By region:");

        var regionCounts = violations
            .GroupBy(v => RegionOf(v.Address))
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var region in RegionOrder)
            sb.AppendLine($"  {region}: {regionCounts.GetValueOrDefault(region)}");
        if (regionCounts.TryGetValue("other", out int otherCount) && otherCount > 0)
            sb.AppendLine($"  other: {otherCount}");

        sb.AppendLine();
        sb.AppendLine("By source location:");

        var resolver = SymbolResolver.TryLoad(romPath);
        var grouped = violations
            .GroupBy(v => resolver.DisplayKeyFor(gb, v.Pc))
            .OrderByDescending(g => g.Count());
        foreach (var group in grouped)
            sb.AppendLine($"  {group.Count()}  {group.Key}");

        return sb.ToString();
    }

    /// <summary>
    /// Best-effort PC -> "FunctionName (file:line)" resolution against a loaded <c>.kdbg</c>.
    /// <see cref="SymbolMap"/> only exposes an exact-key address lookup (<c>LookupByAddress</c>),
    /// which essentially never hits for a violation's mid-function PC, so this does its own
    /// nearest-symbol-at-or-below scan over <see cref="SymbolMap.All"/> grouped by bank.
    /// <see cref="SourceMap.Lookup(BankedAddress)"/> is already range-based and used as-is.
    /// </summary>
    private sealed class SymbolResolver
    {
        private readonly DebugInfoLoader? _loader;
        private readonly List<KdbgParsedSymbol> _symbolsByBankThenAddress;

        private SymbolResolver(DebugInfoLoader? loader)
        {
            _loader = loader;
            _symbolsByBankThenAddress = loader is null
                ? []
                : loader.SymbolMap.All.OrderBy(s => s.Bank).ThenBy(s => s.Address).ToList();
        }

        public static SymbolResolver TryLoad(string romPath)
        {
            string kdbgPath = Path.ChangeExtension(romPath, ".kdbg");
            if (!File.Exists(kdbgPath))
                return new SymbolResolver(null);

            var loader = new DebugInfoLoader();
            loader.Load(File.ReadAllBytes(kdbgPath));
            return new SymbolResolver(loader);
        }

        public string DisplayKeyFor(GameBoySystem gb, ushort pc)
        {
            byte bank = pc >= 0x4000 ? gb.Cartridge.CurrentRomBank : (byte)0;

            string? functionName = NearestEnclosingSymbolName(bank, pc);
            SourceLocation? location = _loader?.SourceMap.Lookup(new BankedAddress(bank, pc));

            // A single-bank (unbanked) ROM's code in $4000-$7FFF is placed by the linker via a
            // fixed-address section with no explicit bank, so DebugInfoPopulator records it at kdbg
            // bank 0 -- while Cartridge.CurrentRomBank reports the hardware convention (bank 1 for
            // that window, even on an MBC-less cart). Retry at kdbg bank 0 before giving up, so an
            // unbanked ROM's own functions still resolve instead of falling back to raw hex.
            if (bank != 0)
            {
                functionName ??= NearestEnclosingSymbolName(0, pc);
                location ??= _loader?.SourceMap.Lookup(new BankedAddress(0, pc));
            }

            if (functionName is not null && location is not null)
                return $"{functionName} ({Path.GetFileName(location.File)}:{location.Line})";
            if (location is not null)
                return $"{Path.GetFileName(location.File)}:{location.Line}";
            if (functionName is not null)
                return $"{functionName} (no line info)";
            return $"${pc:X4}";
        }

        private string? NearestEnclosingSymbolName(byte bank, ushort pc)
        {
            KdbgParsedSymbol? best = null;
            foreach (var sym in _symbolsByBankThenAddress)
            {
                if (sym.Bank != bank || sym.Address > pc)
                    continue;
                if (best is null || sym.Address > best.Address)
                    best = sym;
            }
            return best?.Name;
        }
    }
}
