static class FixedMath
{
    internal static short Sin(byte phase)
    {
        byte quadrant = (byte)(phase >> 6);
        short ramp = (short)((phase & 63) * 2);
        if (quadrant == 0)
            return ramp;
        if (quadrant == 1)
            return (short)(126 - ramp);
        if (quadrant == 2)
            return (short)-ramp;
        return (short)(ramp - 126);
    }

    internal static short Cos(byte phase)
    {
        return Sin((byte)(phase + 64));
    }
}
