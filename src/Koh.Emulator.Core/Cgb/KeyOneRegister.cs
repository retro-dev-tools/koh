using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Cgb;

public sealed class KeyOneRegister
{
    public bool SwitchArmed;
    public bool DoubleSpeed;

    public void WriteState(StateWriter w) { w.WriteBool(SwitchArmed); w.WriteBool(DoubleSpeed); }
    public void ReadState(StateReader r) { SwitchArmed = r.ReadBool(); DoubleSpeed = r.ReadBool(); }

    public byte Read()
        => (byte)(0x7E | (DoubleSpeed ? 0x80 : 0) | (SwitchArmed ? 0x01 : 0));

    public void Write(byte value)
    {
        SwitchArmed = (value & 0x01) != 0;
    }

    /// <summary>
    /// Called when the CPU executes a STOP instruction. If switch is armed,
    /// toggle speed and disarm.
    /// </summary>
    public void OnStopExecuted()
    {
        if (SwitchArmed)
        {
            DoubleSpeed = !DoubleSpeed;
            SwitchArmed = false;
        }
    }
}
