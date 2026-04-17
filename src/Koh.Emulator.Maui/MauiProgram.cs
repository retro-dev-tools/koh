using Microsoft.Extensions.Logging;
using Koh.Emulator.App.Services;
using Koh.Emulator.App.Shell;

namespace Koh.Emulator.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // Same service graph as Koh.Emulator.App (standalone mode only — no
        // DAP transport here; the MAUI shell is for playing ROMs, not debugging).
        builder.Services.AddSingleton<RuntimeModeDetector>();
        builder.Services.AddSingleton<FramePacer>();
        builder.Services.AddSingleton<EmulatorHost>();
        builder.Services.AddSingleton<FramebufferBridge>();
        builder.Services.AddSingleton<WebAudioBridge>();
        builder.Services.AddSingleton<KeyboardInputBridge>();
        builder.Services.AddSingleton<IFileSystemAccess, MauiFileSystemAccess>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Mirrors Koh.Emulator.App/Program.cs: keyboard bridge needs to be attached
        // to EmulatorHost once the container is built.
        var keyboard = app.Services.GetRequiredService<KeyboardInputBridge>();
        app.Services.GetRequiredService<EmulatorHost>().AttachKeyboard(keyboard);

        return app;
    }
}
