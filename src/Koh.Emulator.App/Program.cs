using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Koh.Emulator.App;
using Koh.Emulator.App.Services;
using Koh.Emulator.App.Shell;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<RuntimeModeDetector>();
builder.Services.AddSingleton<FramePacer>();
builder.Services.AddSingleton<EmulatorHost>();

await builder.Build().RunAsync();
