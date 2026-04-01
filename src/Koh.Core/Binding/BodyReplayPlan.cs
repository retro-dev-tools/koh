namespace Koh.Core.Binding;

internal enum BodyReplayKind { Structural, RequiresTextReplay }

internal sealed record BodyReplayPlan(BodyReplayKind Kind, TextReplayReason? Reason = null);
