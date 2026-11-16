public static class Program
{
    struct Cell
    {
        public float Value;
    }

    static Cell Snapshot;

    static float Add(float left, float right) => left + right;

    static unsafe int Bits(float value) => *(int*)&value;

    static float Choose(bool first, float left, float right) => first ? left : right;

    static void Step(ref float value, float increment) => value = value + increment;

    public static int Main() => Bits(Add(1.5f, 2.5f));

    public static int Negative() => Bits(Add(-2.5f, 0.75f));

    public static int Join() => Bits(Add(Choose(false, 1.5f, 2.5f), 1.5f));

    public static int Reference()
    {
        float value = 1.5f;
        Step(ref value, 2.5f);
        return Bits(value);
    }

    public static int Unsupported() => Bits(1.5f / Choose(false, 2.5f, 3.5f));

    public static int Field()
    {
        Snapshot.Value = 1.5f;
        Snapshot.Value = Add(Snapshot.Value, 2.5f);
        return Bits(Snapshot.Value);
    }

    public static int Array()
    {
        float[] values = new float[2];
        values[0] = 1.5f;
        values[1] = 2.5f;
        return Bits(Add(values[0], values[1]));
    }
}
