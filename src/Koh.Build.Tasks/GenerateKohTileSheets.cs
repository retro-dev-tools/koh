using Microsoft.Build.Framework;

namespace Koh.Build.Tasks;

/// <summary>
/// MSBuild task behind the Koh SDK's tile-sheet pipeline: converts every <c>@(KohTileSheet)</c>
/// PNG (see <see cref="TileSheetConverter"/>) into one generated source file
/// (<c>KohTileSheets.g.cs</c>, a <c>static class Art</c> in the game's root namespace) that
/// <c>Sdk.targets</c> adds to <c>@(Compile)</c> — so game code references <c>Art.GrassTiles</c>/
/// <c>Art.GrassColor0</c> and never hand-writes tile bytes, on both the ROM and the desktop
/// reference build.
/// </summary>
public sealed class GenerateKohTileSheets : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Sheets { get; set; } = [];

    [Required]
    public string OutputFile { get; set; } = "";

    [Required]
    public string Namespace { get; set; } = "";

    public override bool Execute()
    {
        try
        {
            var sheets = Sheets
                .Select(item => item.GetMetadata("FullPath"))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(TileSheetConverter.Convert)
                .ToList();
            var source = TileSheetConverter.GenerateSource(Namespace, sheets);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputFile)) ?? ".");
            File.WriteAllText(OutputFile, source);
            Log.LogMessage(
                MessageImportance.Normal,
                $"Koh tile sheets: {sheets.Count} sheet(s), "
                    + $"{sheets.Sum(s => s.TileCount)} tile(s) -> {OutputFile}"
            );
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Log.LogError(ex.Message);
            return false;
        }
    }
}
