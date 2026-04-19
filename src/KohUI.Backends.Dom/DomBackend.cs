using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KohUI;

namespace KohUI.Backends.Dom;

/// <summary>
/// Wires a <see cref="Runner{TModel, TMsg}"/> to one or more connected
/// browser clients over WebSockets.
///
/// <para>
/// On connect: serialise the runner's current render tree and send as
/// an initial <c>replace</c>. On every update tick: serialise the
/// patch list and broadcast. Inbound events (<c>{"op":"event",
/// "path":"…","event":"click"}</c>) are looked up against the runner's
/// current tree, the matching event-prop delegate is invoked, and the
/// returned message goes through <see cref="Runner{TModel, TMsg}.Dispatch"/>.
/// </para>
///
/// <para>
/// Each connection is independent; the backend keeps a concurrent set
/// of active sockets and broadcasts patches to all. Connections
/// attached mid-session receive the full current tree first so they're
/// immediately in sync — the initial render is always the full tree,
/// not "replay patches from t=0".
/// </para>
/// </summary>
public sealed class DomBackend<TModel, TMsg>
{
    private readonly Runner<TModel, TMsg> _runner;
    private readonly ConcurrentDictionary<Guid, WebSocket> _connections = new();
    private byte[]? _lastInitialRenderJson;

    public DomBackend(Runner<TModel, TMsg> runner)
    {
        _runner = runner;
        _runner.OnInitialRender += OnInitialRender;
        _runner.OnPatchesReady += OnPatchesReady;
        if (runner.CurrentRender is { } already) OnInitialRender(already);
    }

    private void OnInitialRender(RenderNode root)
    {
        _lastInitialRenderJson = JsonPatchSerializer.SerializeInitial(root);
    }

    private void OnPatchesReady(IReadOnlyList<Patch> patches)
    {
        var bytes = JsonPatchSerializer.SerializePatches(patches);
        BroadcastAsync(bytes).ContinueWith(t =>
        {
            // Swallowing is intentional — a single dropped client shouldn't
            // tear down the runner; the cleanup loop in HandleAsync will
            // drop the socket from the set when it fails.
            _ = t.Exception;
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Handle one WebSocket connection for its lifetime. Send the
    /// initial tree on connect, then loop on inbound events until the
    /// socket closes.
    /// </summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _connections[id] = socket;

        try
        {
            if (_lastInitialRenderJson is { } initial)
                await SendAsync(socket, initial, ct);

            var buffer = new byte[8 * 1024];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                HandleInboundEvent(buffer.AsSpan(0, result.Count));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { /* client went away */ }
        finally
        {
            _connections.TryRemove(id, out _);
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { /* best-effort */ }
            }
        }
    }

    private void HandleInboundEvent(ReadOnlySpan<byte> json)
    {
        // Shape: {"op":"event","path":"0.1","event":"click"}  (no value)
        //    or: {"op":"event","path":"0.3","event":"change","value":"hi"}
        var reader = new Utf8JsonReader(json);
        string? op = null, path = null, evt = null, value = null;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString();
            reader.Read();
            var s = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            switch (name)
            {
                case "op":    op = s;    break;
                case "path":  path = s;  break;
                case "event": evt = s;   break;
                case "value": value = s; break;
            }
        }
        if (op != "event" || path is null || evt is null) return;

        var handler = FindHandler(_runner.CurrentRender, path, evt);
        if (handler is null) return;

        try
        {
            // Dispatch shape depends on the event: change events carry
            // a string payload (TextBox.OnChange is Func<string, TMsg>);
            // click / close are zero-arg (Func<TMsg>).
            object? result = value is not null
                ? handler.DynamicInvoke(value)
                : handler.DynamicInvoke();
            if (result is TMsg msg)
                _runner.Dispatch(msg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[kohui-dom] event handler threw at {path}.{evt}: {ex.Message}");
        }
    }

    private static Delegate? FindHandler(RenderNode? root, string path, string evt)
    {
        if (root is null) return null;
        var node = root;
        if (path.Length > 0)
        {
            foreach (var seg in path.Split('.'))
            {
                if (!int.TryParse(seg, out var i) || i < 0 || i >= node.Children.Length) return null;
                node = node.Children[i];
            }
        }
        // Event key in the render tree is "on" + capitalised event name,
        // e.g. "onClick" for the "click" DOM event.
        var propKey = "on" + char.ToUpperInvariant(evt[0]) + evt[1..];
        return node.Props.TryGetValue(propKey, out var v) ? v as Delegate : null;
    }

    private async Task BroadcastAsync(byte[] bytes)
    {
        var dead = new List<Guid>();
        foreach (var (id, socket) in _connections)
        {
            try { await SendAsync(socket, bytes, CancellationToken.None); }
            catch { dead.Add(id); }
        }
        foreach (var id in dead) _connections.TryRemove(id, out _);
    }

    private static Task SendAsync(WebSocket socket, byte[] bytes, CancellationToken ct)
        => socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
}
