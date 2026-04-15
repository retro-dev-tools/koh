using System.IO;
using Koh.Linker.Core;

namespace Koh.Linker.Tests;

public class KdbgFileWriterTests
{
    private static byte[] Write(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        KdbgFileWriter.Write(ms, b);
        return ms.ToArray();
    }

    [Test]
    public async Task Header_Magic_And_Version()
    {
        var builder = new DebugInfoBuilder();
        var bytes = Write(builder);

        await Assert.That(bytes.Length).IsGreaterThanOrEqualTo(32);
        uint magic = BitConverter.ToUInt32(bytes, 0);
        ushort version = BitConverter.ToUInt16(bytes, 4);
        await Assert.That(magic).IsEqualTo(KdbgFormat.Magic);
        await Assert.That(version).IsEqualTo(KdbgFormat.Version1);
    }

    [Test]
    public async Task Symbol_Round_Trip()
    {
        var builder = new DebugInfoBuilder();
        builder.AddSymbol(
            kind: KdbgSymbolKind.Label,
            bank: 0,
            address: 0x0150,
            size: 3,
            name: "main",
            scopeId: 0,
            definitionSourceFile: "src/main.asm",
            definitionLine: 42);

        var bytes = Write(builder);
        var parsed = KdbgReader.Parse(bytes);

        await Assert.That(parsed.Symbols.Count).IsEqualTo(1);
        var sym = parsed.Symbols[0];
        await Assert.That(sym.Name).IsEqualTo("main");
        await Assert.That(sym.Bank).IsEqualTo((byte)0);
        await Assert.That(sym.Address).IsEqualTo((ushort)0x0150);
        await Assert.That(sym.Size).IsEqualTo((ushort)3);
        await Assert.That(sym.DefinitionFile).IsEqualTo("src/main.asm");
        await Assert.That(sym.DefinitionLine).IsEqualTo(42u);
    }

    [Test]
    public async Task AddressMap_Round_Trip_Without_Expansion()
    {
        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(bank: 0, address: 0x0100, byteCount: 1,
            sourceFile: "src/main.asm", line: 10);
        builder.AddAddressMapping(bank: 0, address: 0x0101, byteCount: 1,
            sourceFile: "src/main.asm", line: 10);

        var bytes = Write(builder);
        var parsed = KdbgReader.Parse(bytes);

        await Assert.That(parsed.AddressMap.Count).IsEqualTo(2);
        await Assert.That(parsed.AddressMap[0].Address).IsEqualTo((ushort)0x0100);
        await Assert.That(parsed.AddressMap[1].Address).IsEqualTo((ushort)0x0101);
    }

    [Test]
    public async Task AddressMap_Round_Trip_With_Expansion_Stack()
    {
        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(
            bank: 0, address: 0x0100, byteCount: 1,
            sourceFile: "src/main.asm", line: 5,
            expansionStack: new List<(string, uint)>
            {
                ("src/main.asm", 100),
                ("src/main.asm", 42),
            });

        var bytes = Write(builder);
        var parsed = KdbgReader.Parse(bytes);

        await Assert.That(parsed.AddressMap.Count).IsEqualTo(1);
        await Assert.That(parsed.AddressMap[0].ExpansionStack.Count).IsEqualTo(2);
    }
}
