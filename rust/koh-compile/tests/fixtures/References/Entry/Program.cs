public static class Program
{
    public static unsafe void Main()
    {
        *(byte*)0xC900 = (byte)Game.Bridge.Api.Compute(35);
    }
}
