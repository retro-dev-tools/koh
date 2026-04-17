using Koh.Emulator.Core.Joypad;
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

public sealed class KeyboardInputBridge : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly EmulatorHost _host;
    private DotNetObjectReference<KeyboardInputBridge>? _selfRef;
    private bool _registered;

    public KeyboardInputBridge(IJSRuntime js, EmulatorHost host)
    {
        _js = js;
        _host = host;
    }

    public async ValueTask EnsureRegisteredAsync()
    {
        if (_registered) return;
        _selfRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("kohKeyboard.register", _selfRef);
        _registered = true;
    }

    [JSInvokable] public void OnKeyDown(string code) => Dispatch(code, true);
    [JSInvokable] public void OnKeyUp(string code) => Dispatch(code, false);

    private void Dispatch(string code, bool down)
    {
        var button = Map(code);
        if (button is null) return;
        var system = _host.System;
        if (system is null) return;
        if (down) system.JoypadPress(button.Value);
        else system.JoypadRelease(button.Value);
    }

    private static JoypadButton? Map(string code) => code switch
    {
        "ArrowUp"    => JoypadButton.Up,
        "ArrowDown"  => JoypadButton.Down,
        "ArrowLeft"  => JoypadButton.Left,
        "ArrowRight" => JoypadButton.Right,
        "KeyZ"       => JoypadButton.A,
        "KeyX"       => JoypadButton.B,
        "Enter"      => JoypadButton.Start,
        "ShiftRight" => JoypadButton.Select,
        _ => null,
    };

    public ValueTask DisposeAsync()
    {
        _selfRef?.Dispose();
        return ValueTask.CompletedTask;
    }
}
