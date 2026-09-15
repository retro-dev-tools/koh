using Koh.Boot;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Boot;

namespace Koh.Emulator.App;

/// <summary>
/// Chooses the boot ROM for a machine's mode: a user-supplied dump if given, else Koh's own.
/// Per-family slots because mode is resolved per cartridge.
/// </summary>
public sealed class BootRomResolver(string? anyPath, string? dmgPath, string? cgbPath)
{
    /// <exception cref="FileNotFoundException">A supplied path does not exist.</exception>
    /// <exception cref="ArgumentException">A supplied file is not a boot ROM size.</exception>
    public BootRom Resolve(HardwareMode mode)
    {
        string? path = anyPath ?? (mode == HardwareMode.Cgb ? cgbPath : dmgPath);

        if (path is null)
            return BootRom.FromBytes(mode == HardwareMode.Cgb ? KohBootRoms.Cgb : KohBootRoms.Dmg);

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Boot ROM '{path}' not found. Omit the flag to use Koh's own boot ROM.",
                path
            );

        return BootRom.FromBytes(File.ReadAllBytes(path));
    }
}
