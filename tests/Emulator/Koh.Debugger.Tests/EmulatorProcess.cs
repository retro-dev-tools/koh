using System.Diagnostics;
using Koh.Emulator.App;

namespace Koh.Debugger.Tests;

internal static class EmulatorProcess
{
    public static ProcessStartInfo StartInfo(params string[] arguments)
    {
        string? native = Environment.GetEnvironmentVariable("KOH_TEST_EMULATOR");
        var start = new ProcessStartInfo
        {
            FileName = native ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (native is null)
        {
            string assembly = typeof(EmulatorProcess).Assembly.Location;
            foreach (
                string argument in new[]
                {
                    "exec",
                    "--runtimeconfig",
                    Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                    "--depsfile",
                    Path.ChangeExtension(assembly, ".deps.json"),
                    typeof(EmulatorLoop).Assembly.Location,
                }
            )
                start.ArgumentList.Add(argument);
        }
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        start.Environment["ALSOFT_DRIVERS"] = "unavailable";
        start.Environment["SDL_VIDEODRIVER"] = "unavailable";
        start.Environment["SDL_RENDER_DRIVER"] = "unavailable";
        return start;
    }
}
