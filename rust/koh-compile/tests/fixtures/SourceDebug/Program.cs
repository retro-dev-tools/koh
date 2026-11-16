public static class Program
{
    private static int Value;

    public static int Main()
    {
#line 100 "mapped source.cs"
        return Helper(Koh.GameBoy.Graphics.Rgb.Make(9, 1, 0));
#line default
    }

    private static int Helper(int value)
    {
#line 200 "other source.cs"
        Value = value;
#line hidden
        Value += 1;
#line 198 "other source.cs"
        return Value;
#line default
    }
}
