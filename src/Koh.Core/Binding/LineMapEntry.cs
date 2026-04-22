namespace Koh.Core.Binding;

/// <summary>
/// One contiguous run of bytes in a section that all originate from
/// the same source line. The assembler records these as it emits so
/// the linker can turn section offsets into absolute addresses for the
/// .kdbg debug info — enabling line-based breakpoints in the debugger.
///
/// Ranges are coalesced greedily as bytes are emitted: adjacent bytes
/// from the same <c>(file, line)</c> extend the prior entry in place
/// rather than allocating a new one, so a typical instruction (3 bytes
/// of opcode+operand, one source line) produces exactly one entry.
/// </summary>
public sealed record LineMapEntry(int Offset, int ByteCount, string File, uint Line);
