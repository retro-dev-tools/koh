using System.Collections.Immutable;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Binding;

internal enum LoopControl { Continue, Break }

/// <summary>
/// Mutable macro argument frame. SHIFT mutates <see cref="ShiftOffset"/> within
/// the owning expansion scope. The <see cref="ImmutableStack{T}"/> in
/// <see cref="ExpansionContext"/> provides structural ownership (visibility);
/// frame internals are mutable for SHIFT.
/// </summary>
internal sealed class MacroFrame
{
    public IReadOnlyList<string> Args { get; }
    public int ShiftOffset { get; set; }
    public int UniqueId { get; set; }
    public int Narg => Math.Max(0, Args.Count - ShiftOffset);

    public string GetArg(int oneBasedIndex)
    {
        int i = oneBasedIndex - 1 + ShiftOffset;
        return i >= 0 && i < Args.Count ? Args[i] : "";
    }

    public string AllArgs()
    {
        var remaining = new List<string>();
        for (int i = ShiftOffset; i < Args.Count; i++)
            remaining.Add(Args[i]);
        return string.Join(", ", remaining);
    }

    public MacroFrame(IReadOnlyList<string> args) => Args = args;
}

internal sealed record ExpansionContext
{
    public SourceText? SourceText { get; init; }
    public string FilePath { get; init; } = "";
    public ExpansionTrace Trace { get; init; } = ExpansionTrace.Empty;
    public ImmutableStack<MacroFrame> MacroFrames { get; init; } = ImmutableStack<MacroFrame>.Empty;
    public int StructuralDepth { get; init; }
    public int ReplayDepth { get; init; }
    public int MacroBodyDepth { get; init; }
    public int LoopDepth { get; init; }

    public MacroFrame? CurrentMacroFrame
        => MacroFrames.IsEmpty ? null : MacroFrames.Peek();

    public ExpansionContext ForMacro(MacroFrame frame, MacroDefinition macro)
        => this with
        {
            MacroFrames = MacroFrames.Push(frame),
            MacroBodyDepth = MacroBodyDepth + 1,
            StructuralDepth = StructuralDepth + 1,
            Trace = Trace.Push(ExpansionFrame.ForMacro(macro))
        };

    public ExpansionContext ForLoop(ExpansionFrame loopFrame)
        => this with
        {
            LoopDepth = LoopDepth + 1,
            Trace = Trace.Push(loopFrame)
        };

    public ExpansionContext ForInclude(string filePath, SourceText source, TextSpan directiveSpan)
        => this with
        {
            SourceText = source,
            FilePath = filePath,
            StructuralDepth = StructuralDepth + 1,
            Trace = Trace.Push(ExpansionFrame.ForInclude(filePath, directiveSpan))
        };

    public ExpansionContext ForTextReplay(SourceText replaySource, TextSpan triggerSpan,
        TextReplayReason reason)
        => this with
        {
            SourceText = replaySource,
            ReplayDepth = ReplayDepth + 1,
            Trace = Trace.Push(ExpansionFrame.ForTextReplay(FilePath, triggerSpan, reason))
        };
}
