namespace Koh.Compiler.Ir.Optimization;

/// <summary>
/// Removes a redundant MBC1 ROM bank-select — a write of a constant bank number to the bank-select
/// register (<c>[0x2000, 0x4000)</c>, e.g. <c>*(byte*)0x2000 = 3;</c>) when the same bank is already
/// known to be mapped. Koh's banked-data model requires user code to select a banked global's bank
/// before dereferencing it (<c>*(byte*)0x2000 = bank;</c>), so code that reads several read-only
/// globals from one banked region repeats the same select by hand; dropping the repeats saves the
/// 3-byte <c>LD [0x2000], A</c> (plus loading the bank number into <c>A</c>) each time. (The selects
/// are author-written — no compiler stage auto-emits data-bank selects.) This is the local increment
/// of the "optimal placement of bank selection instructions" line the SM83 optimization review flags
/// as directly relevant to Koh's banking model; a cross-block dataflow version is the natural
/// follow-up.
///
/// The analysis is intra-block and deliberately conservative. Scanning a block in order it tracks the
/// bank value currently known to be selected, if any, and resets that knowledge at every point where
/// the mapped bank could change out from under it: a <c>call</c> or <c>intrinsic</c> (a callee may
/// switch banks), a store to a non-constant address (it could be the bank register in disguise), or a
/// store to any other constant address in the MBC control range <c>[0x0000, 0x8000)</c> (RAM-enable,
/// RAM-bank/high-ROM bits, banking mode). Writes to constant addresses at or above <c>0x8000</c>
/// (RAM/VRAM/OAM/IO) and all loads leave the ROM bank untouched, so they are transparent.
///
/// Soundness assumes an interrupt handler leaves the mapped ROM bank as it found it — the same
/// assumption every banked access already depends on, since a select and its dependent load are
/// separate, interruptible instructions.
/// </summary>
public sealed class RedundantBankSelectEliminationPass : IIrFunctionPass
{
    /// <summary>Start of the MBC1 ROM bank-select register: a write in <c>[0x2000, 0x4000)</c> maps
    /// that bank into the <c>0x4000-0x7FFF</c> window.</summary>
    private const long BankSelectStart = 0x2000;
    private const long BankSelectEnd = 0x4000;

    /// <summary>The whole memory-bank-controller register range. A constant-address write anywhere in
    /// it (other than the eliminable bank-select itself) can affect which ROM bank is mapped, so it is
    /// treated as a barrier that invalidates the known-bank state.</summary>
    private const long MbcControlEnd = 0x8000;

    public bool Run(IrFunction function)
    {
        var changed = false;
        foreach (var block in function.Blocks)
        {
            long? knownBank = null; // the bank value provably mapped here, or null if unknown
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                switch (block.Instructions[i])
                {
                    case StoreInstruction store when TryConstAddress(store.Pointer) is { } addr:
                        if (addr >= BankSelectStart && addr < BankSelectEnd)
                        {
                            // A bank select. If it writes a constant bank equal to the one already
                            // mapped, it is redundant; otherwise it becomes the new known bank.
                            if (store.Value is IrConstInt bank)
                            {
                                if (knownBank == bank.Value)
                                {
                                    block.Instructions.RemoveAt(i);
                                    i--;
                                    changed = true;
                                }
                                else
                                {
                                    knownBank = bank.Value;
                                }
                            }
                            else
                            {
                                knownBank = null; // a variable bank — no longer known
                            }
                        }
                        else if (addr < MbcControlEnd)
                        {
                            knownBank = null; // another MBC control write may remap the ROM bank
                        }
                        // addr >= 0x8000: ordinary memory, does not touch the ROM bank — transparent.
                        break;

                    case StoreInstruction:
                        // Store to a non-constant address: it could alias the bank register.
                        knownBank = null;
                        break;

                    case CallInstruction
                    or IntrinsicInstruction:
                        knownBank = null; // the callee may switch banks
                        break;

                    // Loads and pure computation never change the mapped bank — leave state intact.
                }
            }
        }
        return changed;
    }

    /// <summary>The compile-time address a pointer value denotes, or null if it isn't a constant. Peers
    /// through the reinterpret/resize conversions the frontend emits for <c>*(T*)0x2000</c> — a
    /// <c>bitcast</c> to a pointer over a folded integer constant, itself possibly a width conversion of
    /// one — since a bitcast to a pointer type is not folded away.</summary>
    private static long? TryConstAddress(IrValue pointer) =>
        pointer switch
        {
            IrConstInt c => c.Value,
            ConvInstruction { Op: IrConvOp.Bitcast, Operand: var op } => TryConstAddress(op),
            ConvInstruction { Op: IrConvOp.ZExt, Operand: var op } => TryConstAddress(op),
            ConvInstruction { Op: IrConvOp.Trunc, Operand: var op, Type: var t }
                when TryConstAddress(op) is { } inner => (long)IntWidth.ToUnsigned(inner, t.Bits),
            _ => null,
        };
}
