var target = Argument("target", "test");

// ─────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────

void DotNet(string arguments)
{
    var exitCode = StartProcess("dotnet", new ProcessSettings
    {
        Arguments = arguments
    });
    if (exitCode != 0)
        throw new CakeException($"dotnet {arguments} failed with exit code {exitCode}");
}

// ─────────────────────────────────────────────────────────────
// Test
// ─────────────────────────────────────────────────────────────

Task("test")
    .Does(() =>
{
    DotNet("test --project .");
});

Task("compat-tests")
    .Does(() =>
{
    DotNet("test --project tests/Koh.Compat.Tests/");
});

// ─────────────────────────────────────────────────────────────
// Benchmark
// ─────────────────────────────────────────────────────────────

Task("benchmark")
    .Does(() =>
{
    DotNet("run --project benchmarks/Koh.Benchmarks/");
});

// ─────────────────────────────────────────────────────────────
// Publish
// ─────────────────────────────────────────────────────────────

Task("publish-dev")
    .Description("Publish LSP server to editors/vscode/server for F5 debugging")
    .Does(() =>
{
    DotNetPublish("src/Koh.Lsp", new DotNetPublishSettings
    {
        Configuration = "Debug",
        OutputDirectory = "editors/vscode/server"
    });
});

// ─────────────────────────────────────────────────────────────
// Emulator app
// ─────────────────────────────────────────────────────────────

Task("publish-emulator-app")
    .Description("Publish Koh.Emulator.App with AOT and copy into editors/vscode/dist/emulator-app")
    .Does(() =>
{
    var exitCode = StartProcess("npm", new ProcessSettings
    {
        WorkingDirectory = "editors/vscode",
        Arguments = "run build:emulator-app:aot"
    });
    if (exitCode != 0)
        throw new CakeException($"npm run build:emulator-app:aot failed with exit code {exitCode}");
});

// ─────────────────────────────────────────────────────────────

RunTarget(target);
