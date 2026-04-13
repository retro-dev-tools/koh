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

builder.Services.AddSingleton<RuntimeModeDetector>();
builder.Services.AddSingleton<FramePacer>();
builder.Services.AddSingleton<EmulatorHost>();
builder.Services.AddSingleton<FramebufferBridge>();
builder.Services.AddSingleton<Koh.Emulator.App.DebugMode.DebugModeBootstrapper>(sp =>
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
builder.Services.AddSingleton<Koh.Emulator.App.DebugMode.DapTransport>(sp =>
{
    var js = sp.GetRequiredService<IJSRuntime>();
    var boot = sp.GetRequiredService<Koh.Emulator.App.DebugMode.DebugModeBootstrapper>();
    return new Koh.Emulator.App.DebugMode.DapTransport(js, boot.Dispatcher);
});

await builder.Build().RunAsync();
