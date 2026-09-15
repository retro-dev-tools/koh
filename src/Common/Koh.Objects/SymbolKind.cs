namespace Koh.Objects;

public enum SymbolKind
{
    Label,
    Constant,
    StringConstant,
    Macro,
    CharMap,
}

public enum SymbolVisibility
{
    Local,
    Exported,
    Imported,
}
