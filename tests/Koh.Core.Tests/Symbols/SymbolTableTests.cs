using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Core.Tests.Symbols;

public class SymbolTableTests
{
    private static SymbolTable CreateTable() => new(new DiagnosticBag());

    [Test]
    public async Task DefineLabel_LookupByName()
    {
        var table = CreateTable();
        table.DefineLabel("main", 0x0150, "ROM0");

        var sym = table.Lookup("main");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("main");
        await Assert.That(sym.Kind).IsEqualTo(SymbolKind.Label);
        await Assert.That(sym.State).IsEqualTo(SymbolState.Defined);
        await Assert.That(sym.Value).IsEqualTo(0x0150);
        await Assert.That(sym.Section).IsEqualTo("ROM0");
    }

    [Test]
    public async Task DefineLabel_CaseInsensitive()
    {
        var table = CreateTable();
        table.DefineLabel("Main", 0x100, "ROM0");

        var sym = table.Lookup("MAIN");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("Main");
    }

    [Test]
    public async Task LocalLabel_ScopedToGlobal()
    {
        var table = CreateTable();
        table.DefineLabel("main", 0x100, "ROM0");
        table.DefineLabel(".loop", 0x104, "ROM0");

        // Lookup qualified: main.loop
        var sym = table.Lookup(".loop");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("main.loop");
        await Assert.That(sym.Value).IsEqualTo(0x104);
    }

    [Test]
    public async Task LocalLabel_DifferentGlobals()
    {
        var table = CreateTable();
        table.DefineLabel("funcA", 0x100, "ROM0");
        table.DefineLabel(".loop", 0x104, "ROM0");
        table.DefineLabel("funcB", 0x200, "ROM0");
        table.DefineLabel(".loop", 0x204, "ROM0");

        // After funcB is the anchor, .loop resolves to funcB.loop
        var sym = table.Lookup(".loop");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("funcB.loop");
        await Assert.That(sym.Value).IsEqualTo(0x204);
    }

    [Test]
    public async Task DefineConstant()
    {
        var table = CreateTable();
        table.DefineConstant("MY_CONST", 0x10);

        var sym = table.Lookup("MY_CONST");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.Value).IsEqualTo(0x10);
    }

    [Test]
    public async Task DuplicateLabel_ReportsDiagnostic()
    {
        var diag = new DiagnosticBag();
        var table = new SymbolTable(diag);
        table.DefineLabel("main", 0x100, "ROM0");
        table.DefineLabel("main", 0x200, "ROM0");

        await Assert.That(diag.ToList()).IsNotEmpty();
    }

    [Test]
    public async Task DuplicateConstant_ReportsDiagnostic()
    {
        var diag = new DiagnosticBag();
        var table = new SymbolTable(diag);
        table.DefineConstant("FOO", 1);
        table.DefineConstant("FOO", 2);

        await Assert.That(diag.ToList()).IsNotEmpty();
    }

    [Test]
    public async Task ForwardRef_UndefinedThenDefined()
    {
        var table = CreateTable();

        // Forward reference creates undefined placeholder
        var fwd = table.DeclareForwardRef("target");
        await Assert.That(fwd.State).IsEqualTo(SymbolState.Undefined);

        // Later definition resolves it
        table.DefineLabel("target", 0x300, "ROM0");
        var sym = table.Lookup("target");
        await Assert.That(sym!.State).IsEqualTo(SymbolState.Defined);
        await Assert.That(sym.Value).IsEqualTo(0x300);
    }

    [Test]
    public async Task ForwardRef_TracksReferenceSites()
    {
        var table = CreateTable();
        var fwd = table.DeclareForwardRef("target");
        // No reference site provided in this test, but the API supports it
        await Assert.That(fwd.ReferenceSites).IsEmpty();
    }

    [Test]
    public async Task GetUndefinedSymbols()
    {
        var table = CreateTable();
        table.DeclareForwardRef("missing");
        table.DefineLabel("found", 0x100, "ROM0");

        var undefined = table.GetUndefinedSymbols().ToList();
        await Assert.That(undefined).Count().IsEqualTo(1);
        await Assert.That(undefined[0].Name).IsEqualTo("missing");
    }

    [Test]
    public async Task Lookup_NonExistent_ReturnsNull()
    {
        var table = CreateTable();
        var sym = table.Lookup("doesNotExist");
        await Assert.That(sym).IsNull();
    }

    [Test]
    public async Task LocalLabel_NoAnchor_UsesBareName()
    {
        var table = CreateTable();
        // No global label defined yet — local label uses bare name
        table.DefineLabel(".orphan", 0x50, "ROM0");
        var sym = table.Lookup(".orphan");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo(".orphan");
    }

    [Test]
    public async Task DefineConstant_QualifiesLocalName()
    {
        // .LOCAL_CONST defined under global anchor must be stored and looked up as
        // "func.LOCAL_CONST", not ".LOCAL_CONST".
        var table = CreateTable();
        table.DefineLabel("func", 0x100, "ROM0");
        table.DefineConstant(".LOCAL_CONST", 42);

        // Lookup via unqualified local name resolves through current anchor
        var sym = table.Lookup(".LOCAL_CONST");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("func.LOCAL_CONST");
        await Assert.That(sym.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.Value).IsEqualTo(42);
    }

    [Test]
    public async Task DefineConstant_DuplicateQualified_ReportsDiagnostic()
    {
        // Duplicate detection must use the qualified key, not the raw name.
        var diag = new DiagnosticBag();
        var table = new SymbolTable(diag);
        table.DefineLabel("func", 0x100, "ROM0");
        table.DefineConstant(".K", 1);
        table.DefineConstant(".K", 2); // duplicate under same anchor

        await Assert.That(diag.ToList()).IsNotEmpty();
    }
}
