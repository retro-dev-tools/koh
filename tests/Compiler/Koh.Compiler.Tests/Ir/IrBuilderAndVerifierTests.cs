using Koh.Compiler.Ir;

namespace Koh.Compiler.Tests.Ir;

public class IrBuilderAndVerifierTests
{
    /// <summary>Builds @sum(n) = 0+1+...+(n-1) with a loop and two phis, entirely via the builder.</summary>
    private static IrModule BuildSumLoop()
    {
        var module = new IrModule("built");
        var n = new IrParameter("n", IrType.I16);
        var fn = new IrFunction("sum", IrType.I16, [n]);
        module.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var loop = fn.AppendBlock("loop");
        var done = fn.AppendBlock("done");

        var b = new IrBuilder();
        var zero = IrBuilder.ConstInt(IrType.I16, 0);
        var one = IrBuilder.ConstInt(IrType.I16, 1);

        b.PositionAtEnd(entry);
        b.Br(loop);

        b.PositionAtEnd(loop);
        var i = b.Phi(IrType.I16);
        var acc = b.Phi(IrType.I16);
        var accNext = b.Add(acc, i);
        var iNext = b.Add(i, one);
        var cond = b.Compare(IrCompareOp.Ult, iNext, n);
        b.CondBr(cond, loop, done);

        i.AddIncoming(zero, entry);
        i.AddIncoming(iNext, loop);
        acc.AddIncoming(zero, entry);
        acc.AddIncoming(accNext, loop);

        b.PositionAtEnd(done);
        b.Ret(accNext);

        return module;
    }

    [Test]
    public async Task Builder_ProducesVerifiableModule()
    {
        var errors = IrVerifier.Verify(BuildSumLoop());
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task Builder_RoundTripsThroughText()
    {
        var printed = IrPrinter.Print(BuildSumLoop());
        var reparsed = IrPrinter.Print(IrParser.Parse(printed));
        await Assert.That(reparsed).IsEqualTo(printed);
    }

    [Test]
    public async Task Verifier_FlagsMissingTerminator()
    {
        var module = new IrModule("bad");
        var fn = new IrFunction("f", IrType.Void, []);
        module.Functions.Add(fn);
        var block = fn.AppendBlock("entry");
        var b = new IrBuilder();
        b.PositionAtEnd(block);
        b.Alloca(IrType.I8); // non-terminator as the last (and only) instruction

        var errors = IrVerifier.Verify(module);
        await Assert.That(errors.Any(e => e.Contains("terminator"))).IsTrue();
    }

    [Test]
    public async Task Verifier_FlagsReturnTypeMismatch()
    {
        var module = new IrModule("bad");
        var fn = new IrFunction("f", IrType.I16, []);
        module.Functions.Add(fn);
        var block = fn.AppendBlock("entry");
        var b = new IrBuilder();
        b.PositionAtEnd(block);
        b.Ret(IrBuilder.ConstInt(IrType.I8, 0)); // returns i8 from an i16 function

        var errors = IrVerifier.Verify(module);
        await Assert.That(errors.Any(e => e.Contains("ret"))).IsTrue();
    }

    [Test]
    public async Task Verifier_FlagsCallArgTypeMismatch()
    {
        var module = new IrModule("bad");
        var callee = new IrFunction("g", IrType.Void, [new IrParameter("x", IrType.I16)]);
        var caller = new IrFunction("f", IrType.Void, []);
        module.Functions.Add(callee);
        module.Functions.Add(caller);
        var block = caller.AppendBlock("entry");
        var b = new IrBuilder();
        b.PositionAtEnd(block);
        b.Call(callee, [IrBuilder.ConstInt(IrType.I8, 1)]); // i8 arg for an i16 param
        b.Ret();

        var errors = IrVerifier.Verify(module);
        await Assert.That(errors.Any(e => e.Contains("arg"))).IsTrue();
    }
}
