namespace Koh.Linker;

/// <summary>
/// A raw image's placed sections do not fit its exact size.
///
/// Raw images are boot ROMs, whose size hardware fixes at 256 or 2304 bytes. A cartridge
/// that outgrows its size can simply be linked bigger; a boot ROM cannot. The DMG one in
/// particular hands control to the cartridge by letting PC run off its own end into
/// $0100, so its final instruction must sit at $00FE-$00FF — an image one byte too long
/// does not boot at all.
///
/// <see cref="Linker"/> converts this into a diagnostic; it is not expected to escape.
/// </summary>
public sealed class RawImageOverflowException(int neededBytes, int sizeBytes) : Exception
{
    /// <summary>Bytes the placed sections actually need.</summary>
    public int NeededBytes { get; } = neededBytes;

    /// <summary>The exact size the image was required to be.</summary>
    public int SizeBytes { get; } = sizeBytes;

    public override string Message =>
        $"Raw image does not fit: the placed sections need {NeededBytes} bytes "
        + $"(${NeededBytes:X}), but the image must be exactly {SizeBytes} bytes "
        + $"(${SizeBytes:X}). Remove {NeededBytes - SizeBytes} bytes, or link without "
        + "--raw if this is not a boot ROM.";
}
