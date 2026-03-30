using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Binding;

/// <summary>
/// Tracks the conditional assembly state machine (IF/ELIF/ELSE/ENDC nesting).
/// Stack-based: each IF pushes a frame, ELIF/ELSE update the top, ENDC pops.
/// </summary>
internal sealed class ConditionalAssemblyState
{
    private readonly Stack<bool> _branchTakenStack = new();
    private readonly Stack<bool> _elseSeenStack = new(); // Track whether ELSE has been seen for each IF
    private int _skipDepth;

    /// <summary>True when inside a false conditional branch — nodes should be skipped.</summary>
    public bool IsSuppressed => _skipDepth > 0;

    /// <summary>
    /// Handle IF. The condition evaluator is called lazily — not called when
    /// already inside a skipped outer branch.
    /// </summary>
    public void HandleIf(Func<bool> evaluateCondition)
    {
        if (_skipDepth > 0)
        {
            _skipDepth++;
            _branchTakenStack.Push(false); // placeholder frame
            _elseSeenStack.Push(false);
        }
        else
        {
            var condValue = evaluateCondition();
            _branchTakenStack.Push(condValue);
            _elseSeenStack.Push(false);
            if (!condValue)
                _skipDepth = 1;
        }
    }

    /// <summary>True if the current IF block has already seen an ELSE.</summary>
    public bool HasSeenElse => _elseSeenStack.Count > 0 && _elseSeenStack.Peek();

    /// <summary>
    /// Handle ELIF. Returns false if orphaned (no matching IF).
    /// The condition evaluator is called lazily — only when no prior branch
    /// was taken and we're not deeply nested in a skip.
    /// </summary>
    public bool HandleElif(Func<bool> evaluateCondition)
    {
        if (_branchTakenStack.Count == 0) return false; // orphaned
        if (_skipDepth > 1) return true; // deeply nested skip — matched but irrelevant

        if (_branchTakenStack.TryPeek(out var taken) && taken)
        {
            _skipDepth = 1;
        }
        else
        {
            _skipDepth = 0; // unskip so we can evaluate
            var condValue = evaluateCondition();
            if (_branchTakenStack.Count > 0) _branchTakenStack.Pop();
            _branchTakenStack.Push(condValue);
            if (!condValue)
                _skipDepth = 1;
        }
        return true;
    }

    /// <summary>
    /// Handle ELSE. Returns false if orphaned (no matching IF).
    /// Returns 2 if ELSE after ELSE (duplicate ELSE).
    /// </summary>
    public int HandleElseEx()
    {
        if (_branchTakenStack.Count == 0) return 0; // orphaned
        if (_skipDepth > 1) return 1; // deeply nested skip — matched but irrelevant

        // Check for duplicate ELSE
        if (_elseSeenStack.Count > 0 && _elseSeenStack.Peek())
            return 2; // duplicate ELSE

        // Mark ELSE as seen
        if (_elseSeenStack.Count > 0) { _elseSeenStack.Pop(); _elseSeenStack.Push(true); }

        if (_branchTakenStack.TryPeek(out var taken) && taken)
        {
            _skipDepth = 1;
        }
        else
        {
            if (_branchTakenStack.Count > 0) _branchTakenStack.Pop();
            _branchTakenStack.Push(true);
            _skipDepth = 0;
        }
        return 1;
    }

    /// <summary>
    /// Handle ELSE. Returns false if orphaned (no matching IF).
    /// </summary>
    public bool HandleElse() => HandleElseEx() >= 1;

    /// <summary>
    /// Handle ENDC. Returns true if the ENDC was matched; false if orphaned
    /// (caller should report a diagnostic).
    /// </summary>
    public bool HandleEndc()
    {
        if (_skipDepth > 1)
        {
            _skipDepth--;
            if (_branchTakenStack.Count > 0) _branchTakenStack.Pop();
            if (_elseSeenStack.Count > 0) _elseSeenStack.Pop();
            return true;
        }

        _skipDepth = 0;

        if (_branchTakenStack.Count > 0)
        {
            _branchTakenStack.Pop();
            if (_elseSeenStack.Count > 0) _elseSeenStack.Pop();
            return true;
        }

        return false; // orphaned ENDC
    }

    /// <summary>True if there are unclosed IF blocks remaining.</summary>
    public bool HasUnclosedBlocks => _branchTakenStack.Count > 0;

    public void Reset()
    {
        _branchTakenStack.Clear();
        _elseSeenStack.Clear();
        _skipDepth = 0;
    }
}
