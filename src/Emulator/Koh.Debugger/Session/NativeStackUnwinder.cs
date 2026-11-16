using System.Globalization;
using Koh.Objects;

namespace Koh.Debugger.Session;

internal static class NativeStackUnwinder
{
    private const string MarkerPrefix = "__koh_unwind_";
    private const string VersionPrefix = "__koh_unwind_v1_";

    internal static bool IsMarker(string name) =>
        name.StartsWith(MarkerPrefix, StringComparison.Ordinal);

    internal static bool HasMetadata(DebugSession session) =>
        session.DebugInfo.SymbolMap.All.Any(s => IsMarker(s.Name));

    internal static (byte Bank, ushort Pc, ushort Sp)? Caller(
        DebugSession session,
        byte bank,
        ushort pc,
        ushort sp
    )
    {
        if (session.System is not { } gb || sp < 0xC100 || sp >= 0xD000)
            return null;
        ushort? recipe = null;
        foreach (
            var symbol in session.DebugInfo.SymbolMap.LookupByAddress(new BankedAddress(bank, pc))
        )
        {
            if (!IsMarker(symbol.Name))
                continue;
            if (
                symbol.Kind != KdbgSymbolKind.Label
                || !symbol.Name.StartsWith(VersionPrefix, StringComparison.Ordinal)
            )
                return null;
            var text = symbol.Name.AsSpan(VersionPrefix.Length);
            if (
                text.Length < 8
                || text[1] != '_'
                || text[6] != '_'
                || text[0] != 'b'
                || !ushort.TryParse(
                    text.Slice(2, 4),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out ushort delta
                )
                || delta > 0xF00
            )
                return null;
            if (recipe is { } prior && prior != delta)
                return null;
            recipe = delta;
        }
        if (recipe is not { } state)
            return null;
        int entry = sp + state;
        int next = entry + 6;
        if (next > 0xD000)
            return null;
        ushort Word(int address) =>
            (ushort)(
                gb.Mmu.DebugRead((ushort)address) | (gb.Mmu.DebugRead((ushort)(address + 1)) << 8)
            );
        var stub = session.DebugInfo.SymbolMap.Lookup("__koh_far_return");
        if (stub is null || stub.Bank != 0 || Word(entry) != stub.Address)
            return null;
        ushort callerPc = Word(entry + 2);
        ushort callerBank = Word(entry + 4);
        if (callerBank > byte.MaxValue)
            return null;
        if (callerPc == 0 || callerPc >= 0x8000 || callerBank * 0x4000 >= gb.Cartridge.Rom.Length)
            return null;
        return (callerPc < 0x4000 ? (byte)0 : (byte)callerBank, callerPc, (ushort)next);
    }
}
