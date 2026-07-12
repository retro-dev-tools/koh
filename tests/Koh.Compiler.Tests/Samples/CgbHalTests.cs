using System.Text;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Core.Diagnostics;
using Koh.Core.Text;

namespace Koh.Compiler.Tests.Samples;

public class CgbHalTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("could not locate repository root");
    }

    [Test]
    public async Task CgbHal_CompilesThroughTheRealBackend()
    {
        var hal = Path.Combine(RepositoryRoot(), "src", "Koh.GameBoy", "Hal");
        var source = new StringBuilder(
            "static byte Main() { "
                + "bool color = Cgb.IsColor(); "
                + "Cgb.SelectVramBank(1); "
                + "Cgb.SetBackgroundColor(0, 0, 0x1234); "
                + "Cgb.TryEnableDoubleSpeed(); "
                + "Ppu.WaitForVramAccess(); Ppu.WaitForHBlank(); "
                + "return (byte)(color ? 1 : 0); }\n"
        );
        foreach (var file in Directory.GetFiles(hal, "*.cs").Order())
            source.Append(File.ReadAllText(file)).Append('\n');

        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(
            SourceText.From(source.ToString(), "cgb-hal.cs"),
            diagnostics
        );
        new Sm83Backend().Compile(module, diagnostics);

        var errors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Message)
            .ToArray();
        await Assert.That(errors).IsEmpty();
    }
}
