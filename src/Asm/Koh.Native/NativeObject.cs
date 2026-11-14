using System.Text.Json;
using Koh.Common;
using Koh.Objects;

namespace Koh.Native;

internal static class NativeObject
{
    internal static EmitModel Read(
        JsonElement value,
        byte[] elf,
        IReadOnlyList<Diagnostic> diagnostics
    )
    {
        var nativeSections = value.GetProperty("sections").EnumerateArray().ToArray();
        var sections = nativeSections
            .Select(section => new SectionData(
                section.GetProperty("name").GetString()!,
                (SectionType)section.GetProperty("kind").GetByte(),
                OptionalInt(section, "address"),
                OptionalInt(section, "bank"),
                section.GetProperty("kind").GetByte() <= 1
                    ? section
                        .GetProperty("data")
                        .EnumerateArray()
                        .Select(b => b.GetByte())
                        .ToArray()
                    : new byte[section.GetProperty("size").GetInt32()],
                section
                    .GetProperty("lines")
                    .EnumerateArray()
                    .Select(line => new LineMapEntry(
                        line.GetProperty("offset").GetInt32(),
                        line.GetProperty("count").GetInt32(),
                        line.GetProperty("file").GetString()!,
                        line.GetProperty("line").GetUInt32()
                    ))
                    .ToArray()
            ))
            .ToArray();
        var symbols = value
            .GetProperty("symbols")
            .EnumerateArray()
            .Select(symbol =>
            {
                var definition = symbol.GetProperty("definition");
                var imported = definition.ValueKind == JsonValueKind.String;
                var label = !imported && definition.TryGetProperty("Label", out _);
                string? section = null;
                long address = 0;
                if (label)
                {
                    var data = definition.GetProperty("Label");
                    section = sections[data.GetProperty("section").GetInt32()].Name;
                    address = data.GetProperty("offset").GetUInt32();
                }
                else if (!imported)
                    address = definition.GetProperty("Absolute").GetInt32();
                return new SymbolData(
                    symbol.GetProperty("name").GetString()!,
                    label ? SymbolKind.Label : SymbolKind.Constant,
                    imported ? SymbolVisibility.Imported
                        : symbol.GetProperty("binding").GetString() == "Exported"
                            ? SymbolVisibility.Exported
                        : SymbolVisibility.Local,
                    section,
                    address
                );
            })
            .ToArray();
        return new EmitModel(sections, symbols, diagnostics) { ElfImage = elf };
    }

    private static int? OptionalInt(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null
            ? null
            : value.GetProperty(name).GetInt32();

    internal static string Serialize(EmitModel model)
    {
        if (!model.Success)
            throw new InvalidDataException("Cannot emit an object with compilation errors");
        var indices = model
            .Sections.Select((section, index) => (section.Name, index))
            .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.Ordinal);
        var sections = model.Sections.Select(section => new
        {
            name = section.Name,
            kind = (byte)section.Type,
            combination = "Normal",
            address = section.FixedAddress,
            bank = section.Bank,
            align = 0,
            align_offset = 0,
            size = section.Data.Length,
            data = section.Type is SectionType.Rom0 or SectionType.RomX
                ? section.Data.Select(b => (int)b).ToArray()
                : [],
            patches = Array.Empty<object>(),
            lines = section.LineMap.Select(line => new
            {
                offset = line.Offset,
                count = line.ByteCount,
                file = line.File,
                line = line.Line,
            }),
            source = (object?)null,
        });
        var symbols = model.Symbols.Select(symbol => new
        {
            name = symbol.Name,
            binding = symbol.Visibility == SymbolVisibility.Exported ? "Exported" : "Local",
            definition = Definition(symbol, indices),
            source = (object?)null,
        });
        return JsonSerializer.Serialize(new { sections, symbols });
    }

    private static object Definition(SymbolData symbol, Dictionary<string, int> indices)
    {
        if (symbol.Visibility == SymbolVisibility.Imported)
            return "Import";
        if (symbol.Kind == SymbolKind.Label && symbol.Section is not null)
            return new
            {
                Label = new
                {
                    section = indices[symbol.Section],
                    offset = checked((uint)symbol.Value),
                },
            };
        if (symbol.Kind is not (SymbolKind.Constant or SymbolKind.Label))
            throw new InvalidDataException(
                $"Unsupported compiler symbol '{symbol.Name}' ({symbol.Kind})"
            );
        return new { Absolute = checked((int)symbol.Value) };
    }
}
