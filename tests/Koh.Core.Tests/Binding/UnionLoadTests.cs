using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class UnionLoadTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    // =========================================================================
    // UNION / NEXTU / ENDU
    // =========================================================================

    [Test]
    public async Task Union_VariablesShareAddress()
    {
        var model = Emit("""
            SECTION UNION "Shared", WRAM0
            first_var: ds 1
            NEXTU
            second_var: ds 1
            ENDU
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // Both variables should have the same address (0)
        var first = model.Symbols.First(s => s.Name == "first_var");
        var second = model.Symbols.First(s => s.Name == "second_var");
        await Assert.That(first.Value).IsEqualTo(second.Value);
        await Assert.That(first.Value).IsEqualTo(0);
    }

    [Test]
    public async Task Union_SizeIsMaxOfMembers()
    {
        var model = Emit("""
            SECTION UNION "Shared", WRAM0
            small: ds 2
            NEXTU
            large: ds 4
            ENDU
            after_union: ds 1
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // after_union should be at offset 4 (max of 2 and 4)
        var after = model.Symbols.First(s => s.Name == "after_union");
        await Assert.That(after.Value).IsEqualTo(4);
    }

    [Test]
    public async Task Union_ThreeMembers()
    {
        var model = Emit("""
            SECTION UNION "Shared", WRAM0
            mem1: ds 1
            NEXTU
            mem2: ds 3
            NEXTU
            mem3: ds 2
            ENDU
            after: ds 1
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // All members start at 0
        await Assert.That(model.Symbols.First(s => s.Name == "mem1").Value).IsEqualTo(0);
        await Assert.That(model.Symbols.First(s => s.Name == "mem2").Value).IsEqualTo(0);
        await Assert.That(model.Symbols.First(s => s.Name == "mem3").Value).IsEqualTo(0);
        // after = max(1, 3, 2) = 3
        await Assert.That(model.Symbols.First(s => s.Name == "after").Value).IsEqualTo(3);
    }

    [Test]
    public async Task Union_WithLabelsInsideMembers()
    {
        var model = Emit("""
            SECTION UNION "Shared", WRAM0
            x_pos: ds 1
            y_pos: ds 1
            NEXTU
            coords: ds 2
            ENDU
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.First(s => s.Name == "x_pos").Value).IsEqualTo(0);
        await Assert.That(model.Symbols.First(s => s.Name == "y_pos").Value).IsEqualTo(1);
        await Assert.That(model.Symbols.First(s => s.Name == "coords").Value).IsEqualTo(0);
    }

    // =========================================================================
    // LOAD / ENDL
    // =========================================================================

    [Test]
    public async Task Load_LabelGetsRamAddress()
    {
        var model = Emit("""
            SECTION "ROM", ROM0
            nop
            LOAD "RAM", WRAM0
            my_var: ds 1
            ENDL
            nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // my_var should have a WRAM0 address (0), not a ROM address
        var sym = model.Symbols.FirstOrDefault(s => s.Name == "my_var");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Value).IsEqualTo(0); // WRAM0 base
    }

    [Test]
    public async Task Load_DataGoesToRomSection()
    {
        var model = Emit("""
            SECTION "ROM", ROM0
            db $AA
            LOAD "RAM", WRAM0
            db $BB
            ENDL
            db $CC
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // All data bytes should be in the ROM section
        var rom = model.Sections.First(s => s.Name == "ROM");
        await Assert.That(rom.Data.Length).IsEqualTo(3);
        await Assert.That(rom.Data[0]).IsEqualTo((byte)0xAA);
        await Assert.That(rom.Data[1]).IsEqualTo((byte)0xBB);
        await Assert.That(rom.Data[2]).IsEqualTo((byte)0xCC);
    }

    [Test]
    public async Task Load_SequentialLabelsGetCorrectAddresses()
    {
        var model = Emit("""
            SECTION "ROM", ROM0
            LOAD "RAM", WRAM0
            var_a: ds 1
            var_b: ds 2
            var_c: ds 1
            ENDL
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.First(s => s.Name == "var_a").Value).IsEqualTo(0);
        await Assert.That(model.Symbols.First(s => s.Name == "var_b").Value).IsEqualTo(1);
        await Assert.That(model.Symbols.First(s => s.Name == "var_c").Value).IsEqualTo(3);
    }

    [Test]
    public async Task Union_EmptyFirstMember()
    {
        var model = Emit("""
            SECTION UNION "Shared", WRAM0
            NEXTU
            real_var: ds 4
            ENDU
            after: ds 1
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.First(s => s.Name == "after").Value).IsEqualTo(4);
    }

    [Test]
    public async Task Union_SingleMember_NoNextu()
    {
        var model = Emit("""
            SECTION UNION "Shared", WRAM0
            only: ds 3
            ENDU
            after: ds 1
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.First(s => s.Name == "only").Value).IsEqualTo(0);
        await Assert.That(model.Symbols.First(s => s.Name == "after").Value).IsEqualTo(3);
    }

    // =========================================================================
    // Error paths
    // =========================================================================

    [Test]
    public async Task Nextu_WithoutUnion_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            NEXTU
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("NEXTU without matching"))).IsTrue();
    }

    [Test]
    public async Task Endu_WithoutUnion_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ENDU
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ENDU without matching"))).IsTrue();
    }

    [Test]
    public async Task Endl_WithoutLoad_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ENDL
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ENDL without matching"))).IsTrue();
    }

    [Test]
    public async Task Load_RomPcUnaffectedByDsInLoad()
    {
        var model = Emit("""
            SECTION "ROM", ROM0
            nop
            LOAD "RAM", WRAM0
            ds 10
            ENDL
            after_load: nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // ROM PC should advance by: 1 (nop) + 10 (ds inside LOAD emits to ROM) + 1 (nop after)
        var rom = model.Sections.First(s => s.Name == "ROM");
        await Assert.That(rom.Data.Length).IsEqualTo(12);
    }

    [Test]
    public async Task Load_CreatesRamSection()
    {
        var model = Emit("""
            SECTION "ROM", ROM0
            LOAD "MyRAM", WRAM0
            ram_var: ds 2
            ENDL
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // A WRAM0 section "MyRAM" should exist
        var ram = model.Sections.FirstOrDefault(s => s.Name == "MyRAM");
        await Assert.That(ram).IsNotNull();
    }

    // =========================================================================
    // RGBDS rejection tests
    // =========================================================================

    // RGBDS: load-rom
    [Test]
    public async Task LoadRom_LoadBlockForRomSection_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "Hello", ROM0
            ld a, 1
            LOAD "Wello", ROM0
            ld a, 2
            ENDL
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: load-overflow
    [Test]
    public async Task LoadOverflow_SectionExceedsMaxSize_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "Overflow", ROM0
            ds $6000
            LOAD "oops", WRAM0
            ds $2000
            db
            db
            ENDL
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: section-in-load
    [Test]
    public async Task SectionInLoad_SectionInsideLoadBlock_RejectsAssembly()
    {
        // A SECTION directive inside a LOAD block implicitly terminates the block;
        // the subsequent ENDL is then dangling.
        var model = Emit("""
            SECTION "outer1", ROM0
            LOAD "ram", WRAM0
            SECTION "outer2", ROM0
            ENDL
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: union-mismatch
    [Test]
    public async Task UnionMismatch_FixedVsAligned_RejectsAssembly()
    {
        var model = Emit("""
            SECTION UNION "fixed", WRAM0[$c001]
            SECTION UNION "fixed", WRAM0, ALIGN[1]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // =========================================================================
    // RGBDS: single-union.asm — simplest UNION with one exported label
    // =========================================================================

    [Test]
    public async Task SingleUnion_ExportedLabel_AtBaseAddress()
    {
        // RGBDS: single-union.asm — UNION with single exported label :: ds 5
        var model = Emit("""
            SECTION "test", WRAM0
            UNION
            label: ds 5
            ENDU
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
        var label = model.Symbols.FirstOrDefault(s => s.Name == "label");
        await Assert.That(label).IsNotNull();
        await Assert.That(label!.Value).IsEqualTo(0L);
    }

    // =========================================================================
    // RGBDS: union-in-union.asm — nested UNION inside outer UNION member
    // =========================================================================

    [Test]
    public async Task UnionInUnion_NestedUnionSize_IsMaxOfAll()
    {
        // RGBDS: union-in-union.asm — nested union, sizeof("test") == 4
        // Inner union: max(db=1, dw=2) = 2; outer: max(inner=2, dl=4) = 4
        var model = Emit("""
            SECTION "test", WRAM0
            UNION
                UNION
                    db
                NEXTU
                    dw
                ENDU
            NEXTU
                dl
            ENDU
            after: ds 1
            """);
        await Assert.That(model.Success).IsTrue();
        var after = model.Symbols.FirstOrDefault(s => s.Name == "after");
        await Assert.That(after).IsNotNull();
        await Assert.That(after!.Value).IsEqualTo(4L);
    }

    // =========================================================================
    // RGBDS: union-pushs.asm — PUSHS/POPS inside UNION member
    // =========================================================================

    [Test]
    public async Task UnionPushs_PushsInsideUnionMember_SectionSizesCorrect()
    {
        // RGBDS: union-pushs.asm — PUSHS creates HRAM section inside UNION member
        var model = Emit("""
            SECTION "Test", ROM0[$0]
            dw 0
            dw 0

            SECTION "RAM", WRAM0
            ds 654
            UNION
            ds 14
            NEXTU
            ds 897

            PUSHS
            SECTION "HRAM", HRAM
            ds $7F
            POPS
            ds 75
            NEXTU
            ds 863
            ENDU
            ds 28
            """);
        await Assert.That(model.Success).IsTrue();
        var hram = model.Sections.FirstOrDefault(s => s.Name == "HRAM");
        await Assert.That(hram).IsNotNull();
    }

    // =========================================================================
    // RGBDS: load-endings.asm — LOAD terminated by LOAD/SECTION/POPS/ENDSECTION
    // =========================================================================

    [Test]
    public async Task LoadEndings_LoadTerminatedByNewLoad_WarnsUnterminated()
    {
        // RGBDS: load-endings.asm — second LOAD terminates first without ENDL
        var model = Emit("""
            SECTION "A", ROM0
            db 1
            LOAD "P", WRAM0
            db 2
            LOAD "Q", WRAM0
            db 3
            ENDL
            """);
        // RGBDS warns about unterminated LOAD, but assembles ok
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("LOAD"))).IsTrue();
    }

    [Test]
    public async Task LoadEndings_LoadTerminatedBySection_WarnsUnterminated()
    {
        // RGBDS: load-endings.asm — SECTION after LOAD terminates it without ENDL
        var model = Emit("""
            SECTION "A", ROM0
            db 1
            LOAD "P", WRAM0
            db 2
            SECTION "B", ROM0
            db 3
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("LOAD"))).IsTrue();
    }

    // =========================================================================
    // RGBDS: load-pushs.asm — PUSHS inside LOAD block
    // =========================================================================

    [Test]
    public async Task LoadPushs_PushsInsideLoad_HramSectionCreated()
    {
        // RGBDS: load-pushs.asm — PUSHS creates HRAM section while inside LOAD
        var model = Emit("""
            SECTION "ROM CODE", ROM0
            ds $80
            LOAD "RAM CODE", SRAM
            PUSHS "HRAM", HRAM
            ds 1
            POPS
            ENDL
            """);
        await Assert.That(model.Success).IsTrue();
        var hram = model.Sections.FirstOrDefault(s => s.Name == "HRAM");
        await Assert.That(hram).IsNotNull();
    }

    // =========================================================================
    // RGBDS: load-pushs-load.asm — LOAD + PUSHS + nested LOAD + labels
    // =========================================================================

    [Test]
    public async Task LoadPushsLoad_NestedLoadAndPushs_LabelsGetCorrectAddresses()
    {
        // RGBDS: load-pushs-load.asm — PUSHS inside LOAD then another LOAD inside pushed section
        var model = Emit("""
            SECTION "A", ROM0[$1324]
            Rom0Label1:
            LOAD "LA", SRAM[$BEAD]
            SramLabel1:
            PUSHS
            SECTION "B", ROMX[$4698]
            RomxLabel1:
            POPS
            SramLabel2:
            ENDL
            Rom0Label2:
            """);
        await Assert.That(model.Success).IsTrue();
        var sram1 = model.Symbols.FirstOrDefault(s => s.Name == "SramLabel1");
        var sram2 = model.Symbols.FirstOrDefault(s => s.Name == "SramLabel2");
        await Assert.That(sram1).IsNotNull();
        await Assert.That(sram2).IsNotNull();
        await Assert.That(sram1!.Value).IsEqualTo(0xBEADL);
    }

    // =========================================================================
    // RGBDS: endl-local-scope.asm (UnionLoad variant) — .end after ENDL
    // =========================================================================

    [Test]
    public async Task UnionLoad_EndlLocalScope_DotEndAfterEndlIsRomScope()
    {
        // RGBDS: endl-local-scope.asm
        // .end label placed after ENDL in ROM context references DMARoutineCode scope
        var model = Emit("""
            SECTION "DMA ROM", ROM0[$0000]
            DMARoutineCode:
            LOAD "DMA RAM", HRAM[$FF80]
            DMARoutine:
                nop
                ret
            ENDL
            .end
            """);
        await Assert.That(model.Success).IsTrue();
        var endSym = model.Symbols.FirstOrDefault(s => s.Name == "DMARoutineCode.end");
        await Assert.That(endSym).IsNotNull();
    }
}
