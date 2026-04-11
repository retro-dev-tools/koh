using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap;

/// <summary>
/// Dispatches DAP requests to handlers and emits responses/events as byte buffers.
/// Transport-agnostic — see §8.2. The host (Blazor JS interop, in-process tests)
/// wires bytes in via <see cref="HandleRequest"/> and bytes out via the events.
/// </summary>
public sealed class DapDispatcher
{
    private int _nextOutboundSeq = 1;
    private readonly Dictionary<string, Func<Request, Response>> _handlers = new();

    public event Action<ReadOnlyMemory<byte>>? ResponseReady;
    public event Action<ReadOnlyMemory<byte>>? EventReady;

    public void RegisterHandler(string command, Func<Request, Response> handler)
    {
        _handlers[command] = handler;
    }

    public void HandleRequest(ReadOnlySpan<byte> jsonBytes)
    {
        Request? request;
        try
        {
            request = JsonSerializer.Deserialize(jsonBytes, DapJsonContext.Default.Request);
        }
        catch (JsonException ex)
        {
            EmitErrorResponse(requestSeq: 0, command: "", message: $"invalid JSON: {ex.Message}");
            return;
        }

        if (request is null)
        {
            EmitErrorResponse(0, "", "null request");
            return;
        }

        if (!_handlers.TryGetValue(request.Command, out var handler))
        {
            EmitErrorResponse(request.Seq, request.Command, $"unsupported command '{request.Command}'");
            return;
        }

        Response response;
        try
        {
            response = handler(request);
        }
        catch (Exception ex)
        {
            EmitErrorResponse(request.Seq, request.Command, ex.Message);
            return;
        }

        response.Seq = _nextOutboundSeq++;
        response.Type = "response";
        response.RequestSeq = request.Seq;
        response.Command = request.Command;

        var json = JsonSerializer.SerializeToUtf8Bytes(response, DapJsonContext.Default.Response);
        ResponseReady?.Invoke(json);
    }

    public void SendEvent(string eventName, object? body)
    {
        var evt = new Event
        {
            Seq = _nextOutboundSeq++,
            Type = "event",
            EventName = eventName,
            Body = body,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(evt, DapJsonContext.Default.Event);
        EventReady?.Invoke(json);
    }

    private void EmitErrorResponse(int requestSeq, string command, string message)
    {
        var response = new Response
        {
            Seq = _nextOutboundSeq++,
            Type = "response",
            RequestSeq = requestSeq,
            Success = false,
            Command = command,
            Message = message,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(response, DapJsonContext.Default.Response);
        ResponseReady?.Invoke(json);
    }
}
