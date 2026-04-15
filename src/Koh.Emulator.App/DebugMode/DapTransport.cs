using System.Text;
using Microsoft.JSInterop;
using Koh.Debugger.Dap;

namespace Koh.Emulator.App.DebugMode;

/// <summary>
/// Bridges the <see cref="DapDispatcher"/> to VS Code via the postMessage JS bridge.
/// </summary>
public sealed class DapTransport : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly DapDispatcher _dispatcher;
    private DotNetObjectReference<DapTransport>? _selfRef;

    public DapTransport(IJSRuntime js, DapDispatcher dispatcher)
    {
        _js = js;
        _dispatcher = dispatcher;

        _dispatcher.ResponseReady += OnResponseReady;
        _dispatcher.EventReady += OnEventReady;
    }

    public async Task RegisterAsync()
    {
        _selfRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("kohVsCodeBridge.register", _selfRef);
    }

    [JSInvokable]
    public void ReceiveDap(string jsonPayload)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonPayload);
        _dispatcher.HandleRequest(bytes);
    }

    private async void OnResponseReady(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(bytes.Span);
            await _js.InvokeVoidAsync("kohVsCodeBridge.sendToExtension", "dap", payload);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DapTransport] Failed to send DAP response: {ex.Message}");
        }
    }

    private async void OnEventReady(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(bytes.Span);
            await _js.InvokeVoidAsync("kohVsCodeBridge.sendToExtension", "dap", payload);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DapTransport] Failed to send DAP event: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _dispatcher.ResponseReady -= OnResponseReady;
        _dispatcher.EventReady -= OnEventReady;
        _selfRef?.Dispose();
        await ValueTask.CompletedTask;
    }
}
