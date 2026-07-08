// Builds the Koh C# 2048 sample into a Game Boy ROM.
//
// It drives the compiler platform exactly the way the toolchain does: pick a frontend by file
// extension and a backend by name from CompilerRegistry, run CompilerDriver to get an EmitModel,
// then hand that to Koh.Linker.Core to produce the final .gb image - the same path assembler
// output takes.
//
// Run:  dotnet run --project samples/gb-2048-cs/build -- [<source.cs>] [<out.gb>]
using Koh.Compiler;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Linker.Core;

string sourcePath =
    args.Length > 0
        ? args[0]
        : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "2048.cs");
sourcePath = Path.GetFullPath(sourcePath);

string outPath =
    args.Length > 1
        ? args[1]
        : Path.Combine(Path.GetDirectoryName(sourcePath)!, "build", "2048.gb");

var frontend = CompilerRegistry.FrontendForExtension(Path.GetExtension(sourcePath));
if (frontend is null)
{
    Console.Error.WriteLine($"No frontend registered for '{Path.GetExtension(sourcePath)}'.");
    return 1;
}

var backend = CompilerRegistry.BackendByName("sm83")!;
var source = SourceText.From(File.ReadAllText(sourcePath), sourcePath);
var diagnostics = new DiagnosticBag();

var model = CompilerDriver.Compile(frontend, backend, source, diagnostics);

bool hadError = false;
foreach (var d in diagnostics)
{
    Console.Error.WriteLine($"{d.Severity}: {d.Message}  (offset {d.Span.Start})");
    hadError |= d.Severity == DiagnosticSeverity.Error;
}
if (hadError)
{
    Console.Error.WriteLine("Compilation failed.");
    return 1;
}

var link = new Linker().Link([new LinkerInput("2048", model)]);
var rom = link.RomData;
if (rom is null)
{
    Console.Error.WriteLine("Linker produced no ROM.");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllBytes(outPath, rom);
Console.WriteLine($"Built {outPath} ({rom.Length} bytes).");
return 0;
