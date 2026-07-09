using Koh.Compiler.Ir;
using Koh.Compiler.Targets;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// The built-in <c>Hardware</c> surface: Game Boy memory-mapped registers exposed as
/// <c>Hardware.LCDC</c> etc. Each referenced register is materialized as a fixed-address global
/// (the I/O page is memory-mapped), so reads/writes lower to plain load/store. Also maps interrupt
/// kinds to their vector addresses.
/// </summary>
internal sealed class HardwareRegisters
{
    private readonly IrModule _module;
    private readonly Dictionary<string, IrGlobal> _cache = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, int> Addresses = new(StringComparer.Ordinal)
    {
        ["JOYP"] = 0xFF00,
        ["P1"] = 0xFF00,
        ["SB"] = 0xFF01,
        ["SC"] = 0xFF02,
        ["DIV"] = 0xFF04,
        ["TIMA"] = 0xFF05,
        ["TMA"] = 0xFF06,
        ["TAC"] = 0xFF07,
        ["IF"] = 0xFF0F,
        ["NR10"] = 0xFF10,
        ["NR11"] = 0xFF11,
        ["NR12"] = 0xFF12,
        ["NR13"] = 0xFF13,
        ["NR14"] = 0xFF14,
        ["NR21"] = 0xFF16,
        ["NR22"] = 0xFF17,
        ["NR23"] = 0xFF18,
        ["NR24"] = 0xFF19,
        ["NR30"] = 0xFF1A,
        ["NR31"] = 0xFF1B,
        ["NR32"] = 0xFF1C,
        ["NR33"] = 0xFF1D,
        ["NR34"] = 0xFF1E,
        ["NR41"] = 0xFF20,
        ["NR42"] = 0xFF21,
        ["NR43"] = 0xFF22,
        ["NR44"] = 0xFF23,
        ["NR50"] = 0xFF24,
        ["NR51"] = 0xFF25,
        ["NR52"] = 0xFF26,
        ["LCDC"] = 0xFF40,
        ["STAT"] = 0xFF41,
        ["SCY"] = 0xFF42,
        ["SCX"] = 0xFF43,
        ["LY"] = 0xFF44,
        ["LYC"] = 0xFF45,
        ["DMA"] = 0xFF46,
        ["BGP"] = 0xFF47,
        ["OBP0"] = 0xFF48,
        ["OBP1"] = 0xFF49,
        ["WY"] = 0xFF4A,
        ["WX"] = 0xFF4B,
        ["KEY1"] = 0xFF4D,
        ["VBK"] = 0xFF4F,
        ["HDMA1"] = 0xFF51,
        ["HDMA2"] = 0xFF52,
        ["HDMA3"] = 0xFF53,
        ["HDMA4"] = 0xFF54,
        ["HDMA5"] = 0xFF55,
        ["BCPS"] = 0xFF68,
        ["BCPD"] = 0xFF69,
        ["OCPS"] = 0xFF6A,
        ["OCPD"] = 0xFF6B,
        ["SVBK"] = 0xFF70,
        ["IE"] = 0xFFFF,
    };

    /// <summary>The built-in <c>Gb</c> memory regions: <c>Gb.Vram</c> etc. lower to a constant pointer
    /// at the region's base address, so pointer arithmetic over VRAM/tilemap/OAM needs no raw address
    /// literal in the source. The managed <c>Koh.GameBoy</c> runtime backs the same names with real
    /// buffers, so one source both compiles to a ROM and runs on the desktop.</summary>
    private static readonly Dictionary<string, int> Regions = new(StringComparer.Ordinal)
    {
        ["Vram"] = 0x8000,
        ["TileData"] = 0x8000,
        ["TileMap"] = 0x9800,
        ["TileMap1"] = 0x9C00,
        ["Wram"] = 0xC000,
        ["Oam"] = 0xFE00,
    };

    public HardwareRegisters(IrModule module) => _module = module;

    public bool IsRegister(string name) => Addresses.ContainsKey(name);

    public bool IsRegion(string name) => Regions.ContainsKey(name);

    /// <summary>Get (creating on first use) the fixed-address I8 global whose address is a region base;
    /// taking its <see cref="IrBuilder.GlobalRef"/> yields a <c>byte*</c> pointing at the region. The
    /// emitted global name is qualified (<c>Gb.Vram</c>) so it can never collide with a user-declared
    /// global: a top-level field lowers to a bare name, and a user class named <c>Gb</c> is reserved.</summary>
    public IrGlobal Region(string name) =>
        GetOrCreate("@region:" + name, "Gb." + name, Regions[name]);

    /// <summary>Get (creating on first use) the fixed-address global for a register. Like a region, the
    /// emitted name is qualified (<c>Hardware.LY</c>) so it can't collide with a same-named user global.</summary>
    public IrGlobal Register(string name) => GetOrCreate(name, "Hardware." + name, Addresses[name]);

    private IrGlobal GetOrCreate(string cacheKey, string name, int address)
    {
        if (!_cache.TryGetValue(cacheKey, out var global))
        {
            global = new IrGlobal(name, IrType.I8, AddressSpace.Default, fixedAddress: address);
            _module.Globals.Add(global);
            _cache[cacheKey] = global;
        }
        return global;
    }

    /// <summary>Map an interrupt kind name (e.g. "VBlank") to its vector address.</summary>
    public static int? InterruptVector(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "vblank" => 0x40,
            "stat" or "lcdstat" or "lcd" => 0x48,
            "timer" => 0x50,
            "serial" => 0x58,
            "joypad" => 0x60,
            _ => null,
        };
}
