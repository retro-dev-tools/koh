using Koh.Compiler.Backends.Sm83.Mir;
using Koh.Opcodes;

namespace Koh.Compiler.Tests.Sm83;

/// <summary>
/// Cross-checks instruction lengths from <see cref="Sm83Disassembler"/> (driven by the assembler's
/// instruction table) against <see cref="MirDecoder"/> (the backend's independent structural decoder),
/// for every unprefixed and CB-prefixed opcode.
/// </summary>
public class DisassemblerLengthTests
{
    [Test]
    public async Task EveryOpcode_HasTheSameLength_InBothDecoders()
    {
        var mismatches = new List<string>();
        for (int op = 0; op < 256; op++)
        {
            foreach (
                var code in new[]
                {
                    new byte[] { (byte)op, 0x00, 0x00 },
                    new byte[] { 0xCB, (byte)op, 0x00 },
                }
            )
            {
                var (text, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
                int mirLength = MirDecoder.Decode(code).Instructions[0].Length;
                if (length != mirLength)
                    mismatches.Add(
                        $"{Convert.ToHexString(code, 0, code[0] == 0xCB ? 2 : 1)} {text}: {length} vs {mirLength}"
                    );
            }
        }
        await Assert.That(mismatches).IsEmpty();
    }
}
