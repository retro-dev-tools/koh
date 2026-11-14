using Koh.Objects;
using Koh.TestSupport;

namespace Koh.Debugger.Tests;

public class KdbgFixtureTests
{
    [Test]
    public async Task ReaderPreservesExplicitSymbolSizeAndSource()
    {
        var builder = new DebugInfoBuilder();
        builder.AddSymbol(KdbgSymbolKind.Label, 0, 0x150, 3, "main", 0, "src/main.asm", 42);
        using var stream = new MemoryStream();
        KdbgFileWriter.Write(stream, builder);
        var symbol = KdbgReader.Parse(stream.ToArray()).Symbols.Single();
        await Assert.That(symbol.Name).IsEqualTo("main");
        await Assert.That(symbol.Size).IsEqualTo((ushort)3);
        await Assert.That(symbol.DefinitionFile).IsEqualTo("src/main.asm");
        await Assert.That(symbol.DefinitionLine).IsEqualTo(42u);
    }
}
