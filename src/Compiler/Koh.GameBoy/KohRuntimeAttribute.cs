namespace Koh.GameBoy;

/// <summary>
/// Marks a method as the ROM implementation the compiler CALLS for a given operation — the mirror of
/// <see cref="KohIntrinsicAttribute"/> ("the compiler implements this"). <c>[KohRuntime("f32.add")]</c>
/// on a method means: when the frontend sees the corresponding source-level operation (e.g. an IL
/// <c>add</c> on <c>float32</c>), it calls this method rather than emitting inline code for it. The
/// Koh CIL frontend matches this attribute by simple type name (it never references
/// <c>Koh.GameBoy</c>), so it stays a plain, dependency-free marker.
/// </summary>
/// <param name="key">
/// The operation this method implements, e.g. <c>"f32.add"</c>, <c>"f32.toI32"</c>, <c>"f64.cmp"</c>.
/// Namespaced by value-kind + operation so lookups are unambiguous; the compiler owns the vocabulary.
/// </param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class KohRuntimeAttribute(string key) : Attribute
{
    /// <summary>The operation key this method implements, e.g. <c>"f32.add"</c>.</summary>
    public string Key { get; } = key;
}
