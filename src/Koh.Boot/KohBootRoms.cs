using System.Reflection;

namespace Koh.Boot;

/// <summary>
/// Koh's own boot ROMs, written in Koh assembly in this directory and assembled by Koh's
/// own toolchain at build time.
///
/// They exist because Nintendo's boot ROM cannot be redistributed, and because an
/// emulator that fabricates post-boot state instead of executing boot code is lying about
/// the machine. They are an INDEPENDENT implementation: what they owe is the observable
/// hand-off state, not Nintendo's instruction sequence. One consequence is that DIV at
/// hand-off differs, because the cycle counts differ.
///
/// A user who owns a real dump can substitute it — see Koh.Emulator.App's --boot-rom
/// flags. Nothing here is ever downloaded or cached.
/// </summary>
public static class KohBootRoms
{
    private static readonly byte[] DmgBytes = Load("Koh.Boot.dmg_boot.bin");

    /// <summary>Koh's DMG-family boot ROM: 256 bytes, overlaying $0000-$00FF.</summary>
    public static ReadOnlySpan<byte> Dmg => DmgBytes;

    private static byte[] Load(string resourceName)
    {
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded boot ROM '{resourceName}' is missing. It is assembled from the "
                    + ".asm sources in src/Koh.Boot by the AssembleBootRoms target; a missing "
                    + "resource means that target did not run, or ran and failed silently."
            );

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
