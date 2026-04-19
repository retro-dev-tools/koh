using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace KohUI.Tests.E2E;

/// <summary>
/// Drives the actual compiled <c>KohUI.Demo</c> binary with Playwright.
/// Proves the full round trip: C# runner → reconciler → WebSocket patches
/// → DOM → click event → WebSocket back → dispatch → new render.
///
/// <para>
/// One-time setup on a fresh machine:
/// <code>
///   dotnet build tests/KohUI.Tests.E2E
///   pwsh tests/KohUI.Tests.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
/// </code>
/// CI installs Playwright browsers in a dedicated step.
/// </para>
/// </summary>
public class CounterDemoE2ETests
{
    private static readonly Regex s_portRegex = new(@"http://127\.0\.0\.1:(\d+)", RegexOptions.Compiled);

    [Test]
    public async Task Counter_Click_Increments_And_Close_Hides_Window()
    {
        using var demo = await StartDemoAsync();
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // Surface page errors so a failed assertion has enough context to
        // diagnose instead of a bare locator timeout.
        page.PageError += (_, m) => Console.Error.WriteLine($"[page-error] {m}");

        await page.GotoAsync(demo.Url);

        // Initial render: Count: 0 visible inside a Window.
        await page.Locator(".kohui-window .title-bar-text").WaitForAsync();
        await Assert.That((await page.Locator(".kohui-window .title-bar-text").TextContentAsync()) ?? "")
            .IsEqualTo("Koh Counter");
        await Assert.That((await page.Locator(".kohui-label").First.TextContentAsync()) ?? "")
            .IsEqualTo("Count: 0");

        // Click + twice → Count: 2. Button labels are "-" and "+".
        var incrementBtn = page.Locator("button", new() { HasTextString = "+" });
        await incrementBtn.ClickAsync();
        await incrementBtn.ClickAsync();
        await page.Locator(".kohui-label", new() { HasTextString = "Count: 2" }).WaitForAsync();

        // Status bar should reflect the same count.
        await Assert.That((await page.Locator(".status-bar-field").Nth(1).TextContentAsync()) ?? "")
            .IsEqualTo("Value: 2");

        // Click the window close button. Window disappears; a "Reopen"
        // UI takes its place.
        await page.Locator(".title-bar-controls button[aria-label='Close']").ClickAsync();
        await page.Locator("button", new() { HasTextString = "Reopen" }).WaitForAsync();
        await Assert.That(await page.Locator(".kohui-window").CountAsync()).IsEqualTo(0);

        // Reopen resets the count to 0.
        await page.Locator("button", new() { HasTextString = "Reopen" }).ClickAsync();
        await page.Locator(".kohui-label", new() { HasTextString = "Count: 0" }).WaitForAsync();
    }

    // ─── Subprocess plumbing ─────────────────────────────────────────

    private static async Task<DemoHandle> StartDemoAsync()
    {
        var repoRoot = FindRepoRoot();
        var demoDir = Path.Combine(repoRoot, "samples", "KohUI.Demo");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // --preview → run with DomBackend only (no SDL window). The E2E
            // test drives the DOM preview explicitly; the SkiaBackend path
            // is the default production shape but can't be steered by
            // Playwright.
            Arguments = "run --project \"" + demoDir + "\" --no-build --configuration Debug -- --preview",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("dotnet run failed to start");

        var urlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var m = s_portRegex.Match(e.Data);
            if (m.Success && !urlTcs.Task.IsCompleted)
                urlTcs.TrySetResult(m.Value);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var url = await Task.WhenAny(urlTcs.Task, Task.Delay(TimeSpan.FromSeconds(30))) switch
        {
            var t when t == urlTcs.Task => await urlTcs.Task,
            _ => throw new TimeoutException("demo didn't print its listen URL within 30s"),
        };

        return new DemoHandle(proc, url);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("couldn't locate repo root from " + AppContext.BaseDirectory);
    }

    private sealed class DemoHandle : IDisposable
    {
        public Process Process { get; }
        public string Url { get; }
        public DemoHandle(Process p, string url) { Process = p; Url = url; }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch { /* best-effort teardown */ }
            Process.Dispose();
        }
    }
}
