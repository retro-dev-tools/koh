using Koh.Core.Symbols;

namespace Koh.Core.Tests.Symbols;

public class MacroKindTests
{
    [Test]
    public async Task MacroKind_IsDistinctFromOtherKinds()
    {
        // Verify Macro is a distinct enum value by constructing symbols of each kind
        var macro = new Symbol("m", SymbolKind.Macro);
        var label = new Symbol("l", SymbolKind.Label);
        var constant = new Symbol("c", SymbolKind.Constant);
        var strConst = new Symbol("s", SymbolKind.StringConstant);

        await Assert.That(macro.Kind).IsNotEqualTo(label.Kind);
        await Assert.That(macro.Kind).IsNotEqualTo(constant.Kind);
        await Assert.That(macro.Kind).IsNotEqualTo(strConst.Kind);
    }

    [Test]
    public async Task MacroKind_CanBeAssignedToSymbol()
    {
        var sym = new Symbol("test_macro", SymbolKind.Macro);
        await Assert.That(sym.Kind).IsEqualTo(SymbolKind.Macro);
        await Assert.That(sym.Name).IsEqualTo("test_macro");
    }
}
