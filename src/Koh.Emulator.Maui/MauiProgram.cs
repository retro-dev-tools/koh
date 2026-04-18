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
        //
        // Everything that takes an IJSRuntime must be Scoped in MAUI Blazor
        // Hybrid: IJSRuntime lives in the BlazorWebView's scope, so singletons
        // that capture it at root-scope activation wind up with a stale runtime
        // and every later invoke throws "Cannot invoke JavaScript outside of a
        // WebView context." Scoped works identically in Blazor WASM (single
        // app scope), so the Shared services are safe to keep uniform.
        builder.Services.AddScoped<RuntimeModeDetector>();
        builder.Services.AddScoped<AudioPipe>();
        builder.Services.AddScoped<EmulatorHost>();
        builder.Services.AddScoped<FramebufferBridge>();
        builder.Services.AddScoped<KeyboardInputBridge>();
        builder.Services.AddScoped<IFileSystemAccess, MauiFileSystemAccess>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Keyboard-to-emulator wiring happens lazily from StandaloneShell on
        // first render; pre-resolving here would activate everything at root
        // scope (no webview yet).
        return builder.Build();
    }
}
