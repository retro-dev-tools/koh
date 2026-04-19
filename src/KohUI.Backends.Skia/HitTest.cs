namespace KohUI.Backends.Skia;

/// <summary>
/// Resolves a window-local point to the deepest <see cref="LayoutNode"/>
/// whose bounds contain it and whose source carries an <c>onClick</c>
/// delegate. Walks children last-first so siblings drawn on top win.
/// </summary>
public static class HitTest
{
    public static LayoutNode? Find(LayoutNode root, int x, int y)
    {
        if (!root.Bounds.Contains(x, y)) return null;

        // Try the deepest children first. If a child contains the point
        // and has a handler, that wins; otherwise keep bubbling up.
        for (int i = root.Children.Length - 1; i >= 0; i--)
        {
            var hit = Find(root.Children[i], x, y);
            if (hit is not null) return hit;
        }
        return HasHandler(root) ? root : null;
    }

    private static bool HasHandler(LayoutNode node)
        => node.Source.Props.TryGetValue("onClick", out var v) && v is Delegate;
}
