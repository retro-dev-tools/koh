using KohUI;
using KohUI.Backends.Gl;
using KohUI.Demo;
#if KOHUI_DEV_PREVIEW
using System.Net;
using KohUI.Backends.Dom;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
#endif

// Modes (Debug builds):
//   default     Native SDL window PLUS DomBackend dev preview on a
//               localhost port. One Runner feeds both surfaces so a
//               click in either updates the other.
//   --preview   Preview only — no SDL. Used by CI and Playwright so
//               headless runners don't need a display server.
//   --native    Native only — no localhost port even in Debug.
//
// Release builds: native path is the only path. Preview is compiled
// out entirely (no Kestrel, no ASP.NET Core, no localhost exposure,
// ~12 MB smaller binary). Override with /p:KohUIDevPreview=true if a
// Release-built diagnostic binary needs the preview channel.

bool previewOnly = args.Contains("--preview") || args.Contains("--headless");
bool nativeOnly  = args.Contains("--native");

var runner = new Runner<CounterModel, CounterMsg>(
    initialModel: new CounterModel(Count: 0, Step: 1, AllowNegative: true, WindowOpen: true),
    update: CounterApp.Update,
    view: CounterApp.View);

#if !KOHUI_DEV_PREVIEW
if (previewOnly)
{
    await Console.Error.WriteLineAsync("[kohui-demo] --preview isn't available in this build "
                                     + "(KohUIDevPreview=false at compile time).");
    return 1;
}
// Release / preview-stripped build — native is the only code path.
RunNative(runner);
await runner.DisposeAsync();
return 0;
#else

if (previewOnly)
{
    await RunPreviewOnlyAsync(runner);
    return 0;
}

if (nativeOnly)
{
    RunNative(runner);
    await runner.DisposeAsync();
    return 0;
}

// Both: start Kestrel on a background thread, launch the SDL window on
// the main thread, shut Kestrel down when the window closes.
var previewApp = await StartPreviewAsync(runner);
try
{
    RunNative(runner);
}
finally
{
    await previewApp.StopAsync();
    await previewApp.DisposeAsync();
}
await runner.DisposeAsync();
return 0;

static async Task<WebApplication> StartPreviewAsync(Runner<CounterModel, CounterMsg> runner)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
    builder.Logging.ClearProviders();

    var app = builder.Build();
    app.UseKohUI(runner);
    await app.StartAsync();

    var url = app.Services.GetRequiredService<IServer>()
                 .Features.Get<IServerAddressesFeature>()!
                 .Addresses.First();
    Console.WriteLine($"[kohui-demo] preview listening on {url}");
    return app;
}

static async Task RunPreviewOnlyAsync(Runner<CounterModel, CounterMsg> runner)
{
    var app = await StartPreviewAsync(runner);
    Console.WriteLine("[kohui-demo] preview-only mode; open the URL in a browser. ctrl-c to stop.");
    await app.WaitForShutdownAsync();
}

#endif

static void RunNative(Runner<CounterModel, CounterMsg> runner)
{
    // No width/height — GlBackend measures the root view and opens the
    // GLFW window at content-size. Override only when the demo wants a
    // specific layout box regardless of content.
    var backend = new GlBackend<CounterModel, CounterMsg>(runner, title: "KohUI Demo");
    backend.Run();
}
