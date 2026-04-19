using System.Net.WebSockets;
using System.Reflection;
using KohUI;
using KohUI.Theme;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace KohUI.Backends.Dom;

/// <summary>
/// Minimal-API glue that turns a <see cref="Runner{TModel, TMsg}"/>
/// into a running web preview in ~4 lines of consumer code:
///
/// <code>
/// var runner = new Runner&lt;Model, Msg&gt;(initial, update, view);
/// var builder = WebApplication.CreateSlimBuilder();
/// var app = builder.Build();
/// app.UseKohUI(runner);
/// await app.RunAsync();
/// </code>
///
/// KohUI's bundled assets (the HTML shell, 98.css, the JS patch
/// applier) are embedded in this assembly and served at <c>/_kohui/</c>.
/// The WebSocket endpoint is <c>/_kohui/ws</c>. The root route <c>/</c>
/// serves the bundled <c>index.html</c>.
/// </summary>
public static class DomBackendExtensions
{
    private static readonly EmbeddedFileProvider BundledAssets =
        new(typeof(DomBackendExtensions).Assembly, "KohUI.Backends.Dom.wwwroot");

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void UseKohUI<TModel, TMsg>(
        this WebApplication app,
        Runner<TModel, TMsg> runner,
        Win98Theme? theme = null)
    {
        var activeTheme = theme ?? Win98Theme.Default;
        var backend = new DomBackend<TModel, TMsg>(runner);

        app.UseWebSockets();

        // The full stylesheet is generated from the theme + WidgetSpecs —
        // no hand-written CSS on disk. Both backends consume the same
        // spec table, so a change to a padding or bevel width here
        // lands in Skia and DOM simultaneously.
        var generatedCss = CssGenerator.Build(activeTheme);
        app.MapGet("/_kohui/kohui.css", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/css; charset=utf-8";
            return ctx.Response.WriteAsync(generatedCss);
        });

        // Serve bundled KohUI static assets from the embedded manifest.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = BundledAssets,
            RequestPath = "/_kohui",
            ContentTypeProvider = ContentTypes,
        });

        // Default route: ship the bundled bootstrap page.
        app.MapGet("/", async ctx =>
        {
            var indexFile = BundledAssets.GetFileInfo("index.html");
            if (!indexFile.Exists)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("index.html missing from embedded assets");
                return;
            }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await using var stream = indexFile.CreateReadStream();
            await stream.CopyToAsync(ctx.Response.Body);
        });

        app.Map("/_kohui/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await backend.HandleAsync(socket, ctx.RequestAborted);
        });
    }

}
