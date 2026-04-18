using System.Collections.Immutable;

namespace KohUI;

/// <summary>
/// Diffs two <see cref="RenderNode"/> trees and produces a flat
/// <see cref="Patch"/> list. Positional matching with key-based override:
/// if two nodes at the same parent position differ in <c>Type</c> or
/// <c>Key</c>, the old subtree is replaced wholesale. Otherwise props
/// are compared shallowly and children are recursed.
///
/// <para>
/// This is deliberately not a fancy minimum-edit-distance algorithm.
/// For retro tooling UIs (a few dozen to a few hundred nodes) a linear
/// positional diff is both fast and predictable, and reordering in
/// Win98-style chrome is rare — tree shapes shift more than they
/// rearrange. If a widget genuinely needs identity-stable reordering
/// (long list with drag-drop), its items should set <c>Key</c> and the
/// algorithm will fall back to wholesale-replace only when keys
/// conflict — correct, if not optimal.
/// </para>
/// </summary>
public static class Reconciler
{
    /// <summary>
    /// Produces the patch list that transforms <paramref name="previous"/>
    /// into <paramref name="current"/>. When <paramref name="previous"/>
    /// is <c>null</c>, returns a single <see cref="ReplaceNode"/> patch
    /// at the root — suitable for bootstrapping a new connection.
    /// </summary>
    public static IReadOnlyList<Patch> Diff(RenderNode? previous, RenderNode current)
    {
        if (previous is null)
            return [new ReplaceNode("", current)];

        var patches = new List<Patch>();
        DiffNode(previous, current, "", patches);
        return patches;
    }

    private static void DiffNode(RenderNode prev, RenderNode curr, string path, List<Patch> patches)
    {
        if (prev.Type != curr.Type || prev.Key != curr.Key)
        {
            patches.Add(new ReplaceNode(path, curr));
            return;
        }

        var (changed, removed) = DiffProps(prev.Props, curr.Props);
        if (changed.Count > 0 || removed.Length > 0)
            patches.Add(new UpdateProps(path, changed, removed));

        DiffChildren(prev.Children, curr.Children, path, patches);
    }

    private static (ImmutableDictionary<string, object?> Changed, ImmutableArray<string> Removed)
        DiffProps(ImmutableDictionary<string, object?> prev, ImmutableDictionary<string, object?> curr)
    {
        var changedBuilder = ImmutableDictionary.CreateBuilder<string, object?>();
        foreach (var kv in curr)
        {
            if (!prev.TryGetValue(kv.Key, out var oldValue) || !Equals(oldValue, kv.Value))
                changedBuilder[kv.Key] = kv.Value;
        }

        var removedBuilder = ImmutableArray.CreateBuilder<string>();
        foreach (var kv in prev)
        {
            if (!curr.ContainsKey(kv.Key)) removedBuilder.Add(kv.Key);
        }

        return (changedBuilder.ToImmutable(), removedBuilder.ToImmutable());
    }

    private static void DiffChildren(
        ImmutableArray<RenderNode> prev,
        ImmutableArray<RenderNode> curr,
        string parentPath,
        List<Patch> patches)
    {
        int shared = Math.Min(prev.Length, curr.Length);
        for (int i = 0; i < shared; i++)
            DiffNode(prev[i], curr[i], Join(parentPath, i), patches);

        // Inserts: curr longer than prev.
        for (int i = shared; i < curr.Length; i++)
            patches.Add(new InsertChild(parentPath, i, curr[i]));

        // Removes: iterate high-to-low so each index stays valid after
        // earlier removes are applied.
        for (int i = prev.Length - 1; i >= curr.Length; i--)
            patches.Add(new RemoveChild(parentPath, i));
    }

    private static string Join(string parentPath, int childIndex)
        => parentPath.Length == 0 ? childIndex.ToString() : $"{parentPath}.{childIndex}";
}
