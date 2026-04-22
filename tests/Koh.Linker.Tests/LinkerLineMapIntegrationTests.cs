using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Koh.Linker.Core;

namespace Koh.Linker.Tests;

/// <summary>
/// Verifies the full assemble → link → .kdbg → parse pipeline: after
/// linking real assembly source, the produced .kdbg's address map lets
/// us resolve (file, line) back to the (bank, address) that the DAP
/// server would set a breakpoint at.
///
/// Regression guard against Phase-1 behaviour where .kdbg held only
/// symbol entries with line=0 and all source files were flattened to
/// "<kobj-path>.kobj", breaking VS Code's setBreakpoints for every
/// user who tried F5.
/// </summary>
public class LinkerLineMapIntegrationTests
{
    private static LinkerInput AssembleToInput(string path, string source)
    {
        var tree = SyntaxTree.Parse(SourceText.From(source, path));
        var model = Compilation.Create(tree).Emit();
        if (!model.Success)
            throw new InvalidOperationException(
                $"assemble failed: {string.Join("; ", model.Diagnostics.Select(d => d.Message))}");
        return new LinkerInput(path, model);
    }

    private static KdbgParsed LinkAndParseKdbg(string path, string source)
    {
        var input = AssembleToInput(path, source);
        var linker = new Koh.Linker.Core.Linker();
        var result = linker.Link([input]);
        if (!result.Success)
            throw new InvalidOperationException(
                $"link failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");

        var builder = new DebugInfoBuilder();
        DebugInfoPopulator.Populate(builder, result);

        using var ms = new MemoryStream();
        KdbgFileWriter.Write(ms, builder);
        return KdbgReader.Parse(ms.ToArray());
    }

    private static IReadOnlyList<(byte Bank, ushort Address)> LookupLine(
        KdbgParsed kdbg, string file, uint line)
    {
        // Mirror what Koh.Debugger.SourceMap does: case-insensitive file
        // match + exact line match, collect every (bank, address) that
        // starts a run on that line.
        var results = new List<(byte, ushort)>();
        foreach (var entry in kdbg.AddressMap)
        {
            if (entry.SourceFile is null) continue;
            if (!StringComparer.OrdinalIgnoreCase.Equals(entry.SourceFile, file)) continue;
            if (entry.Line != line) continue;
            results.Add((entry.Bank, entry.Address));
        }
        return results;
    }

    [Test]
    public async Task Nops_EachLineResolvesToItsOwnAddress()
    {
        // ROM0 section starts at 0x0000 (default), so the three nops
        // live at $0000, $0001, $0002.
        var src =
            "SECTION \"Main\", ROM0\n" +   // line 1
            "__main__:\n" +                 // line 2
            "    nop\n" +                   // line 3 → $0000
            "    nop\n" +                   // line 4 → $0001
            "    nop\n";                    // line 5 → $0002
        var kdbg = LinkAndParseKdbg("main.asm", src);

        await Assert.That(LookupLine(kdbg, "main.asm", 3)).IsEquivalentTo(new[] { ((byte)0, (ushort)0x0000) });
        await Assert.That(LookupLine(kdbg, "main.asm", 4)).IsEquivalentTo(new[] { ((byte)0, (ushort)0x0001) });
        await Assert.That(LookupLine(kdbg, "main.asm", 5)).IsEquivalentTo(new[] { ((byte)0, (ushort)0x0002) });
    }

    [Test]
    public async Task MultiByteInstruction_ResolvesToInstructionStart()
    {
        // LD HL, $BEEF is 3 bytes; the breakpoint-setting lookup should
        // return the opcode's address (not the middle of the operand).
        var src =
            "SECTION \"Main\", ROM0\n" +
            "    ld hl, $BEEF\n";          // line 2 → $0000
        var kdbg = LinkAndParseKdbg("main.asm", src);

        var addrs = LookupLine(kdbg, "main.asm", 2);
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
            "SECTION \"Main\", ROM0\n" +   // 1
            "__main__:\n" +                 // 2 — label emits no bytes
            "; comment line\n" +            // 3 — no code
            "    nop\n";                    // 4
        var kdbg = LinkAndParseKdbg("main.asm", src);

        await Assert.That(LookupLine(kdbg, "main.asm", 1).Count).IsEqualTo(0);
        await Assert.That(LookupLine(kdbg, "main.asm", 2).Count).IsEqualTo(0);
        await Assert.That(LookupLine(kdbg, "main.asm", 3).Count).IsEqualTo(0);
        await Assert.That(LookupLine(kdbg, "main.asm", 4).Count).IsEqualTo(1);
    }

    [Test]
    public async Task SectionWithFixedRomxAddress_MapsToPlacedAddress()
    {
        // ROMX section at bank 3, fixed address $4000 — the kdbg must
        // report bank=3 and the windowed address $4000 for byte 0.
        var src =
            "SECTION \"Bank3\", ROMX[$4000], BANK[3]\n" +
            "    nop\n" +                   // line 2 → bank 3, $4000
            "    nop\n";                    // line 3 → bank 3, $4001
        var kdbg = LinkAndParseKdbg("b.asm", src);

        await Assert.That(LookupLine(kdbg, "b.asm", 2)).IsEquivalentTo(new[] { ((byte)3, (ushort)0x4000) });
        await Assert.That(LookupLine(kdbg, "b.asm", 3)).IsEquivalentTo(new[] { ((byte)3, (ushort)0x4001) });
    }

    [Test]
    public async Task LargeDbBlock_SplitsIntoMultipleAddressMapEntries()
    {
        // AddressMapRecord.ByteCount is one byte; runs > 255 must be
        // split. A 600-byte DS fill on one line should produce at least
        // three entries all pointing back to that same line.
        var src =
            "SECTION \"Main\", ROM0\n" +
            "    ds 600, $AA\n";            // line 2, 600 bytes
        var kdbg = LinkAndParseKdbg("d.asm", src);

        var hits = kdbg.AddressMap
            .Where(e => e.Line == 2 && e.SourceFile == "d.asm")
            .ToList();
        await Assert.That(hits.Count).IsGreaterThanOrEqualTo(3);
        int totalBytes = hits.Sum(h => h.ByteCount);
        await Assert.That(totalBytes).IsEqualTo(600);
        await Assert.That(hits[0].Address).IsEqualTo((ushort)0x0000);
    }
}
