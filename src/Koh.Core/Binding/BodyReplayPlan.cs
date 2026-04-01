namespace Koh.Core.Binding;

internal enum BodyReplayKind { Structural, RequiresTextReplay }

/// <summary>
/// Result of body classification for REPT/FOR. For the text-replay FOR path,
/// <see cref="IdentifierPositions"/> carries pre-collected variable positions
/// from the classification parse, avoiding a redundant second parse.
/// </summary>
internal sealed record BodyReplayPlan(
    BodyReplayKind Kind,
    TextReplayReason? Reason = null,
    List<(int Start, int Length)>? IdentifierPositions = null);
