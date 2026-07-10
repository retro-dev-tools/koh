namespace Koh.Compiler.Backends.Sm83.Mir;

/// <summary>
/// One decoded SM83 machine instruction: a view over its raw encoding — an offset/length slice of the
/// source buffer, so no bytes are copied — together with its <see cref="MirEffects"/> footprint. Holding
/// a slice of the shared source rather than a private array means decoding a region allocates nothing per
/// instruction, and re-encoding (<see cref="MirProgram.ToBytes"/>) is a straight copy of those slices, so
/// lifting a region to MIR and lowering it back is an identity. A value type for the same reason: a
/// decoded run is a list of slices, not a graph of heap objects.
/// </summary>
public readonly struct MirInstruction
{
    private readonly ReadOnlyMemory<byte> _source;

    public MirInstruction(ReadOnlyMemory<byte> source, int offset, int length, MirEffects effects)
    {
        _source = source;
        Offset = offset;
        Length = length;
        Effects = effects;
    }

    /// <summary>Byte offset of this instruction within the region it was decoded from.</summary>
    public int Offset { get; }

    /// <summary>Encoded length in bytes, 1–3 (CB-prefixed instructions are 2).</summary>
    public int Length { get; }

    public MirEffects Effects { get; }

    /// <summary>The raw encoding as a read-only view over the source buffer — no copy, and not mutable
    /// through this instruction, so a consumer cannot corrupt the bytes a re-encode would reproduce.</summary>
    public ReadOnlySpan<byte> Bytes => _source.Span.Slice(Offset, Length);

    /// <summary>The primary opcode byte (the CB prefix for CB-prefixed instructions).</summary>
    public byte Opcode => _source.Span[Offset];

    /// <summary>True for a CB-prefixed rotate/shift/bit instruction.</summary>
    public bool IsCbPrefixed => Opcode == 0xCB;

    public override string ToString() =>
        // A truncated CB tail has no second byte, so guard on length rather than assuming CB ⇒ 2 bytes.
        (IsCbPrefixed && Length > 1 ? $"CB {Bytes[1]:X2}" : $"{Opcode:X2}")
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
        // Indexed loops (not foreach over the IReadOnlyList) avoid a boxed interface enumerator.
        var total = 0;
        for (var i = 0; i < Instructions.Count; i++)
            total += Instructions[i].Length;

        var bytes = new byte[total];
        var at = 0;
        for (var i = 0; i < Instructions.Count; i++)
        {
            var instruction = Instructions[i];
            instruction.Bytes.CopyTo(bytes.AsSpan(at));
            at += instruction.Length;
        }
        return bytes;
    }
}
