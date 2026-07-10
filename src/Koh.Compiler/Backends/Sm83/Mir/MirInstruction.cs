namespace Koh.Compiler.Backends.Sm83.Mir;

/// <summary>
/// One decoded SM83 machine instruction: its raw encoding, its offset in the region it was decoded
/// from, and its <see cref="MirEffects"/> footprint. Holding the exact bytes makes re-encoding lossless
/// (<see cref="MirProgram.ToBytes"/> concatenates them), so lifting a region to MIR and lowering it
/// back is an identity — the property that lets a peephole or superoptimizer rewrite in the MIR domain
/// and drop back to bytes without a separate encoder round-trip risk.
/// </summary>
public sealed class MirInstruction
{
    public MirInstruction(int offset, byte[] bytes, MirEffects effects)
    {
        Offset = offset;
        Bytes = bytes;
        Effects = effects;
    }

    /// <summary>Byte offset of this instruction within the region it was decoded from.</summary>
    public int Offset { get; }

    /// <summary>The raw encoding, 1–3 bytes (CB-prefixed instructions are 2).</summary>
    public byte[] Bytes { get; }

    /// <summary>The primary opcode byte (the CB prefix for CB-prefixed instructions).</summary>
    public byte Opcode => Bytes[0];

    /// <summary>True for a CB-prefixed rotate/shift/bit instruction.</summary>
    public bool IsCbPrefixed => Bytes[0] == 0xCB;

    /// <summary>Encoded length in bytes.</summary>
    public int Length => Bytes.Length;

    public MirEffects Effects { get; }

    public override string ToString() =>
        // A truncated CB tail has no second byte, so guard on length rather than assuming CB ⇒ 2 bytes.
        (IsCbPrefixed && Bytes.Length > 1 ? $"CB {Bytes[1]:X2}" : $"{Bytes[0]:X2}")
        + $"  r-{Effects.RegRead} w-{Effects.RegWrite} fr-{Effects.FlagRead} fw-{Effects.FlagWrite}"
        + (Effects.MemRead ? " memR" : "")
        + (Effects.MemWrite ? " memW" : "")
        + (Effects.Control == MirControl.Fallthrough ? "" : $" {Effects.Control}");
}

/// <summary>A decoded run of instructions that re-encodes losslessly to its source bytes.</summary>
public sealed class MirProgram
{
    public MirProgram(IReadOnlyList<MirInstruction> instructions) => Instructions = instructions;

    public IReadOnlyList<MirInstruction> Instructions { get; }

    /// <summary>Concatenate the instruction encodings back into a flat byte buffer.</summary>
    public byte[] ToBytes()
    {
        var total = 0;
        foreach (var instruction in Instructions)
            total += instruction.Length;
        var bytes = new byte[total];
        var at = 0;
        foreach (var instruction in Instructions)
        {
            instruction.Bytes.CopyTo(bytes, at);
            at += instruction.Length;
        }
        return bytes;
    }
}
