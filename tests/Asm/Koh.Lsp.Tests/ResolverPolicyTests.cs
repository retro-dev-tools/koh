using Koh.Common;
using Koh.Lsp.Config;
using Koh.Lsp.Projects;
using Koh.Native;
using Koh.Native.Syntax;

namespace Koh.Lsp.Tests;

public class ResolverPolicyTests
{
    [Test]
    public async Task EntrypointReadfileUsesRootPolicyAndByteLimit()
    {
        foreach (var virtualFiles in new[] { false, true })
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"koh-readfile-policy-{Guid.NewGuid():N}"
            );
            var root = Path.Combine(directory, "src");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            try
            {
                var main = Path.Combine(root, "main.asm");
                var files = new Dictionary<string, string>
                {
                    [main] = "SECTION \"main\",ROM0\nINCLUDE \"sub/body.inc\"",
                    [Path.Combine(root, "sub", "body.inc")] = "db READFILE(\"note.txt\",2)",
                    [Path.Combine(root, "note.txt")] = "ABtail",
                    [Path.Combine(root, "sub", "note.txt")] = "ZZ",
                };
                var memory = new VirtualFileResolver();
                foreach (var (path, text) in files)
                {
                    if (virtualFiles)
                        memory.AddTextFile(path, text);
                    else
                        File.WriteAllText(path, text);
                }
                var manager = new ProjectContextManager(
                    directory,
                    virtualFiles ? memory : new FileSystemResolver()
                );
                manager.ApplyConfig(
                    new KohConfigLoadResult.Configured([
                        new KohProjectDefinition { Name = "main", Entrypoint = main },
                    ])
                );
                var model = manager.AllProjects.Single().Compilation.Emit();
                await Assert.That(model.Success).IsTrue();
                await Assert
                    .That(model.Sections.Single().Data)
                    .IsEquivalentTo(new byte[] { 65, 66 });
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed class RootResolver(ISourceFileResolver inner, string root) : ISourceFileResolver
    {
        public bool FileExists(string path) => inner.FileExists(path);

        public string ReadAllText(string path) => inner.ReadAllText(path);

        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

        public string ResolvePath(string currentFile, string includedPath) =>
            Path.GetFullPath(Path.Combine(root, includedPath));

        public IReadOnlyDictionary<string, string> GetTextOverlays() => inner.GetTextOverlays();

        public IReadOnlyDictionary<string, byte[]> GetBinaryOverlays() => inner.GetBinaryOverlays();
    }

    [Test]
    public async Task CustomAndEntrypointResolversControlNestedPhysicalAndVirtualIncludes()
    {
        foreach (var virtualFiles in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), $"koh-resolver-{Guid.NewGuid():N}");
            var root = Path.Combine(directory, "src");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            try
            {
                var main = Path.Combine(root, "main.asm");
                var body = Path.Combine(root, "sub", "body.inc");
                var constants = Path.Combine(root, "constants.inc");
                const string mainText = "SECTION \"main\",ROM0\nINCLUDE \"sub/body.inc\"";
                const string bodyText =
                    "INCLUDE \"constants.inc\"\nINCBIN \"asset.bin\"\nld a,VALUE";
                var files = new Dictionary<string, string>
                {
                    [main] = mainText,
                    [body] = bodyText,
                    [constants] = "DEF VALUE EQU 42",
                    [Path.Combine(root, "sub", "constants.inc")] = "DEF VALUE EQU 99",
                };
                var binary = new Dictionary<string, byte[]>
                {
                    [Path.Combine(root, "asset.bin")] = [7, 8],
                    [Path.Combine(root, "sub", "asset.bin")] = [9, 9],
                };
                var memory = new VirtualFileResolver();
                foreach (var (path, text) in files)
                {
                    if (virtualFiles)
                        memory.AddTextFile(path, text);
                    else
                        File.WriteAllText(path, text);
                }
                foreach (var (path, bytes) in binary)
                {
                    if (virtualFiles)
                        memory.AddBinaryFile(path, bytes);
                    else
                        File.WriteAllBytes(path, bytes);
                }
                ISourceFileResolver inner = virtualFiles ? memory : new FileSystemResolver();
                var tree = SyntaxTree.Parse(SourceText.From(mainText, main));
                var direct = Compilation.Create(new RootResolver(inner, root), tree);
                await Assert.That(direct.Emit().Success).IsTrue();
                await Assert
                    .That(direct.Emit().Sections.Single().Data)
                    .IsEquivalentTo(new byte[] { 7, 8, 0x3e, 42 });
                await Assert
                    .That(
                        direct.GetSemanticModel(tree).ResolveSymbol("VALUE", 0)!.DefinitionFilePath
                    )
                    .IsEqualTo(constants);
                var manager = new ProjectContextManager(directory, inner);
                manager.ApplyConfig(
                    new KohConfigLoadResult.Configured([
                        new KohProjectDefinition { Name = "main", Entrypoint = main },
                    ])
                );
                manager.UpdateDocumentText(body, bodyText + "\n");
                var project = manager.AllProjects.Single();
                await Assert.That(project.Compilation.Emit().Success).IsTrue();
                await Assert
                    .That(project.Compilation.Emit().Sections.Single().Data)
                    .IsEquivalentTo(new byte[] { 7, 8, 0x3e, 42 });
                await Assert.That(project.ReachableFiles.Contains(constants)).IsTrue();
                await Assert
                    .That(
                        project
                            .Compilation.GetSemanticModel(project.Compilation.SyntaxTrees[0])
                            .ResolveSymbol("VALUE", 0)!
                            .DefinitionFilePath
                    )
                    .IsEqualTo(constants);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
