using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

/// <summary>
/// WASM implementation. Programmatic pickers are not available in the browser,
/// so <see cref="UsesNativeDialog"/> is false and UI components must fall back
/// to an &lt;input type=file&gt; element. SaveSaveStateAsync uses the existing
/// <c>kohDownloadFile</c> JS helper to trigger a browser download.
/// </summary>
public sealed class BrowserFileSystemAccess : IFileSystemAccess
{
    private readonly IJSRuntime _js;

    public BrowserFileSystemAccess(IJSRuntime js) { _js = js; }

    public bool UsesNativeDialog => false;

    public Task<PickedFile?> PickRomAsync() =>
        throw new NotSupportedException("BrowserFileSystemAccess does not support programmatic pickers; use <InputFile> in the UI.");

    public Task<PickedFile?> PickSaveStateAsync() =>
        throw new NotSupportedException("BrowserFileSystemAccess does not support programmatic pickers; use <InputFile> in the UI.");

    public async Task SaveSaveStateAsync(string defaultName, byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        await _js.InvokeVoidAsync("kohDownloadFile", defaultName, base64);
    }
}
