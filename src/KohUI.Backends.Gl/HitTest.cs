namespace KohUI.Backends.Gl;

/// <summary>
/// Resolves a window-local point to the deepest <see cref="LayoutNode"/>
/// whose bounds contain it and whose source carries an interactive
/// event delegate (<c>onClick</c> for buttons/menu items,
/// <c>onChange</c> for text input). Walks children last-first so
/// siblings drawn on top win.
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
    {
        if (node.Source.Props.TryGetValue("onClick", out var v1) && v1 is Delegate) return true;
        if (node.Source.Props.TryGetValue("onChange", out var v2) && v2 is Delegate) return true;
        return false;
    }
}
