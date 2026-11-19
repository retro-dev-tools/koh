using Koh.Common;
using Koh.Native;
using Koh.Objects;

namespace Koh.TestSupport;

public static class DebugFixture
{
    public static byte[] Mapping(
        byte bank,
        ushort address,
        byte byteCount,
        string sourceFile,
        uint line
    )
    {
        var section = new SectionData(
            "fixture",
            bank == 0 ? SectionType.Rom0 : SectionType.RomX,
            address,
            bank,
            new byte[byteCount],
            [new LineMapEntry(0, byteCount, sourceFile, line)]
        );
        var model = new EmitModel([section], [], []);
        var result = new NativeLinker().Link([new LinkerInput(sourceFile, model)]);
        if (!result.Success)
            throw new InvalidOperationException(
                string.Join("; ", result.Diagnostics.Select(d => d.Message))
            );
        return result.DebugData;
    }
}
