using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Koh.Emulator.App;
using Koh.Emulator.App.Services;
using Koh.Emulator.App.Shell;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Scoped instead of Singleton for symmetry with the MAUI host — everything
// that takes an IJSRuntime must live inside the webview/app scope so the
// runtime is resolved against the active view. In Blazor WASM this is
// effectively a singleton (one app scope), so behavior is unchanged.
builder.Services.AddScoped<RuntimeModeDetector>();
builder.Services.AddScoped<AudioPipe>();
builder.Services.AddScoped<EmulatorHost>();
builder.Services.AddScoped<FramebufferBridge>();
builder.Services.AddScoped<KeyboardInputBridge>();
builder.Services.AddScoped<IFileSystemAccess, BrowserFileSystemAccess>();
builder.Services.AddScoped<WebRtcLink>();
builder.Services.AddScoped<Koh.Emulator.App.DebugMode.DebugModeBootstrapper>(sp =>
{
    var js = sp.GetRequiredService<IJSRuntime>();
    var host = sp.GetRequiredService<EmulatorHost>();
    return new Koh.Emulator.App.DebugMode.DebugModeBootstrapper(host, path =>
    {
        var task = js.InvokeAsync<string?>("kohVsCodeBridge.requestFile", path);
        var base64 = task.AsTask().GetAwaiter().GetResult();
        if (base64 is null) return ReadOnlyMemory<byte>.Empty;
        return Convert.FromBase64String(base64);
    });
});
builder.Services.AddScoped<Koh.Emulator.App.DebugMode.DapTransport>(sp =>
{
    var js = sp.GetRequiredService<IJSRuntime>();
    var boot = sp.GetRequiredService<Koh.Emulator.App.DebugMode.DebugModeBootstrapper>();
    return new Koh.Emulator.App.DebugMode.DapTransport(js, boot.Dispatcher);
});

// Keyboard wiring now happens in StandaloneShell.OnInitialized (webview scope).
await builder.Build().RunAsync();
