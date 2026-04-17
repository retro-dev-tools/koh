using Koh.Emulator.App.Services;

namespace Koh.Emulator.Maui;

public partial class MainPage : ContentPage
{
    private readonly EmulatorHost _host;

    public MainPage(EmulatorHost host)
    {
        InitializeComponent();
        _host = host;
        _host.StateChanged += OnStateChanged;
    }

    private void OnStateChanged()
    {
        var title = _host.System?.Cartridge.Header.Title is { Length: > 0 } t
            ? $"Koh Emulator — {t}"
            : "Koh Emulator";
        MainThread.BeginInvokeOnMainThread(() => Window!.Title = title);
    }

    protected override void OnDisappearing()
    {
        _host.StateChanged -= OnStateChanged;
        base.OnDisappearing();
    }
}
