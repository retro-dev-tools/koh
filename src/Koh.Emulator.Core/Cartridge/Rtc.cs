using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Cartridge;

public struct Rtc
{
    public byte Seconds;
    public byte Minutes;
    public byte Hours;
    public byte DayLow;
    public byte DayHighAndFlags;   // bit 0 = day high, bit 6 = halt, bit 7 = day carry

    public byte LatchedSeconds;
    public byte LatchedMinutes;
    public byte LatchedHours;
    public byte LatchedDayLow;
    public byte LatchedDayHighAndFlags;

    public long BaseUnixSeconds;

    public void Latch()
    {
        LatchedSeconds = Seconds;
        LatchedMinutes = Minutes;
        LatchedHours = Hours;
        LatchedDayLow = DayLow;
        LatchedDayHighAndFlags = DayHighAndFlags;
    }

    public void AdvanceFromHost(long currentUnixSeconds)
    {
        // Bootstrap: first call after construction anchors the base. No advance.
        if (BaseUnixSeconds == 0) { BaseUnixSeconds = currentUnixSeconds; return; }
        if ((DayHighAndFlags & 0x40) != 0) { BaseUnixSeconds = currentUnixSeconds; return; }
        long delta = currentUnixSeconds - BaseUnixSeconds;
        BaseUnixSeconds = currentUnixSeconds;
        if (delta <= 0) return;

        long totalSec = Seconds + delta;
        Seconds = (byte)(totalSec % 60);
        long totalMin = Minutes + totalSec / 60;
        Minutes = (byte)(totalMin % 60);
        long totalHr = Hours + totalMin / 60;
        Hours = (byte)(totalHr % 24);
        long totalDay = (((long)(DayHighAndFlags & 1) << 8) | DayLow) + totalHr / 24;
        if (totalDay > 0x1FF)
        {
            DayHighAndFlags |= 0x80;  // day carry
            totalDay &= 0x1FF;
        }
        DayLow = (byte)(totalDay & 0xFF);
        DayHighAndFlags = (byte)((DayHighAndFlags & 0xFE) | (int)((totalDay >> 8) & 1));
    }

    public void WriteState(StateWriter w)
    {
        w.WriteByte(Seconds); w.WriteByte(Minutes); w.WriteByte(Hours);
        w.WriteByte(DayLow); w.WriteByte(DayHighAndFlags);
        w.WriteByte(LatchedSeconds); w.WriteByte(LatchedMinutes); w.WriteByte(LatchedHours);
        w.WriteByte(LatchedDayLow); w.WriteByte(LatchedDayHighAndFlags);
        w.WriteI64(BaseUnixSeconds);
    }

    public void ReadState(StateReader r)
    {
        Seconds = r.ReadByte(); Minutes = r.ReadByte(); Hours = r.ReadByte();
        DayLow = r.ReadByte(); DayHighAndFlags = r.ReadByte();
        LatchedSeconds = r.ReadByte(); LatchedMinutes = r.ReadByte(); LatchedHours = r.ReadByte();
        LatchedDayLow = r.ReadByte(); LatchedDayHighAndFlags = r.ReadByte();
        BaseUnixSeconds = r.ReadI64();
    }
}
