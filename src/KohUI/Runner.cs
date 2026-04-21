using System.Threading.Channels;

namespace KohUI;

/// <summary>
/// The MVU loop. Given a pure <c>update(msg, model) =&gt; newModel</c> and
/// a pure <c>view(model) =&gt; IView</c>, the runner:
///
/// <list type="number">
///   <item>Owns the current <typeparamref name="TModel"/>.</item>
///   <item>Exposes <see cref="Dispatch"/> so backends can push input events.</item>
///   <item>Serialises message processing so <c>update</c> never runs concurrently.</item>
///   <item>After every message, calls <c>view</c>, diffs the new render tree
///         against the previous one, and notifies <see cref="OnPatchesReady"/>
///         subscribers with the patch list.</item>
/// </list>
///
/// No timers, no animation loop. Views render only in response to messages —
/// a Win98-era UI doesn't animate anyway, and if later we want
/// requestAnimationFrame-style ticks, a backend can dispatch its own
/// <c>Tick</c> message on a cadence it owns.
/// </summary>
public sealed class Runner<TModel, TMsg> : IAsyncDisposable
{
    private readonly Func<TMsg, TModel, TModel> _update;
    private readonly Func<TModel, IView<TMsg>> _view;
    private readonly Channel<TMsg> _messages = Channel.CreateUnbounded<TMsg>(new UnboundedChannelOptions
    {
        SingleReader = true,   // the background loop is the only consumer
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    private TModel _model;
    private RenderNode? _lastRender;

    /// <summary>Fires after each message is processed, carrying the diffed patch list.</summary>
    public event Action<IReadOnlyList<Patch>>? OnPatchesReady;

    /// <summary>Fires on the first render only, carrying the complete tree so a new connection can sync up.</summary>
    public event Action<RenderNode>? OnInitialRender;

    public Runner(TModel initialModel, Func<TMsg, TModel, TModel> update, Func<TModel, IView<TMsg>> view)
    {
        _model = initialModel;
        _update = update;
        _view = view;
        _loopTask = Task.Run(LoopAsync);
        RenderInitial();
    }

    public TModel CurrentModel => _model;
    public RenderNode? CurrentRender => _lastRender;

    /// <summary>Enqueue a message for the update loop. Never blocks; never drops.</summary>
    public void Dispatch(TMsg msg) => _messages.Writer.TryWrite(msg);

    private void RenderInitial()
    {
        var tree = _view(_model).Render();
        _lastRender = tree;
        OnInitialRender?.Invoke(tree);
    }

    private async Task LoopAsync()
    {
        try
        {
            await foreach (var msg in _messages.Reader.ReadAllAsync(_cts.Token))
            {
                _model = _update(msg, _model);
                var next = _view(_model).Render();
                var patches = Reconciler.Diff(_lastRender, next);
                _lastRender = next;
                if (patches.Count > 0) OnPatchesReady?.Invoke(patches);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _messages.Writer.TryComplete();
        try { await _loopTask; }
        catch (OperationCanceledException) { /* expected */ }
    }
}
