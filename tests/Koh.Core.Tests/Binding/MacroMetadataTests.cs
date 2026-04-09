using Koh.Core.Symbols;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class MacroMetadataTests
{
    private static SemanticModel GetModel(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var compilation = Compilation.Create(tree);
        return compilation.GetSemanticModel(tree);
    }

    [Test]
    public async Task TwoArgCall_ReturnsTwo()
    {
        var model = GetModel(
            "my_mac: MACRO\nld \\1, \\2\nENDM\nSECTION \"Main\", ROM0\nmy_mac a, b");
        var sym = model.ResolveSymbol("my_mac", 0);
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Macro);
        await Assert.That(model.GetMacroArity(sym)).IsEqualTo(2);
    }

    [Test]
    public async Task VaryingCallSites_ReturnsMax()
    {
        var model = GetModel(
            "my_mac: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_mac a\nmy_mac a, b, c");
        var sym = model.ResolveSymbol("my_mac", 0);
        await Assert.That(sym).IsNotNull();
        await Assert.That(model.GetMacroArity(sym!)).IsEqualTo(3);
    }

    [Test]
    public async Task ZeroArgCall_ReturnsZero()
    {
        var model = GetModel(
            "my_mac: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_mac");
        var sym = model.ResolveSymbol("my_mac", 0);
        await Assert.That(sym).IsNotNull();
        await Assert.That(model.GetMacroArity(sym!)).IsEqualTo(0);
    }

    [Test]
    public async Task DefinedButNeverCalled_ReturnsNull()
    {
        var model = GetModel(
            "my_mac: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nnop");
        var sym = model.ResolveSymbol("my_mac", 0);
        await Assert.That(sym).IsNotNull();
        await Assert.That(model.GetMacroArity(sym!)).IsNull();
    }

    [Test]
    public async Task NoMacrosDefined_ReturnsNull()
    {
        var model = GetModel("SECTION \"Main\", ROM0\nnop");
        await Assert.That(model.GetMacroArity("some_macro")).IsNull();
    }

    [Test]
    public async Task ParenthesizedArgs_CountedCorrectly()
    {
        // BANK(x) is a single arg due to paren grouping, so: BANK(x), y = 2 args
        var model = GetModel(
            "my_mac: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_mac BANK(x), y");
        var sym = model.ResolveSymbol("my_mac", 0);
        await Assert.That(sym).IsNotNull();
        await Assert.That(model.GetMacroArity(sym!)).IsEqualTo(2);
    }

    [Test]
    public async Task CaseInsensitiveLookup()
    {
        var model = GetModel(
            "MY_MAC: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_mac a, b");
        // Lookup by different case via convenience method
        var arity = model.GetMacroArity("my_mac");
        await Assert.That(arity).IsEqualTo(2);
    }

    [Test]
    public async Task NestedMacroCall_TracksOuterArity()
    {
        var model = GetModel(
            "inner: MACRO\nnop\nENDM\nouter: MACRO\ninner\nENDM\nSECTION \"Main\", ROM0\nouter a, b, c");
        var sym = model.ResolveSymbol("outer", 0);
        await Assert.That(sym).IsNotNull();
        await Assert.That(model.GetMacroArity(sym!)).IsEqualTo(3);
    }

    [Test]
    public async Task ConvenienceByName_DelegatesThroughSymbol()
    {
        var model = GetModel(
            "my_mac: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_mac a, b");
        var arity = model.GetMacroArity("my_mac");
        await Assert.That(arity).IsEqualTo(2);
    }
}
