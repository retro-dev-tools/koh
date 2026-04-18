using System.Net;
using KohUI;
using KohUI.Backends.Dom;
using KohUI.Demo;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
builder.Logging.ClearProviders();

var runner = new Runner<CounterModel, CounterMsg>(
    initialModel: new CounterModel(Count: 0),
    update: CounterApp.Update,
    view: CounterApp.View);

var app = builder.Build();
app.UseKohUI(runner);

await app.StartAsync();
var url = app.Services.GetRequiredService<IServer>()
             .Features.Get<IServerAddressesFeature>()!
             .Addresses.First();
Console.WriteLine($"[kohui-demo] listening on {url}");
Console.WriteLine("[kohui-demo] open the URL in a browser; ctrl-c to stop.");
await app.WaitForShutdownAsync();
