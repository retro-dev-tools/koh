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

    // Task-returning methods report errors via a faulted Task instead of a
    // synchronous throw. The guards in RomFilePicker / SaveStateControls make
    // this a latent hazard today rather than an active bug, but a future
    // Task.WhenAny / ContinueWith caller that skipped the UsesNativeDialog
    // check would otherwise see the exception escape on the call stack
    // instead of at the await site. Framework Design Guidelines §7.2.
    public Task<PickedFile?> PickRomAsync() =>
        Task.FromException<PickedFile?>(new NotSupportedException(
            "BrowserFileSystemAccess does not support programmatic pickers; use <InputFile> in the UI."));

    public Task<PickedFile?> PickSaveStateAsync() =>
        Task.FromException<PickedFile?>(new NotSupportedException(
            "BrowserFileSystemAccess does not support programmatic pickers; use <InputFile> in the UI."));

    public async Task SaveSaveStateAsync(string defaultName, byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        await _js.InvokeVoidAsync("kohDownloadFile", defaultName, base64);
    }
}
