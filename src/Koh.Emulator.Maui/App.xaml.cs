namespace Koh.Emulator.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var host = IPlatformApplication.Current!.Services.GetRequiredService<global::Koh.Emulator.App.Services.EmulatorHost>();
        var window = new Window(new MainPage(host)) { Title = "Koh Emulator" };
        return window;
    }
}
