using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Tests;

public class SerialTests
{
    [Test]
    public async Task Write_SC_Start_Transfer_Raises_Interrupt_After_4096_TCycles()
    {
        var s = new Serial.Serial();
        var irq = default(Interrupts);

        s.WriteSB(0x42);
        s.WriteSC(0x81);

        // 8 bits × 512 T-cycles = 4096 T-cycles to complete.
        for (int i = 0; i < 4095; i++) s.TickT(ref irq);
        await Assert.That(s.IsTransferring).IsTrue();
        await Assert.That((irq.IF & Interrupts.Serial)).IsEqualTo((byte)0);

        s.TickT(ref irq);
        await Assert.That(s.IsTransferring).IsFalse();
        await Assert.That((irq.IF & Interrupts.Serial)).IsEqualTo(Interrupts.Serial);
    }

    [Test]
    public async Task SC_Clears_Bit_7_After_Transfer()
    {
        var s = new Serial.Serial();
        var irq = default(Interrupts);
        s.WriteSC(0x81);
        for (int i = 0; i < 4096; i++) s.TickT(ref irq);
        await Assert.That(s.ReadSC() & 0x80).IsEqualTo(0);
    }

    [Test]
    public async Task SB_Byte_Is_Captured_At_Transfer_Start()
    {
        var s = new Serial.Serial();
        s.WriteSB((byte)'H');
        s.WriteSC(0x81);
        await Assert.That(s.ReadBufferAsString()).IsEqualTo("H");
    }

    [Test]
    public async Task External_Clock_Transfer_Does_Not_Advance_Without_Peer()
    {
        var s = new Serial.Serial();
        var irq = default(Interrupts);
        s.WriteSB(0x42);
        s.WriteSC(0x80);  // internal-clock bit off — external clock, no peer
        for (int i = 0; i < 4096; i++) s.TickT(ref irq);
        await Assert.That(s.IsTransferring).IsFalse();
        await Assert.That((irq.IF & Interrupts.Serial)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Linked_Peer_Exchanges_Byte_On_Transfer()
    {
        var s = new Serial.Serial { Link = new LoopbackLink(peerByte: 0xA5) };
        var irq = default(Interrupts);

        s.WriteSB(0x5A);
        s.WriteSC(0x81);
        for (int i = 0; i < 4096; i++) s.TickT(ref irq);

        // After 8 bits shifted in, SB holds the peer's byte.
        await Assert.That(s.ReadSB()).IsEqualTo((byte)0xA5);
        // Sent byte was observed by the loopback.
        await Assert.That(((LoopbackLink)s.Link!).LastSent).IsEqualTo((byte)0x5A);
        await Assert.That((irq.IF & Interrupts.Serial)).IsEqualTo(Interrupts.Serial);
    }

    private sealed class LoopbackLink : Serial.ISerialLink
    {
        private readonly byte _peerByte;
        public byte LastSent;
        public LoopbackLink(byte peerByte) { _peerByte = peerByte; }
        public byte ExchangeByte(byte sent) { LastSent = sent; return _peerByte; }
    }
}
