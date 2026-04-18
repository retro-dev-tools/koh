using System.Collections.Immutable;

namespace KohUI;

/// <summary>
/// Serialisable, backend-agnostic description of a rendered widget: the
/// element type, its properties, and its children. Views produce these;
/// the reconciler diffs old vs new trees and emits patches for backends
/// to consume. Both DomBackend (HTML patches over WebSocket) and the
/// future SkiaBackend (AccessKit nodes + Skia draws) consume the same
/// tree.
///
/// Properties are a flat <c>string → object?</c> map rather than a
/// strongly-typed record so reconciliation can diff by key without
/// reflection — critical for AOT and for keeping the patch protocol
/// open to widget types the backend doesn't statically know about.
/// </summary>
public sealed record RenderNode(
    string Type,
    ImmutableDictionary<string, object?> Props,
    ImmutableArray<RenderNode> Children,
    string? Key = null)
{
    public static readonly ImmutableArray<RenderNode> NoChildren = ImmutableArray<RenderNode>.Empty;
    public static readonly ImmutableDictionary<string, object?> NoProps = ImmutableDictionary<string, object?>.Empty;

    public static RenderNode Leaf(string type, ImmutableDictionary<string, object?>? props = null, string? key = null)
        => new(type, props ?? NoProps, NoChildren, key);

    public static RenderNode WithChildren(string type, ImmutableArray<RenderNode> children, ImmutableDictionary<string, object?>? props = null, string? key = null)
        => new(type, props ?? NoProps, children, key);
}

/// <summary>
/// Convenience builder for <see cref="RenderNode"/> props; avoids the
/// verbose <see cref="ImmutableDictionary"/> syntax at call sites inside
/// widget <c>Render()</c> methods.
/// </summary>
public static class Props
{
    public static ImmutableDictionary<string, object?> Of(params (string Key, object? Value)[] pairs)
    {
        var b = ImmutableDictionary.CreateBuilder<string, object?>();
        foreach (var (k, v) in pairs) b[k] = v;
        return b.ToImmutable();
    }
}
