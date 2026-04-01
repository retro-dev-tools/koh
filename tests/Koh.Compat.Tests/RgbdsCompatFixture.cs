using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Emit;

namespace Koh.Compat.Tests;

/// <summary>
/// Helpers for RGBDS compatibility tests. Spins up a lightweight container
/// with RGBDS tools and executes rgblink/rgbasm inside it via Testcontainers.
/// </summary>
internal static class RgbdsCompatFixture
{
    private static IFutureDockerImage? _image;
    private static IContainer? _container;

    public static bool IsAvailable => _container != null;

    public static async Task StartAsync()
    {
        if (_container != null)
            return;

        var dockerfileDir = Path.GetDirectoryName(typeof(RgbdsCompatFixture).Assembly.Location)!;

        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(dockerfileDir)
            .WithDockerfile("Dockerfile.rgbds")
            .WithName("koh-rgbds-compat")
            .Build();

        await _image.CreateAsync();

        _container = new ContainerBuilder()
            .WithImage(_image)
            .WithEntrypoint("tail", "-f", "/dev/null")
            .Build();

        await _container.StartAsync();
    }

    public static async Task StopAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
            _container = null;
        }

        if (_image != null)
        {
            await _image.DisposeAsync();
            _image = null;
        }
    }

    public static EmitModel Assemble(string source, BinderOptions? options = null)
    {
        var tree = SyntaxTree.Parse(source);
        return options.HasValue
            ? Compilation.Create(options.Value, tree).Emit()
            : Compilation.Create(tree).Emit();
    }

    public static byte[] WriteObjectFile(EmitModel model)
    {
        using var stream = new MemoryStream();
        RgbdsObjectWriter.Write(stream, model);
        return stream.ToArray();
    }

    public static async Task<byte[]?> RgbasmAssembleAsync(string source, string containerDir, string name)
    {
        var container = _container ?? throw new InvalidOperationException("Container not started");

        var asmPath = $"{containerDir}/{name}.asm";
        var objPath = $"{containerDir}/{name}.o";

        await container.CopyAsync(System.Text.Encoding.UTF8.GetBytes(source), asmPath);

        var result = await container.ExecAsync(["rgbasm", "-o", objPath, asmPath]);

        if (result.ExitCode != 0)
            return null;

        try
        {
            return await container.ReadFileAsync(objPath);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<LinkResult> LinkAsync(
        string containerDir,
        string outputName,
        params (string Name, byte[] Data)[] objectFiles)
    {
        var container = _container ?? throw new InvalidOperationException("Container not started");

        await container.ExecAsync(["mkdir", "-p", containerDir]);

        var objPaths = new string[objectFiles.Length];
        for (var i = 0; i < objectFiles.Length; i++)
        {
            var (name, data) = objectFiles[i];
            var objPath = $"{containerDir}/{name}";
            await container.CopyAsync(data, objPath);
            objPaths[i] = objPath;
        }

        var outputPath = $"{containerDir}/{outputName}";

        var args = new List<string> { "rgblink", "-o", outputPath };
        args.AddRange(objPaths);

        var result = await container.ExecAsync(args);

        Console.WriteLine($"rgblink exit: {result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            Console.WriteLine($"rgblink stderr: {result.Stderr}");

        byte[]? romData = null;
        if (result.ExitCode == 0)
        {
            try
            {
                romData = await container.ReadFileAsync(outputPath);
            }
            catch
            {
                // File may not exist if linking produced no output
            }
        }

        return new LinkResult((int)result.ExitCode, result.Stdout, result.Stderr, romData);
    }
}

internal sealed record LinkResult(int ExitCode, string Stdout, string Stderr, byte[]? RomData);
