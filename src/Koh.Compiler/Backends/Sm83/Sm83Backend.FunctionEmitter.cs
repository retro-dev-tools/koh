using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>Lowers one function: creates the shared EmitContext, emits the prologue, and walks the
    /// blocks dispatching each instruction to the arithmetic, memory, or control-flow emitter.</summary>
    internal sealed class FunctionEmitter
    {
        private readonly EmitContext _ctx;
        private readonly Emitter _e;
        private readonly ArithmeticEmitter _arith;
        private readonly MemoryEmitter _mem;
        private readonly ControlFlowEmitter _cf;

        public FunctionEmitter(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null,
            int wramGlobalsSize = 0
        )
        {
            _ctx = new EmitContext(
                emitter,
                fn,
                allocations,
                globals,
                recursive,
                isEntry,
                softStackBase,
                banked,
                wramGlobalsSize
            );
            _e = emitter;
            _arith = new ArithmeticEmitter(_ctx);
            _mem = new MemoryEmitter(_ctx);
            _cf = new ControlFlowEmitter(_ctx);
        }

        public void Compile()
        {
            // Boot-only, and deliberately BEFORE the CALL label: the cartridge boots into this byte (the
            // recorded entry address), but a recursive CALL targets FunctionLabel below and skips it. Both
            // sections below run for EVERY program (not just a recursive one) exactly once, which is the
            // whole point of placing them here rather than as ordinary instructions in the entry function's
            // own IR body: that body re-runs on every recursive re-entry (Main calling Main), which would
            // undo either one on every call instead of only at true boot. In multi-bank mode the boot stub
            // jumps straight to FunctionLabel (it can't reach a pre-label byte), so there both live in the
            // boot stub instead (see CompileMultiBank); a non-empty Banked set marks that mode.
            if (_ctx.IsEntry && _ctx.Banked.Count == 0)
            {
                // Zero the WRAM-globals region: every module-scope static field/array with no explicit
                // initializer defaults to zero in C#, but real hardware (and mGBA) do not guarantee WRAM
                // starts zeroed the way the managed test-harness emulator's byte[]-backed memory happens to.
                EmitWramGlobalsClear(_e, _ctx.WramGlobalsSize);
            }
            if (_ctx.IsEntry && _ctx.Recursive.Count > 0 && _ctx.Banked.Count == 0)
            {
                // Move the hardware CALL stack into WRAM (it defaults to the tiny HRAM window, where deep
                // recursion overflows into the I/O registers and crashes). Growing down from just below
                // ArgScratch gives it the whole arena above the static frames.
                _e.U8(0x31);
                _e.U16(HwStackTop); // LD SP, HwStackTop
                // Initialize the software-stack pointer once at boot (only needed when some function
                // recurses and therefore saves its frame there).
                LdHL(_e, _ctx.SoftStackBase); // LD HL, softStackBase
                _e.U8(0x7D);
                _e.StoreA(SoftSp); // LD A, L ; LD (SoftSp), A
                _e.U8(0x7C);
                _e.StoreA(SoftSp + 1); // LD A, H ; LD (SoftSp+1), A
            }

            // The CALL target is here, before any prologue (the entry block label follows the prologue).
            _e.Place(_e.FunctionLabel(_ctx.Fn));

            // Interrupt handlers must preserve everything they touch; push at entry, pop before RETI.
            if (_ctx.Fn.InterruptVector is not null)
            {
                _e.U8(0xF5); // PUSH AF
                _e.U8(0xC5); // PUSH BC
                _e.U8(0xD5); // PUSH DE
                _e.U8(0xE5); // PUSH HL
            }

            if (_ctx.IsRecursive)
            {
                if (_ctx.FrameSize > 255)
                    throw new Sm83LimitException(
                        $"recursive function '{_ctx.Fn.Name}' frame is {_ctx.FrameSize} bytes; the software-stack "
                            + "save supports up to 255."
                    );
                // Save the caller's copy of the shared static frame, then install this call's arguments
                // (staged in ArgScratch) into the parameter slots at the frame base. A zero-byte frame
                // (no params/locals — recursion driven through globals) has nothing to save; skip it, as
                // rt.pushframe with a count of 0 would `dec b` to 0xFF and copy 256 bytes.
                if (_ctx.FrameSize > 0)
                {
                    LdDE(_e, _ctx.FrameBase); // LD DE, frameBase
                    _e.U8(0x06);
                    _e.U8(_ctx.FrameSize); // LD B, frameSize
                    _e.Jump(0xCD, _e.RoutineLabel("rt.pushframe"));
                    int paramBytes = _ctx.ParamBytes(_ctx.Fn);
                    for (int k = 0; k < paramBytes; k++)
                    {
                        _e.LoadA(ArgScratch + k);
                        _e.StoreA(_ctx.FrameBase + k);
                    }
                }
            }

            foreach (var block in _ctx.Fn.Blocks)
            {
                _e.Place(_e.BlockLabel(block));
                foreach (var instr in block.Instructions)
                {
                    int start = _e.Code.Count;
                    EmitInstruction(block, instr);
                    if (instr.Source is { } src && _e.Code.Count > start)
                        _e.AddLineRange(start, _e.Code.Count - start, src.File, src.Line);
                }
            }
        }

        private void EmitInstruction(IrBasicBlock block, IrInstruction instr)
        {
            switch (instr)
            {
                case BinaryInstruction b:
                    _arith.EmitBinary(b);
                    break;
                case CompareInstruction c:
                    _arith.EmitCompare(c);
                    break;
                case ConvInstruction cv:
                    _arith.EmitConv(cv);
                    break;
                case LoadInstruction l:
                    _mem.EmitLoad(l);
                    break;
                case StoreInstruction s:
                    _mem.EmitStore(s);
                    break;
                case AllocaInstruction:
                    break; // storage pre-assigned
                case GetElementPtrInstruction g:
                    if (_ctx.FusedGep.Contains(g))
                        break; // computed inline at its single load/store use instead — see EmitContext
                    if (_ctx.Slot.ContainsKey(g)) // dynamic: compute the pointer at runtime
                        _mem.EmitGep(g);
                    break; // static: address pre-assigned
                case PhiInstruction:
                    break; // realized by predecessor edge copies
                case RetInstruction r:
                    _cf.EmitRet(r);
                    break;
                case BrInstruction br:
                    _cf.EmitBr(block, br);
                    break;
                case CondBrInstruction cb:
                    _cf.EmitCondBr(block, cb);
                    break;
                case SwitchInstruction sw:
                    _cf.EmitSwitch(block, sw);
                    break;
                case CallInstruction call:
                    _cf.EmitCall(call);
                    break;
                case IntrinsicInstruction intr:
                    _cf.EmitIntrinsic(intr);
                    break;
                default:
                    throw new NotSupportedException(
                        $"MVP SM83 backend does not support '{instr.Mnemonic}' (in '@{_ctx.Fn.Name}')."
                    );
            }
        }
    }
}
