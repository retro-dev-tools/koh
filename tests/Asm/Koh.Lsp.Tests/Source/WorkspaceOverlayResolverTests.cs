using Koh.Core;
using Koh.Lsp.Source;

namespace Koh.Lsp.Tests.Source;

public class WorkspaceOverlayResolverTests
{
    private static WorkspaceOverlayResolver CreateResolver(VirtualFileResolver? inner = null)
    {
        return new WorkspaceOverlayResolver(inner ?? new VirtualFileResolver());
    }

    [Test]
    public async Task FileExists_ReturnsTrueForOverlayPath()
    {
        var resolver = CreateResolver();
        resolver.SetOverlayText("C:/project/main.asm", "nop");

        await Assert.That(resolver.FileExists("C:/project/main.asm")).IsTrue();
    }

    [Test]
    public async Task FileExists_ReturnsTrueForDiskFile()
    {
        var inner = new VirtualFileResolver();
        inner.AddTextFile("C:/project/disk.asm", "halt");
        var resolver = CreateResolver(inner);

        await Assert.That(resolver.FileExists("C:/project/disk.asm")).IsTrue();
    }

    [Test]
    public async Task FileExists_ReturnsFalseForMissingFile()
    {
        var resolver = CreateResolver();

        await Assert.That(resolver.FileExists("C:/project/missing.asm")).IsFalse();
    }

    [Test]
    public async Task ReadAllText_ReturnsOverlayTextWhenAvailable()
    {
        var inner = new VirtualFileResolver();
        inner.AddTextFile("C:/project/main.asm", "disk content");
        var resolver = CreateResolver(inner);
        resolver.SetOverlayText("C:/project/main.asm", "unsaved content");

        var text = resolver.ReadAllText("C:/project/main.asm");

        await Assert.That(text).IsEqualTo("unsaved content");
    }

    [Test]
    public async Task ReadAllText_FallsBackToDiskWhenNoOverlay()
    {
        var inner = new VirtualFileResolver();
        inner.AddTextFile("C:/project/main.asm", "disk content");
        var resolver = CreateResolver(inner);

        var text = resolver.ReadAllText("C:/project/main.asm");

        await Assert.That(text).IsEqualTo("disk content");
    }

    [Test]
    public async Task ReadAllText_FallsBackToDiskAfterOverlayRemoved()
    {
        var inner = new VirtualFileResolver();
        inner.AddTextFile("C:/project/main.asm", "disk content");
        var resolver = CreateResolver(inner);

        resolver.SetOverlayText("C:/project/main.asm", "unsaved content");
        resolver.RemoveOverlay("C:/project/main.asm");

        var text = resolver.ReadAllText("C:/project/main.asm");

        await Assert.That(text).IsEqualTo("disk content");
    }

    [Test]
    public async Task ReadAllBytes_AlwaysDelegatesToDisk()
    {
        var inner = new VirtualFileResolver();
        var diskBytes = new byte[] { 0x01, 0x02, 0x03 };
        inner.AddBinaryFile("C:/project/data.bin", diskBytes);
        var resolver = CreateResolver(inner);

        // Set overlay text — ReadAllBytes should still return disk bytes
        resolver.SetOverlayText("C:/project/data.bin", "overlay text");

        var bytes = resolver.ReadAllBytes("C:/project/data.bin");

        await Assert.That(bytes).IsEqualTo(diskBytes);
    }

    [Test]
    public async Task ResolvePath_DelegatesToInnerResolver()
    {
        var inner = new VirtualFileResolver();
        var resolver = CreateResolver(inner);

        // VirtualFileResolver.ResolvePath returns the included path as-is
        var resolved = resolver.ResolvePath("C:/project/main.asm", "utils.asm");

        await Assert.That(resolved).IsEqualTo("utils.asm");
    }

    [Test]
    public async Task FileExists_IsCaseInsensitive()
    {
        var resolver = CreateResolver();
        resolver.SetOverlayText("C:/project/Main.ASM", "nop");

        await Assert.That(resolver.FileExists("C:/project/main.asm")).IsTrue();
    }

    [Test]
    public async Task SetOverlayText_UpdatesExistingOverlay()
    {
        var resolver = CreateResolver();
        resolver.SetOverlayText("C:/project/main.asm", "version 1");
        resolver.SetOverlayText("C:/project/main.asm", "version 2");

        var text = resolver.ReadAllText("C:/project/main.asm");

        await Assert.That(text).IsEqualTo("version 2");
    }
}
