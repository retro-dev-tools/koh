using Koh.Core.Binding;

namespace Koh.Core.Tests.Binding;

public class SectionBufferLineMapTests
{
    [Test]
    public async Task NoLineMapEntries_WhenSourceLocationNeverSet()
    {
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.EmitByte(0x00);
        sec.EmitByte(0x01);
        await Assert.That(sec.LineMap.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SingleInstruction_ProducesOneCoalescedEntry()
    {
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("foo.asm", 7);
        sec.EmitByte(0x21);   // LD HL, n16 opcode
        sec.EmitWord(0xBEEF); // operand
        await Assert.That(sec.LineMap.Count).IsEqualTo(1);
        var e = sec.LineMap[0];
        await Assert.That(e.Offset).IsEqualTo(0);
        await Assert.That(e.ByteCount).IsEqualTo(3);
        await Assert.That(e.File).IsEqualTo("foo.asm");
        await Assert.That(e.Line).IsEqualTo(7u);
    }

    [Test]
    public async Task TwoInstructionsDifferentLines_ProducesTwoEntries()
    {
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("foo.asm", 7);
        sec.EmitByte(0x00);

        sec.SetSourceLocation("foo.asm", 8);
        sec.EmitByte(0x00);

        await Assert.That(sec.LineMap.Count).IsEqualTo(2);
        await Assert.That(sec.LineMap[0].Line).IsEqualTo(7u);
        await Assert.That(sec.LineMap[0].ByteCount).IsEqualTo(1);
        await Assert.That(sec.LineMap[1].Offset).IsEqualTo(1);
        await Assert.That(sec.LineMap[1].Line).IsEqualTo(8u);
        await Assert.That(sec.LineMap[1].ByteCount).IsEqualTo(1);
    }

    [Test]
    public async Task ConsecutiveEmitsOnSameLine_CoalesceIntoOneEntry()
    {
        // db $01, $02, $03 emits three bytes — all one source line, one slot.
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("foo.asm", 5);
        sec.EmitByte(0x01);
        sec.EmitByte(0x02);
        sec.EmitByte(0x03);
        await Assert.That(sec.LineMap.Count).IsEqualTo(1);
        await Assert.That(sec.LineMap[0].ByteCount).IsEqualTo(3);
    }

    [Test]
    public async Task DifferentFilesSameLine_DoNotCoalesce()
    {
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("a.asm", 10);
        sec.EmitByte(0x00);
        sec.SetSourceLocation("b.asm", 10);
        sec.EmitByte(0x00);
        await Assert.That(sec.LineMap.Count).IsEqualTo(2);
        await Assert.That(sec.LineMap[0].File).IsEqualTo("a.asm");
        await Assert.That(sec.LineMap[1].File).IsEqualTo("b.asm");
    }

    [Test]
    public async Task UnsetSourceLocation_SkipsLineMapping()
    {
        // Passing null as the file name switches tracking off — used for
        // synthetic DS fills between sections with no meaningful source.
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("foo.asm", 1);
        sec.EmitByte(0x00);
        sec.SetSourceLocation(null, 0);
        sec.EmitByte(0x00);
        sec.SetSourceLocation("foo.asm", 2);
        sec.EmitByte(0x00);
        await Assert.That(sec.LineMap.Count).IsEqualTo(2);
        await Assert.That(sec.LineMap[0].ByteCount).IsEqualTo(1);
        await Assert.That(sec.LineMap[1].Offset).IsEqualTo(2);
    }

    [Test]
    public async Task ReserveByte_CountsTowardLineMap()
    {
        // Unresolved operands (jump to forward-declared label) emit via
        // ReserveByte/ReserveWord. Those bytes still belong to the
        // instruction's source line.
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("foo.asm", 12);
        sec.EmitByte(0xC3);    // JP n16 opcode
        sec.ReserveWord();     // operand placeholder
        await Assert.That(sec.LineMap.Count).IsEqualTo(1);
        await Assert.That(sec.LineMap[0].ByteCount).IsEqualTo(3);
    }

    [Test]
    public async Task ReserveBytes_WithFill_CountsTowardLineMap()
    {
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("foo.asm", 3);
        sec.ReserveBytes(5, 0xAA);
        await Assert.That(sec.LineMap.Count).IsEqualTo(1);
        await Assert.That(sec.LineMap[0].ByteCount).IsEqualTo(5);
    }

    [Test]
    public async Task TruncateTo_RemovesEntriesPastCutoff()
    {
        // UNION/NEXTU rewinds bytes; line map entries past the cutoff
        // must be dropped so surviving offsets still line up.
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("foo.asm", 1);
        sec.EmitByte(0x00);
        sec.SetSourceLocation("foo.asm", 2);
        sec.EmitByte(0x00);
        sec.SetSourceLocation("foo.asm", 3);
        sec.EmitByte(0x00);

        sec.TruncateTo(1);

        await Assert.That(sec.LineMap.Count).IsEqualTo(1);
        await Assert.That(sec.LineMap[0].Line).IsEqualTo(1u);
        await Assert.That(sec.LineMap[0].ByteCount).IsEqualTo(1);
    }

    [Test]
    public async Task TruncateTo_ClipsStraddlingEntry()
    {
        var sec = new SectionBuffer("Main", SectionType.Rom0);
        sec.SetSourceLocation("foo.asm", 1);
        sec.EmitByte(0x00);
        sec.EmitByte(0x00);
        sec.EmitByte(0x00); // single 3-byte entry [0, 3)

        sec.TruncateTo(2);

        await Assert.That(sec.LineMap.Count).IsEqualTo(1);
        await Assert.That(sec.LineMap[0].Offset).IsEqualTo(0);
        await Assert.That(sec.LineMap[0].ByteCount).IsEqualTo(2);
    }
}
