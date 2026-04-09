using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Core.Binding;

public sealed class BindingResult
{
    public IReadOnlyDictionary<string, SectionBuffer>? Sections { get; }
    public SymbolTable? Symbols { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    /// <summary>
    /// Maps each called macro symbol to its maximum observed call-site argument count.
    /// Macros that were defined but never called have no entry.
    /// </summary>
    public IReadOnlyDictionary<Symbol, int>? MacroArities { get; }
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
        IReadOnlyList<Diagnostic> diagnostics,
        IReadOnlyDictionary<Symbol, int>? macroArities = null)
    {
        Sections = sections;
        Symbols = symbols;
        Diagnostics = diagnostics;
        MacroArities = macroArities;
    }
}
