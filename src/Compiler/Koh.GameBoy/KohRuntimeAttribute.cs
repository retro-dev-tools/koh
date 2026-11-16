namespace Koh.GameBoy;

/// <summary>Software runtime operation key.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class KohRuntimeAttribute(string key) : Attribute
{
    /// <summary>Operation key.</summary>
    public string Key { get; } = key;
}
