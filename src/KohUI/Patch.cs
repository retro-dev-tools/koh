using System.Collections.Immutable;

namespace KohUI;

/// <summary>
/// A single change to apply to a backend's rendered view. Backends
/// consume a list of patches each tick and update their native or HTML
/// representation to match.
///
/// Paths are dot-separated index sequences from the root: e.g.
/// <c>"0.2.1"</c> addresses <c>root.children[0].children[2].children[1]</c>.
/// An empty path is the root itself.
/// </summary>
public abstract record Patch(string Path);

/// <summary>Wholesale replace the subtree at <paramref name="Path"/>.</summary>
public sealed record ReplaceNode(string Path, RenderNode Node) : Patch(Path);

/// <summary>Update a subset of props on the node at <paramref name="Path"/>. Props not in the dictionary keep their previous values.</summary>
public sealed record UpdateProps(string Path, ImmutableDictionary<string, object?> Changed, ImmutableArray<string> Removed) : Patch(Path);

/// <summary>Append a new child at the end of the children list under <paramref name="Path"/>.</summary>
public sealed record InsertChild(string Path, int Index, RenderNode Node) : Patch(Path);

/// <summary>Remove the child at the given index under <paramref name="Path"/>.</summary>
public sealed record RemoveChild(string Path, int Index) : Patch(Path);
