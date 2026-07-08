using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Ir;

namespace Koh.Compiler.Tests.Backends;

/// <summary>Drives a concern-specific emitter (ArithmeticEmitter) in isolation over a hand-built IR
/// function — demonstrating the decomposed emitters are independently constructible and testable.</summary>
public class Sm83EmitterTests
{
    [Test]
    public async Task ArithmeticEmitter_LowersI8Add()
    {
        // func f(a: i8, b: i8) : i8 { entry: %r = add a, b; ret %r }
        var fn = new IrFunction("f", IrType.I8,
            [new IrParameter("a", IrType.I8), new IrParameter("b", IrType.I8)]);
        var builder = new IrBuilder();
        builder.PositionAtEnd(fn.AppendBlock("entry"));
        var add = (BinaryInstruction)builder.Add(fn.Parameters[0], fn.Parameters[1]);
        builder.Ret(add);

        // Parameters are placed first, so a -> 0xC000, b -> 0xC001.
        var allocations = new Dictionary<IrFunction, FunctionAllocation> { [fn] = FunctionAllocation.For(fn, 0xC000) };
        var emitter = new Emitter();
        var ctx = new Sm83Backend.EmitContext(
            emitter, fn, allocations,
            new Dictionary<IrGlobal, int>(),
            ImmutableHashSet<IrFunction>.Empty,
            isEntry: false, softStackBase: 0);
        var arith = new Sm83Backend.ArithmeticEmitter(ctx);

        arith.EmitBinary(add);

        // LD A,(b) ; LD B,A ; LD A,(a) ; ADD A,B ; LD (r),A
        await Assert.That(emitter.Code.Count).IsEqualTo(11);
        await Assert.That(emitter.Code.GetRange(0, 9))
            .IsEquivalentTo(new byte[] { 0xFA, 0x01, 0xC0, 0x47, 0xFA, 0x00, 0xC0, 0x80, 0xEA });
    }
}
