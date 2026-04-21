using System.Collections.Immutable;

namespace KohUI.Backends.Gl;

/// <summary>
/// Focus-cycle helpers. The focus "ring" is a single path into the
/// current layout tree — we don't bind focus to widget identity across
/// renders (no KohUI concept of persistent widget ids yet), we bind it
/// to a positional path. A re-layout that preserves tree shape keeps
/// focus where the user left it; a layout that grows or shrinks gets
/// clamped to the nearest valid path.
/// </summary>
internal static class Focus
{
    /// <summary>Flat, in-layout-order list of focusable-widget paths.</summary>
    public static ImmutableArray<string> Enumerate(LayoutNode? root)
    {
        if (root is null) return ImmutableArray<string>.Empty;
        var builder = ImmutableArray.CreateBuilder<string>();
        Visit(root, builder);
        return builder.ToImmutable();
    }

    private static void Visit(LayoutNode node, ImmutableArray<string>.Builder output)
    {
        if (IsFocusable(node)) output.Add(node.Path);
        foreach (var c in node.Children) Visit(c, output);
    }

    public static bool IsFocusable(LayoutNode node)
    {
        // A widget is focusable if it carries *any* event delegate the
        // user could address via keyboard — clickable for buttons/menu
        // items, onChange for text input. Add more slots here as
        // widgets with their own event shapes land.
        if (node.Source.Props.TryGetValue("onClick", out var v1) && v1 is Delegate) return true;
        if (node.Source.Props.TryGetValue("onChange", out var v2) && v2 is Delegate) return true;
        return false;
    }

    public static string? Next(ImmutableArray<string> order, string? current)
    {
        if (order.IsEmpty) return null;
        if (current is null) return order[0];
        int idx = order.IndexOf(current);
        if (idx < 0) return order[0];
        return order[(idx + 1) % order.Length];
    }

    public static string? Prev(ImmutableArray<string> order, string? current)
    {
        if (order.IsEmpty) return null;
        if (current is null) return order[^1];
        int idx = order.IndexOf(current);
        if (idx < 0) return order[^1];
        return order[(idx - 1 + order.Length) % order.Length];
    }

    /// <summary>
    /// Walk the tree looking for a MenuItem whose accelerator character
    /// (the char after its first '&amp;') matches <paramref name="ch"/>.
    /// Case-insensitive. Returns the first match's path or null.
    /// </summary>
    public static string? ResolveAccelerator(LayoutNode? root, char ch)
    {
        if (root is null) return null;
        char target = char.ToUpperInvariant(ch);
        return FindAcc(root, target);
    }

    private static string? FindAcc(LayoutNode node, char target)
    {
        if (node.Source.Type == "MenuItem")
        {
            var text = node.Source.Props.TryGetValue("text", out var v) && v is string s ? s : "";
            int amp = text.IndexOf('&');
            if (amp >= 0 && amp < text.Length - 1
                && char.ToUpperInvariant(text[amp + 1]) == target
                && node.Source.Props.TryGetValue("onClick", out var h) && h is Delegate)
            {
                return node.Path;
            }
        }
        foreach (var c in node.Children)
        {
            var hit = FindAcc(c, target);
            if (hit is not null) return hit;
        }
        return null;
    }

    public static LayoutNode? FindByPath(LayoutNode? root, string? path)
    {
        if (root is null || path is null) return null;
        if (path == root.Path) return root;
        foreach (var c in root.Children)
        {
            var hit = FindByPath(c, path);
            if (hit is not null) return hit;
        }
        return null;
    }
}
