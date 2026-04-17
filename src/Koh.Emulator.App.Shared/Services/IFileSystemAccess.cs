namespace Koh.Emulator.App.Services;

/// <summary>
/// Host-agnostic file access for the standalone shell. The WASM build uses
/// the browser's &lt;input type=file&gt; flow; the MAUI build uses the platform
/// file picker. Both return the raw bytes so the emulator core stays
/// hosting-agnostic.
/// </summary>
public interface IFileSystemAccess
{
    Task<PickedFile?> PickRomAsync();
    Task<PickedFile?> PickSaveStateAsync();
    Task SaveSaveStateAsync(string defaultName, byte[] data);
}

public sealed record PickedFile(string FileName, byte[] Bytes);
