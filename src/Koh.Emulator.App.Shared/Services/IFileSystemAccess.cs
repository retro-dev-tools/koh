namespace Koh.Emulator.App.Services;

/// <summary>
/// Host-agnostic file access for the standalone shell. The WASM build uses
/// the browser's &lt;input type=file&gt; flow; the MAUI build uses the platform
/// file picker. Both return the raw bytes so the emulator core stays
/// hosting-agnostic.
/// </summary>
public interface IFileSystemAccess
{
    /// <summary>
    /// True when <see cref="PickRomAsync"/> / <see cref="PickSaveStateAsync"/>
    /// open a platform-native file dialog. False for browser hosts where the
    /// UI must render an &lt;input type=file&gt; element instead (the WASM
    /// implementation does not support programmatic pickers).
    /// </summary>
    bool UsesNativeDialog { get; }

    Task<PickedFile?> PickRomAsync();
    Task<PickedFile?> PickSaveStateAsync();
    Task SaveSaveStateAsync(string defaultName, byte[] data);
}

public sealed record PickedFile(string FileName, byte[] Bytes);
