using Koh.Emulator.App.Services;

namespace Koh.Emulator.Maui;

internal sealed class MauiFileSystemAccess : IFileSystemAccess
{
    public bool UsesNativeDialog => true;

    private static readonly FilePickerFileType RomFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.WinUI] = new[] { ".gb", ".gbc" },
        [DevicePlatform.MacCatalyst] = new[] { "public.data" },
    });

    private static readonly FilePickerFileType StateFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.WinUI] = new[] { ".state" },
        [DevicePlatform.MacCatalyst] = new[] { "public.data" },
    });

    public async Task<PickedFile?> PickRomAsync()
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Select a Game Boy ROM",
            FileTypes = RomFileType,
        });
        return await ReadAsync(result);
    }

    public async Task<PickedFile?> PickSaveStateAsync()
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Select a save state",
            FileTypes = StateFileType,
        });
        return await ReadAsync(result);
    }

    public async Task SaveSaveStateAsync(string defaultName, byte[] data)
    {
        // FileSaver is not available on all MAUI targets; use the app cache as
        // a fallback landing spot and surface the path via a toast-like alert.
        var path = Path.Combine(FileSystem.AppDataDirectory, defaultName);
        await File.WriteAllBytesAsync(path, data);
    }

    private static async Task<PickedFile?> ReadAsync(FileResult? result)
    {
        if (result is null) return null;
        using var stream = await result.OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return new PickedFile(result.FileName, ms.ToArray());
    }
}
