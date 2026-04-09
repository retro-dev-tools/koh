using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Core.Binding;

public sealed class BindingResult
{
    public IReadOnlyDictionary<string, SectionBuffer>? Sections { get; }
    public SymbolTable? Symbols { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    /// <summary>
    /// Max observed call-site arg count per macro symbol. Null entry means the macro
    /// was never called. Only populated for macros that had at least one call site.
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
