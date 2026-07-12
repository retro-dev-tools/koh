using System.Text;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

public class Cube3dTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static ushort Run(string main)
    {
        var source = new StringBuilder(main).Append('\n');
        var shared = Path.Combine(Root(), "samples", "gb-3d", "shared");
        if (Directory.Exists(shared))
            foreach (var file in Directory.GetFiles(shared, "*.cs").Order())
                source.Append(File.ReadAllText(file)).Append('\n');

        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(
            SourceText.From(source.ToString(), "cube-test.cs"),
            diagnostics
        );
        var model = new Sm83Backend().Compile(module, diagnostics);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException(
                string.Join("; ", diagnostics.Select(d => d.Message))
            );
        var link = new LinkerType().Link([new LinkerInput("cube-test", model)]);
        if (!link.Success || link.RomData is null)
            throw new InvalidOperationException(
                string.Join("; ", link.Diagnostics.Select(d => d.Message))
            );
        var rom = link.RomData;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Pc = 0x100;
        gb.Registers.Sp = 0xFFFE;
        var pixelsSymbol = link.Symbols.Single(s => s.Name == "Surface.pixels");
        var doneSymbol = link.Symbols.Single(s => s.Name == "Surface.done");
        bool finished = false;
        for (int i = 0; i < 100_000_000; i++)
        {
            if (gb.DebugReadByte((ushort)doneSymbol.AbsoluteAddress) != 0)
            {
                finished = true;
                break;
            }
            gb.StepInstruction();
        }
        if (!finished)
            throw new InvalidOperationException(
                "cube renderer did not finish within instruction budget"
            );
        ushort count = 0;
        for (ushort i = 0; i < 768; i++)
            if (gb.DebugReadByte((ushort)(pixelsSymbol.AbsoluteAddress + i)) != 0)
                count++;
        return count;
    }

    [Test]
    public async Task CubeRenderer_DrawsFilledPixelsAtPhaseZero()
    {
        const string main =
            "static byte Main() { Surface.Clear(); CubeRenderer.Render(24); Surface.MarkDone(); return 0; } "
            + "static class Surface { static byte[] pixels=new byte[768]; static byte done; "
            + "public static void Clear(){ for(ushort i=0;i<768;i++)pixels[i]=0; done=0; } public static void MarkDone(){done=1;} "
            + "public static void SetPixel(byte x, byte y, byte c){ if(x<96 && y<64 && c!=0) pixels[(ushort)y*12+(x>>3)]|=(byte)(0x80>>(x&7)); } "
            + "public static byte Width(){ return 96; } public static byte Height(){ return 64; } "
            + "}";

        ushort count = Run(main);
        await Assert.That(count).IsGreaterThan((ushort)20);
        await Assert.That(count).IsLessThan((ushort)768);
    }
}
