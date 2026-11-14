using System.Buffers.Binary;
using Koh.Common;
using Koh.Native;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Objects.Tests;

public class ElfIntegrationTests
{
    [Test]
    public async Task DebugOptOutAllowsConstantsOutsideKdbgRange()
    {
        var model = Assemble("DEF Value EQU -1\nSECTION \"Code\", ROM0\ndb LOW(Value)\n");
        var result = new NativeLinker().Link(
            [new LinkerInput("constant.o", model)],
            new LinkOptions(EmitDebugInfo: false)
        );
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData![0]).IsEqualTo((byte)255);
        await Assert.That(result.DebugData.Length).IsEqualTo(0);
        var debug = new NativeLinker().Link([new LinkerInput("constant.o", model)]);
        await Assert.That(debug.Success).IsFalse();
        await Assert
            .That(debug.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("KDBG")))
            .IsTrue();
    }

    private static EmitModel Assemble(
        string source,
        string path = "object.asm",
        bool imports = false
    )
    {
        var model = Compilation
            .Create(
                new AssemblyOptions { AllowUndefinedSymbols = imports },
                SyntaxTree.Parse(SourceText.From(source, path))
            )
            .Emit();
        if (!model.Success)
            throw new InvalidOperationException(
                string.Join("; ", model.Diagnostics.Select(d => d.Message))
            );
        return model;
    }

    private static LinkResult Link(params EmitModel[] models) =>
        new NativeLinker().Link(
            models.Select((model, index) => new LinkerInput($"input{index}.o", model)).ToArray()
        );

    [Test]
    public async Task Elf32RelocatableHeaderAndSectionData()
    {
        var model = Assemble(
            "SECTION \"Code\", ROM0[$200]\nstart:: db $A5,$5A\nSECTION \"Vars\", WRAM0[$C000]\nstate: ds 4"
        );
        var bytes = model.ElfImage!;
        await Assert
            .That(bytes[..7])
            .IsEquivalentTo(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1 });
        await Assert
            .That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16)))
            .IsEqualTo((ushort)1);
        var sectionOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));
        var sectionSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(46));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(48));
        await Assert.That(sectionSize).IsEqualTo((ushort)40);
        await Assert.That(sectionOffset + count * sectionSize).IsLessThanOrEqualTo(bytes.Length);
        var codeType = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(sectionOffset + sectionSize + 4)
        );
        var dataOffset = checked(
            (int)
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(sectionOffset + sectionSize + 16)
                )
        );
        await Assert.That(codeType).IsEqualTo(1u);
        await Assert
            .That(bytes[dataOffset..(dataOffset + 2)])
            .IsEquivalentTo(new byte[] { 0xA5, 0x5A });
        await Assert
            .That(
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(sectionOffset + 2 * sectionSize + 4)
                )
            )
            .IsEqualTo(8u);
        await Assert
            .That(
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(sectionOffset + 2 * sectionSize + 20)
                )
            )
            .IsEqualTo(4u);
        var result = Link(model);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData![0x200..0x202]).IsEquivalentTo(new byte[] { 0xA5, 0x5A });
        await Assert
            .That(result.Symbols.Single(s => s.Name == "state").AbsoluteAddress)
            .IsEqualTo(0xC000L);
    }

    [Test]
    public async Task EmptyMultipleAndLargeSectionsRetainDataAndSymbols()
    {
        var data = string.Join(",", Enumerable.Range(0, 256));
        var result = Link(
            Assemble(
                $"DEF Value EQU $42\nSECTION \"Empty\",ROM0\nSECTION \"Data\",ROM0[$200]\nstart:: db {data}\nSECTION \"Other\",ROM0[$400]\ndb Value"
            )
        );
        await Assert.That(result.Success).IsTrue();
        await Assert
            .That(result.RomData![0x200..0x300])
            .IsEquivalentTo(Enumerable.Range(0, 256).Select(v => (byte)v).ToArray());
        await Assert.That(result.RomData[0x400]).IsEqualTo((byte)0x42);
        await Assert
            .That(result.Symbols.Single(s => s.Name == "start").AbsoluteAddress)
            .IsEqualTo(0x200L);
        await Assert
            .That(result.Symbols.Single(s => s.Name == "Value").AbsoluteAddress)
            .IsEqualTo(0x42L);
    }

    [Test]
    public async Task ImportsAndLocalForwardReferencesResolveAcrossSeparateElfFiles()
    {
        var consumer = Assemble(
            "SECTION \"Code\",ROM0[$200]\ndw external, local\ncall external\nlocal: nop",
            "consumer.asm",
            true
        );
        var provider = Assemble(
            "SECTION \"Provider\",ROMX[$4567],BANK[2]\nexternal:: ret",
            "provider.asm"
        );
        await Assert
            .That(consumer.Symbols.Single(s => s.Name == "external").Visibility)
            .IsEqualTo(SymbolVisibility.Imported);
        await Assert
            .That(consumer.Symbols.Single(s => s.Name == "local").Visibility)
            .IsEqualTo(SymbolVisibility.Local);
        var result = Link(consumer, provider);
        await Assert.That(result.Success).IsTrue();
        await Assert
            .That(result.RomData![0x200..0x208])
            .IsEquivalentTo(new byte[] { 0x67, 0x45, 0x07, 0x02, 0xCD, 0x67, 0x45, 0 });
        await Assert.That(result.RomData[0x8567]).IsEqualTo((byte)0xC9);
    }

    [Test]
    public async Task EveryDeferredOperatorEvaluatesAfterImportResolution()
    {
        string[] expressions =
        [
            "ext",
            "ext+1",
            "ext-2*3",
            "(ext+4)/2",
            "ext%3",
            "ext&$FF",
            "ext|1",
            "ext^2",
            "ext<<2",
            "ext>>1",
            "ext==1",
            "ext!=1",
            "ext<1",
            "ext>1",
            "ext<=1",
            "ext>=1",
            "ext&&1",
            "ext||1",
            "-ext",
            "~ext",
            "!ext",
            "+ext",
            "((ext))",
            "2**ext",
            "ext>>>1",
        ];
        int[] expected =
        [
            5,
            6,
            -1,
            4,
            2,
            5,
            5,
            7,
            20,
            2,
            0,
            1,
            0,
            1,
            0,
            1,
            1,
            1,
            -5,
            -6,
            0,
            5,
            5,
            32,
            2,
        ];
        var source =
            "SECTION \"Code\",ROM0[$200]\n" + string.Join("\n", expressions.Select(e => "dw " + e));
        var result = Link(
            Assemble(source, imports: true),
            Assemble("DEF ext EQU 5\nEXPORT ext", "provider.asm")
        );
        await Assert.That(result.Success).IsTrue();
        for (int i = 0; i < expected.Length; i++)
            await Assert
                .That(
                    BinaryPrimitives.ReadUInt16LittleEndian(result.RomData!.AsSpan(0x200 + i * 2))
                )
                .IsEqualTo(unchecked((ushort)expected[i]));
    }

    [Test]
    public async Task CurrentAddressHighLowBankAndInstructionOperandsKeepTheirContext()
    {
        var consumer = Assemble(
            "SECTION \"Code\",ROM0[$200]\ndb HIGH(ext),LOW(ext),BANK(ext),HIGH(ext+1)\ndw ext+@\njp ext\ncall ext+3\nld a,LOW(ext)\nld hl,ext\n",
            imports: true
        );
        var provider = Assemble(
            "SECTION \"Provider\",ROMX[$4567],BANK[2]\next:: ret",
            "provider.asm"
        );
        var result = Link(consumer, provider);
        await Assert.That(result.Success).IsTrue();
        await Assert
            .That(result.RomData![0x200..0x211])
            .IsEquivalentTo(
                new byte[]
                {
                    0x45,
                    0x67,
                    2,
                    0x45,
                    0x6B,
                    0x47,
                    0xC3,
                    0x67,
                    0x45,
                    0xCD,
                    0x6A,
                    0x45,
                    0x3E,
                    0x67,
                    0x21,
                    0x67,
                    0x45,
                }
            );
    }

    [Test]
    public async Task SourceLineRangesSurviveSerializedElf()
    {
        var result = Link(
            Assemble("SECTION \"Code\",ROMX[$4000],BANK[3]\nld hl,$1234\nnop", "real.asm")
        );
        await Assert.That(result.Success).IsTrue();
        var parsed = KdbgReader.Parse(result.DebugData);
        var instruction = parsed.AddressMap.Single(e => e.SourceFile == "real.asm" && e.Line == 2);
        await Assert.That(instruction.Bank).IsEqualTo((byte)3);
        await Assert.That(instruction.Address).IsEqualTo((ushort)0x4000);
        await Assert.That(instruction.ByteCount).IsEqualTo((byte)3);
    }

    [Test]
    public async Task InvalidElfAndMissingImportsFailWithoutRom()
    {
        var valid = Assemble("SECTION \"Code\",ROM0\nnop").ElfImage!;
        foreach (var bytes in new[] { new byte[4], valid[..20] })
        {
            var result = Link(new EmitModel([], [], success: true) { ElfImage = bytes });
            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.RomData).IsNull();
            await Assert.That(result.Diagnostics.Count).IsGreaterThan(0);
        }
        var unresolved = Link(Assemble("SECTION \"Code\",ROM0\njp Missing", imports: true));
        await Assert.That(unresolved.Success).IsFalse();
        await Assert.That(unresolved.RomData).IsNull();
        await Assert.That(unresolved.Diagnostics.Any(d => d.Message.Contains("Missing"))).IsTrue();
    }

    [Test]
    public async Task FailedAssemblyCannotEmitElf()
    {
        var compilation = Compilation.Create(SyntaxTree.Parse("ASSERT 0"));
        await Assert.That(compilation.Emit().Success).IsFalse();
        await Assert.That(() => compilation.EmitBytes("elf")).Throws<InvalidDataException>();
    }
}
