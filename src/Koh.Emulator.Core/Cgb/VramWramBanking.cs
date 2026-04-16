using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Cgb;

public sealed class VramWramBanking
{
    public byte VramBank;   // 0 or 1 on CGB, always 0 on DMG
    public byte WramBank = 1;   // 1..7 on CGB

    public byte ReadVbkRegister() => (byte)(0xFE | (VramBank & 1));
    public void WriteVbkRegister(byte value) => VramBank = (byte)(value & 1);

    public byte ReadSvbkRegister() => (byte)(0xF8 | (WramBank & 7));
    public void WriteSvbkRegister(byte value)
    {
        int bank = value & 7;
        WramBank = (byte)(bank == 0 ? 1 : bank);
    }

    public void WriteState(StateWriter w) { w.WriteByte(VramBank); w.WriteByte(WramBank); }
    public void ReadState(StateReader r) { VramBank = r.ReadByte(); WramBank = r.ReadByte(); }
}
