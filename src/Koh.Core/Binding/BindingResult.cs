using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Core.Binding;

public sealed class BindingResult
{
    public IReadOnlyDictionary<string, SectionBuffer>? Sections { get; }
    public SymbolTable? Symbols { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool Success
    {
        get
        {
            var diags = Diagnostics;
            for (int i = 0; i < diags.Count; i++)
                if (diags[i].Severity == DiagnosticSeverity.Error) return false;
            return true;
        }
    }

    public BindingResult(
        IReadOnlyDictionary<string, SectionBuffer>? sections,
        SymbolTable? symbols,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Sections = sections;
        Symbols = symbols;
        Diagnostics = diagnostics;
    }
}
