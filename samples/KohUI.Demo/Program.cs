using System.Net;
using KohUI;
using KohUI.Backends.Dom;
using KohUI.Backends.Skia;
using KohUI.Demo;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// Modes:
//   default     Native window via SDL3 + Skia (the actual desktop app).
//   --preview   Dev-only: start the Kestrel-backed DomBackend and wait
//               for a browser to connect on the printed URL. Used by
//               the Playwright E2E test and any coding-agent automation.
//   --headless  Alias for --preview (no browser launched either way
//               since the backend doesn't spawn one).

var preview = args.Contains("--preview") || args.Contains("--headless");

var runner = new Runner<CounterModel, CounterMsg>(
    initialModel: new CounterModel(Count: 0, WindowOpen: true),
    update: CounterApp.Update,
    view: CounterApp.View);

if (preview)
{
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
    builder.Logging.ClearProviders();

    var app = builder.Build();
    app.UseKohUI(runner);

    await app.StartAsync();
    var url = app.Services.GetRequiredService<IServer>()
                 .Features.Get<IServerAddressesFeature>()!
                 .Addresses.First();
    Console.WriteLine($"[kohui-demo] listening on {url}");
    Console.WriteLine("[kohui-demo] open the URL in a browser; ctrl-c to stop.");
    await app.WaitForShutdownAsync();
    return;
}

// Native path.
var backend = new SkiaBackend<CounterModel, CounterMsg>(
    runner,
    title: "KohUI Demo",
    width: 360,
    height: 240);
backend.Run();
await runner.DisposeAsync();
