using Koh.Emulator.Core.Serial;
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Prototype WebRTC-backed <see cref="ISerialLink"/>. Signaling is manual
/// paste-swap between the two instances — call <see cref="CreateOfferAsync"/>
/// on one peer, share the SDP string, call <see cref="AcceptOfferAsync"/> on
/// the other, share the answer back, and call <see cref="ApplyAnswerAsync"/>
/// on the first peer.
///
/// Caveats: <see cref="ExchangeByte"/> is synchronous but WebRTC is async.
/// The outgoing byte is queued for send (no blocking). The returned byte is
/// whatever is currently in the receive queue, falling back to $FF. For
/// real-time link-cable games that have tight handshakes, expect desync. The
/// data channel is fine for casual use (e.g. Pokémon trades which tolerate
/// lots of latency); tighter sync requires a deeper redesign.
/// </summary>
public sealed class WebRtcLink : ISerialLink, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly DotNetObjectReference<WebRtcLink> _selfRef;

    public bool IsOpen { get; private set; }
    public event Action? Opened;
    public event Action? Closed;

    public WebRtcLink(IJSRuntime js)
    {
        _js = js;
        _selfRef = DotNetObjectReference.Create(this);
    }

    public async ValueTask RegisterAsync()
    {
        await _js.InvokeVoidAsync("kohWebRtcLink.register", _selfRef);
    }

    public ValueTask<string> CreateOfferAsync()
        => _js.InvokeAsync<string>("kohWebRtcLink.createOffer");

    public ValueTask<string> AcceptOfferAsync(string offerJson)
        => _js.InvokeAsync<string>("kohWebRtcLink.acceptOffer", offerJson);

    public ValueTask ApplyAnswerAsync(string answerJson)
        => _js.InvokeVoidAsync("kohWebRtcLink.applyAnswer", answerJson);

    public byte ExchangeByte(byte sent)
    {
        // Fire the send; don't await — the emulator's TickT is synchronous.
        _ = _js.InvokeVoidAsync("kohWebRtcLink.sendByte", sent);

        // Read whatever the peer has already delivered. Uses the synchronous
        // JS-interop fast path only when running in Blazor WASM; otherwise
        // returns $FF (no peer byte available yet).
        if (_js is IJSInProcessRuntime inProc)
        {
            int next = inProc.Invoke<int>("kohWebRtcLink.drainReceived");
            return next < 0 ? (byte)0xFF : (byte)next;
        }
        return 0xFF;
    }

    [JSInvokable]
    public void OnLinkOpened() { IsOpen = true; Opened?.Invoke(); }

    [JSInvokable]
    public void OnLinkClosed() { IsOpen = false; Closed?.Invoke(); }

    public async ValueTask DisposeAsync()
    {
        try { await _js.InvokeVoidAsync("kohWebRtcLink.close"); }
        catch { /* JS host may already be gone. */ }
        _selfRef.Dispose();
    }
}
