namespace KohUI;

/// <summary>
/// A transient view description. Xilem-flavoured: views are cheap,
/// preferably <c>readonly struct</c>s, and are re-created every update
/// tick. They render themselves down to a <see cref="RenderNode"/>
/// tree that the reconciler then diffs against the previous tree to
/// produce patches.
///
/// <para>
/// <typeparamref name="TMsg"/> is the app-level discriminated message
/// union consumed by <c>update</c>. Views carry <c>Func&lt;TMsg&gt;</c>
/// event handlers; the dispatcher invokes them when the corresponding
/// DOM / input event fires and routes the returned message through
/// the MVU loop.
/// </para>
///
/// <para>
/// Container views parameterise their child types (<c>Window&lt;TMsg,
/// TChild&gt;</c>, <c>Tuple2&lt;TMsg, A, B&gt;</c>, etc.) so the
/// compiler can monomorphise the full tree — no boxing, no allocation
/// per render. The only path that boxes is variable-length collections
/// (<see cref="ForEach{TMsg, TItem}"/>), which ship with a documented
/// allocation cost.
/// </para>
/// </summary>
public interface IView<TMsg>
{
    /// <summary>
    /// Produce the serialisable render tree for this view and its
    /// children. Should not allocate beyond the child views it already
    /// holds; every call on the same view must return an equivalent
    /// tree (deterministic, no side effects).
    /// </summary>
    RenderNode Render();
}
