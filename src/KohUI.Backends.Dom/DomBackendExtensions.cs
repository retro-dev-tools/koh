using System.Net.WebSockets;
using System.Reflection;
using System.Text;
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

        // Theme CSS: served dynamically from the Win98Theme record so the
        // palette is a single source of truth shared with SkiaBackend.
        // 98.css's hand-maintained rules still reference var(--win98-*);
        // this endpoint emits the custom-property block on :root.
        var themeCss = BuildThemeCss(activeTheme);
        app.MapGet("/_kohui/theme.css", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/css; charset=utf-8";
            return ctx.Response.WriteAsync(themeCss);
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

    private static string BuildThemeCss(Win98Theme t)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine(":root {");
        sb.Append("    --win98-bg:            ").Append(t.Background.ToHex()).AppendLine(";");
        sb.Append("    --win98-hilite:        ").Append(t.BevelHilite.ToHex()).AppendLine(";");
        sb.Append("    --win98-shadow:        ").Append(t.BevelShadow.ToHex()).AppendLine(";");
        sb.Append("    --win98-dark-shadow:   ").Append(t.BevelDarkShadow.ToHex()).AppendLine(";");
        sb.Append("    --win98-text:          ").Append(t.Text.ToHex()).AppendLine(";");
        sb.Append("    --win98-disabled-text: ").Append(t.DisabledText.ToHex()).AppendLine(";");
        sb.Append("    --win98-title-bg:      ").Append(t.TitleBarStart.ToHex()).AppendLine(";");
        sb.Append("    --win98-title-bg-end:  ").Append(t.TitleBarEnd.ToHex()).AppendLine(";");
        sb.Append("    --win98-title-text:    ").Append(t.TitleBarText.ToHex()).AppendLine(";");
        sb.Append("    --win98-desktop:       ").Append(t.Desktop.ToHex()).AppendLine(";");
        sb.Append("    --win98-ui-font:       ").Append('"').Append(t.UiFontFamily).Append("\", \"Segoe UI\", sans-serif;").AppendLine();
        sb.Append("    --win98-ui-font-size:  ").Append(t.UiFontSize).AppendLine("px;");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
