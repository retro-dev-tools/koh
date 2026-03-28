using Koh.Core.Binding;
using Koh.Core.Symbols;

namespace Koh.Emit;

/// <summary>
/// Writes an EmitModel to a .kobj binary stream.
/// </summary>
public sealed class KobjWriter
{
    /// <summary>
    /// Writes <paramref name="model"/> to <paramref name="stream"/> as a .kobj binary.
    /// Only call this on a successful compilation — <see cref="EmitModel.Success"/> must
    /// be <c>true</c>. Writing a failed model produces a file that cannot express the
    /// error state (diagnostics are not serialized) and would be silently treated as
    /// successful by <see cref="KobjReader"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="model"/> has <see cref="EmitModel.Success"/> == false.
    /// </exception>
    public static void Write(Stream stream, EmitModel model)
    {
        if (!model.Success)
            throw new InvalidOperationException(
                "Cannot write a .kobj file for a failed compilation. " +
                "Check EmitModel.Success and EmitModel.Diagnostics before calling Write.");

        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Header
        bw.Write(KobjFormat.Magic);
        bw.Write(KobjFormat.Version);

        // Sections
        bw.Write(KobjFormat.TagSections);
        bw.Write((ushort)model.Sections.Count);
        foreach (var section in model.Sections)
        {
            bw.Write(section.Name);
            bw.Write((byte)section.Type);
            bw.Write(section.FixedAddress.HasValue);
            if (section.FixedAddress.HasValue)
                bw.Write(section.FixedAddress.Value);
            bw.Write(section.Bank.HasValue);
            if (section.Bank.HasValue)
                bw.Write(section.Bank.Value);
            bw.Write(section.Data.Length);
            bw.Write(section.Data);

            // Patches for this section
            bw.Write((ushort)section.Patches.Count);
            foreach (var patch in section.Patches)
            {
                bw.Write(patch.Offset);
                bw.Write((byte)patch.Kind);
                bw.Write(patch.PCAfterInstruction);
                // Kobj patches store the diagnostic span for error reporting.
                // Expression ASTs are not serialized — all patches are resolved by
                // PatchResolver before writing. For cross-file linking, use --format rgbds.
                bw.Write(patch.DiagnosticSpan.Start);
                bw.Write(patch.DiagnosticSpan.Length);
            }
        }

        // Symbols
        bw.Write(KobjFormat.TagSymbols);
        bw.Write((ushort)model.Symbols.Count);
        foreach (var sym in model.Symbols)
        {
            bw.Write(sym.Name);
            bw.Write((byte)sym.Kind);
            bw.Write((byte)sym.Visibility);
            bw.Write(sym.Section ?? "");
            bw.Write(sym.Value);
        }

        // End marker
        bw.Write(KobjFormat.TagEnd);
    }
}
