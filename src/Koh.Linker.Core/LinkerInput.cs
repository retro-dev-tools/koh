using Koh.Core.Binding;

namespace Koh.Linker.Core;

/// <summary>
/// A single object file's contribution to the link. Wraps an EmitModel
/// with its source file path for diagnostics.
/// </summary>
public sealed class LinkerInput
{
    public string FilePath { get; }
    public EmitModel Model { get; }

    public LinkerInput(string filePath, EmitModel model)
    {
        FilePath = filePath;
        Model = model;
    }
}
