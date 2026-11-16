public static class Library
{
    static T Identity<T>(T value) => value;

    public static T Forward<T>(T value) => Identity(value);

    public static float Unused() => 0.25f;
}
