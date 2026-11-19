using Koh.Common;
using Koh.Native;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Debugger.Tests;

public class DwarfFixtureTests
{
    [Test]
    public async Task ReaderPreservesSymbolDefinitionAndSourceRange()
    {
        var source = new string('\n', 40) + "SECTION \"code\", ROM0[$150]\nmain:: ld hl, $1234";
        var model = Compilation
            .Create(SyntaxTree.Parse(SourceText.From(source, "src/main.asm")))
            .Emit();
        var result = new NativeLinker().Link([new LinkerInput("src/main.asm", model)]);
        await Assert.That(result.Success).IsTrue();
        var data = new NativeLinker().ReadDebugInfo(result.DebugData);
        var symbol = data.Symbols.Single(s => s.Name == "main");
        await Assert.That(symbol.Name).IsEqualTo("main");
        await Assert.That(data.AddressMap.Single(e => e.Address == 0x150).ByteCount).IsEqualTo(3u);
        await Assert.That(symbol.DefinitionFile).IsEqualTo("src/main.asm");
        await Assert.That(symbol.DefinitionLine).IsEqualTo(42u);
    }
}
