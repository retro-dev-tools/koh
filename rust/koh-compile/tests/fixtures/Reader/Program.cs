public static class Program
{
    public static unsafe void Main()
    {
        int value = Add(17, 25);
        *(byte*)0xC900 = (byte)value;
    }

    public static int Add(int left, int right) => left + right;

    public static int WithException(int value)
    {
        try
        {
            return 100 / value;
        }
        catch (System.DivideByZeroException)
        {
            return 0;
        }
    }
}
