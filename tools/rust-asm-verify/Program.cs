using System.Text.Json;
using Koh.Common;
using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Linker;
using Koh.Objects;

try
{
    if (args.Length == 0)
        throw new ArgumentException("Expected dump, link, or execute");
    switch (args[0])
    {
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
