using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Core.Binding;

/// <summary>
/// Re-evaluates deferred expressions after Pass 2 and applies byte patches
/// to section buffers. Remaining unresolved patches are kept for the linker.
/// </summary>
internal sealed class PatchResolver
{
    private readonly SymbolTable _symbols;
    private readonly SectionManager _sections;
    private readonly DiagnosticBag _diagnostics;

    public PatchResolver(SymbolTable symbols, SectionManager sections, DiagnosticBag diagnostics)
    {
        _symbols = symbols;
        _sections = sections;
        _diagnostics = diagnostics;
    }

    public void ApplyAll()
    {
        foreach (var (_, section) in _sections.AllSections)
        {
            var resolved = new List<int>();

            for (int i = 0; i < section.Patches.Count; i++)
            {
                var patch = section.Patches[i];
                // Use the patch's own byte offset as the $ value so that expressions
                // like "dw $ + 4" evaluate correctly even when deferred to this phase.
                // Expression is null for patches deserialized from .kobj (linker-time patches).
                // They cannot be resolved here — leave them for the linker to handle.
                if (patch.Expression is null) continue;

                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics,
                    () => section.BaseAddress + patch.Offset);
                var value = evaluator.TryEvaluate(patch.Expression);
                if (value == null) continue; // keep for linker

                resolved.Add(i);

                switch (patch.Kind)
                {
                    case PatchKind.Absolute8:
                        section.ApplyPatch(patch.Offset, (byte)(value.Value & 0xFF));
                        break;
                    case PatchKind.Absolute16:
                        section.ApplyPatchWord(patch.Offset, (ushort)(value.Value & 0xFFFF));
                        break;
                    case PatchKind.Relative8:
                        long rel = value.Value - patch.PCAfterInstruction;
                        if (rel < -128 || rel > 127)
                        {
                            _diagnostics.Report(patch.DiagnosticSpan,
                                $"JR target out of range: offset {rel} does not fit in signed byte");
                            break; // leave placeholder byte; error is reported
                        }
                        section.ApplyPatch(patch.Offset, (byte)(sbyte)rel);
                        break;
                }
            }

            // Remove resolved patches (iterate in reverse to preserve indices)
            section.RemoveResolvedPatches(resolved);
        }
    }
}
