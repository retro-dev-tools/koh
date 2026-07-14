using Koh.Compiler.Frontends.Cil;
using Mono.Cecil;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Builds <see cref="CilIntrinsicIndex"/> over the real <c>Koh.GameBoy</c> assembly and asserts
/// concrete entries — the same assembly the CIL frontend lowers a game against, so this pins the
/// index against production metadata rather than a hand-rolled fixture.
/// </summary>
public class CilIntrinsicIndexTests
{
    private static (
        ModuleDefinition Module,
        IReadOnlyDictionary<MethodDefinition, CilIntrinsicIndex.Entry> Index
    ) BuildIndex()
    {
        var location = typeof(Koh.GameBoy.Hardware).Assembly.Location;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(location)!);
        var module = ModuleDefinition.ReadModule(
            location,
            new ReaderParameters { AssemblyResolver = resolver }
        );
        return (module, CilIntrinsicIndex.Build(module));
    }

    [Test]
    public async Task LcdcSetter_MapsToRegisterAddress()
    {
        var (module, index) = BuildIndex();
        var lcdc = module.GetType("Koh.GameBoy.Hardware").Properties.Single(p => p.Name == "LCDC");
        var entry = index[lcdc.SetMethod!];
        await Assert.That(entry.Kind).IsEqualTo("register");
        await Assert.That(entry.Address).IsEqualTo(0xFF40);
    }

    [Test]
    public async Task LcdcGetter_IsAlsoIndexed()
    {
        var (module, index) = BuildIndex();
        var lcdc = module.GetType("Koh.GameBoy.Hardware").Properties.Single(p => p.Name == "LCDC");
        var entry = index[lcdc.GetMethod!];
        await Assert.That(entry.Kind).IsEqualTo("register");
        await Assert.That(entry.Address).IsEqualTo(0xFF40);
    }

    [Test]
    public async Task GbVramGetter_MapsToRegionAddress()
    {
        var (module, index) = BuildIndex();
        var vram = module.GetType("Koh.GameBoy.Gb").Properties.Single(p => p.Name == "Vram");
        var entry = index[vram.GetMethod!];
        await Assert.That(entry.Kind).IsEqualTo("region");
        await Assert.That(entry.Address).IsEqualTo(0x8000);
    }

    [Test]
    public async Task HardwareHalt_MapsToAddresslessControlIntrinsic()
    {
        var (module, index) = BuildIndex();
        var halt = module.GetType("Koh.GameBoy.Hardware").Methods.Single(m => m.Name == "Halt");
        var entry = index[halt];
        await Assert.That(entry.Kind).IsEqualTo("halt");
        await Assert.That(entry.Address).IsEqualTo(-1);
    }
}
