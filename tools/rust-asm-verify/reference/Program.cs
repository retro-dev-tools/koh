using System.Text.Json;
using Koh.Assembler;
using Koh.Assembler.Binding;
using Koh.Assembler.Syntax;
using Koh.Common;
using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Linker;
using Koh.Objects;

try
{
    if (args.Length == 0)
        throw new ArgumentException("Expected assemble, dump, link, or execute");
    switch (args[0])
    {
        case "corpus" when args.Length == 2:
        {
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[1]))!;
            var results = paths
                .Select(path =>
                {
                    var model = Compilation
                        .Create(SyntaxTree.Parse(SourceText.From(File.ReadAllText(path), path)))
                        .Emit();
                    if (model.Success)
                    {
                        using var output = File.Create(path + ".csharp.kobj");
                        KobjWriter.Write(output, model);
                    }
                    return new
                    {
                        path,
                        success = model.Success,
                        diagnostics = model
                            .Diagnostics.Select(diagnostic => diagnostic.Message)
                            .ToArray(),
                    };
                })
                .ToArray();
            Console.WriteLine(JsonSerializer.Serialize(results));
            break;
        }
        case "assemble" when args.Length == 4:
        {
            var path = Path.GetFullPath(args[2]);
            var model = Compilation
                .Create(
                    new BinderOptions { AllowUndefinedSymbols = args[1] == "rgbds" },
                    SyntaxTree.Parse(SourceText.From(File.ReadAllText(path), path))
                )
                .Emit();
            foreach (var diagnostic in model.Diagnostics)
            {
                var diagnosticPath = string.IsNullOrEmpty(diagnostic.FilePath)
                    ? path
                    : diagnostic.FilePath;
                var source = SourceText.From(File.ReadAllText(diagnosticPath), diagnosticPath);
                var position = Math.Clamp(diagnostic.Span.Start, 0, source.Length);
                var line = source.GetLineIndex(position);
                Console.Error.WriteLine(
                    $"{diagnosticPath}:{line + 1}:{position - source.Lines[line].Start + 1}: "
                        + $"{diagnostic.Severity.ToString().ToLowerInvariant()}: {diagnostic.Message}"
                );
            }
            if (!model.Success)
                return 1;
            using var output = File.Create(args[3]);
            if (args[1] == "rgbds")
                RgbdsObjectWriter.Write(output, model);
            else if (args[1] == "kobj")
                KobjWriter.Write(output, model);
            else
                throw new ArgumentException("Expected kobj or rgbds");
            break;
        }
        case "dump" when args.Length == 2:
        {
            using var input = File.OpenRead(args[1]);
            var model = KobjReader.Read(input);
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        sections = model.Sections.Select(section => new
                        {
                            name = section.Name,
                            kind = (int)section.Type,
                            address = section.FixedAddress,
                            bank = section.Bank,
                            data = Convert.ToHexString(section.Data),
                            patches = section.Patches.Select(patch => new
                            {
                                offset = patch.Offset,
                                kind = (int)patch.Kind,
                                pcAfter = patch.PCAfterInstruction,
                                symbol = patch.SymbolName,
                                addend = patch.SymbolOffset,
                                shift = patch.SymbolShift,
                            }),
                            lines = section.LineMap.Select(line => new
                            {
                                offset = line.Offset,
                                count = line.ByteCount,
                                file = Path.GetFileName(line.File),
                                line = line.Line,
                            }),
                        }),
                        symbols = model.Symbols.Select(symbol => new
                        {
                            name = symbol.Name,
                            kind = (int)symbol.Kind,
                            visibility = (int)symbol.Visibility,
                            section = symbol.Section,
                            value = symbol.Value,
                        }),
                    }
                )
            );
            break;
        }
        case "link" when args.Length >= 3:
        {
            var inputs = args.Skip(2)
                .Select(path =>
                {
                    using var input = File.OpenRead(path);
                    return new LinkerInput(path, KobjReader.Read(input));
                })
                .ToArray();
            var result = new Linker().Link(inputs);
            foreach (var diagnostic in result.Diagnostics)
                Console.Error.WriteLine(diagnostic.Message);
            if (!result.Success || result.RomData == null)
                return 1;
            File.WriteAllBytes(args[1], result.RomData);
            break;
        }
        case "execute" when args.Length == 2:
        {
            var system = new GameBoySystem(
                CartridgeFactory.Load(File.ReadAllBytes(args[1])),
                HardwareMode.Dmg
            );
            system.Registers.Pc = 0x100;
            system.Registers.Sp = 0xFFFE;
            for (var step = 1; step <= 10000; step++)
            {
                system.StepInstruction();
                if (system.DebugReadByte(0xC000) != 42 || system.DebugReadByte(0xC001) != 43)
                    continue;
                Console.WriteLine($"Executed {step} instructions; WRAM C000=2A C001=2B");
                return 0;
            }
            throw new InvalidOperationException(
                $"Execution signature absent after 10000 instructions; PC={system.Registers.Pc:X4}, "
                    + $"C000={system.DebugReadByte(0xC000):X2}, C001={system.DebugReadByte(0xC001):X2}"
            );
        }
        default:
            throw new ArgumentException("Invalid verifier command arguments");
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
