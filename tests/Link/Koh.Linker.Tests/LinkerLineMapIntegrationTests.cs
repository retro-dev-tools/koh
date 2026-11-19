using Koh.Common;
using Koh.Native;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Linker.Tests;

public class LinkerLineMapIntegrationTests
{
    private static LinkerInput AssembleToInput(string path, string source)
    {
        var tree = SyntaxTree.Parse(SourceText.From(source, path));
        var model = Compilation.Create(tree).Emit();
        if (!model.Success)
            throw new InvalidOperationException(
                $"assemble failed: {string.Join("; ", model.Diagnostics.Select(d => d.Message))}"
            );
        return new LinkerInput(path, model);
    }

    private static DebugData LinkAndParseDwarf(string path, string source)
    {
        var input = AssembleToInput(path, source);
        var linker = new NativeLinker();
        var result = linker.Link([input]);
        if (!result.Success)
            throw new InvalidOperationException(
                $"link failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}"
            );

        return new NativeLinker().ReadDebugInfo(result.DebugData);
    }

    private static IReadOnlyList<(byte Bank, ushort Address)> LookupLine(
        DebugData debug,
        string file,
        uint line
    )
    {
        // Mirror what Koh.Debugger.SourceMap does: case-insensitive file
        // match + exact line match, collect every (bank, address) that
        // starts a run on that line.
        var results = new List<(byte, ushort)>();
        foreach (var entry in debug.AddressMap)
        {
            if (entry.SourceFile is null)
                continue;
            if (!StringComparer.OrdinalIgnoreCase.Equals(entry.SourceFile, file))
                continue;
            if (entry.Line != line)
                continue;
            results.Add((checked((byte)entry.Bank), entry.Address));
        }
        return results;
    }

    [Test]
    public async Task Nops_EachLineResolvesToItsOwnAddress()
    {
        // ROM0 section starts at 0x0000 (default), so the three nops
        // live at $0000, $0001, $0002.
        var src =
            "SECTION \"Main\", ROM0\n"
            + // line 1
            "__main__:\n"
            + // line 2
            "    nop\n"
            + // line 3 → $0000
            "    nop\n"
            + // line 4 → $0001
            "    nop\n"; // line 5 → $0002
        var debug = LinkAndParseDwarf("main.asm", src);

        await Assert
            .That(LookupLine(debug, "main.asm", 3))
            .IsEquivalentTo(new[] { ((byte)0, (ushort)0x0000) });
        await Assert
            .That(LookupLine(debug, "main.asm", 4))
            .IsEquivalentTo(new[] { ((byte)0, (ushort)0x0001) });
        await Assert
            .That(LookupLine(debug, "main.asm", 5))
            .IsEquivalentTo(new[] { ((byte)0, (ushort)0x0002) });
    }

    [Test]
    public async Task MultiByteInstruction_ResolvesToInstructionStart()
    {
        // LD HL, $BEEF is 3 bytes; the breakpoint-setting lookup should
        // return the opcode's address (not the middle of the operand).
        var src = "SECTION \"Main\", ROM0\n" + "    ld hl, $BEEF\n"; // line 2 → $0000
        var debug = LinkAndParseDwarf("main.asm", src);

        var addrs = LookupLine(debug, "main.asm", 2);
        await Assert.That(addrs.Count).IsEqualTo(1);
        await Assert.That(addrs[0]).IsEqualTo(((byte)0, (ushort)0x0000));
    }

    [Test]
    public async Task LinesWithoutCode_ReturnNoAddresses()
    {
        // "No code at this line" — the DAP server needs an empty lookup
        // to correctly report `verified: false` for breakpoints on
        // comments, blank lines, or labels (which emit no bytes).
        var src =
            "SECTION \"Main\", ROM0\n"
            + // 1
            "__main__:\n"
            + // 2 — label emits no bytes
            "; comment line\n"
            + // 3 — no code
            "    nop\n"; // 4
        var debug = LinkAndParseDwarf("main.asm", src);

        await Assert.That(LookupLine(debug, "main.asm", 1).Count).IsEqualTo(0);
        await Assert.That(LookupLine(debug, "main.asm", 2).Count).IsEqualTo(0);
        await Assert.That(LookupLine(debug, "main.asm", 3).Count).IsEqualTo(0);
        await Assert.That(LookupLine(debug, "main.asm", 4).Count).IsEqualTo(1);
    }

    [Test]
    public async Task SectionWithFixedRomxAddress_MapsToPlacedAddress()
    {
        // ROMX section at bank 3, fixed address $4000 — the debug must
        // report bank=3 and the windowed address $4000 for byte 0.
        var src =
            "SECTION \"Bank3\", ROMX[$4000], BANK[3]\n"
            + "    nop\n"
            + // line 2 → bank 3, $4000
            "    nop\n"; // line 3 → bank 3, $4001
        var debug = LinkAndParseDwarf("b.asm", src);

        await Assert
            .That(LookupLine(debug, "b.asm", 2))
            .IsEquivalentTo(new[] { ((byte)3, (ushort)0x4000) });
        await Assert
            .That(LookupLine(debug, "b.asm", 3))
            .IsEquivalentTo(new[] { ((byte)3, (ushort)0x4001) });
    }

    [Test]
    public async Task LargeDbBlockPreservesItsFullSourceRange()
    {
        var src = "SECTION \"Main\", ROM0\n" + "    ds 600, $AA\n"; // line 2, 600 bytes
        var debug = LinkAndParseDwarf("d.asm", src);

        var hits = debug.AddressMap.Where(e => e.Line == 2 && e.SourceFile == "d.asm").ToList();
        await Assert.That(hits.Sum(hit => (long)hit.ByteCount)).IsEqualTo(600L);
        var covered = hits.SelectMany(hit =>
                Enumerable.Range(hit.Address, checked((int)hit.ByteCount))
            )
            .ToArray();
        await Assert.That(covered).IsEquivalentTo(Enumerable.Range(0, 600).ToArray());
        await Assert.That(hits[0].Address).IsEqualTo((ushort)0x0000);
    }
}
