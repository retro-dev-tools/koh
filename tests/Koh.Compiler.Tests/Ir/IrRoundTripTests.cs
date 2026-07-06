using Koh.Compiler.Ir;

namespace Koh.Compiler.Tests.Ir;

public class IrRoundTripTests
{
    /// <summary>A module exercising every instruction, type, and global form.</summary>
    private const string Canonical = """
        module "test"

        global @counter : i8 addrspace(wram)
        global @table : [3 x i8] addrspace(rom) = 0x01, 0x02, 0x03

        extern func @wait_vblank() : void

        func @compute(%a : i8, %b : i16) : i16 bank(2) {
        entry:
          %p = alloca i8
          store i8 %a, i8* %p
          %v = load i8* %p
          %w = zext i8 %v to i16
          %sum = add i16 %w, %b
          %c = icmp ugt i16 %sum, 100
          condbr %c, big, small
        big:
          call void @wait_vblank()
          %e = gep i8, [3 x i8] addrspace(rom)* @table, i16 1
          br done
        small:
          br done
        done:
          %r = phi i16 [ %sum, big ], [ %b, small ]
          ret i16 %r
        }

        func @main() : void {
        entry:
          %x = call i16 @compute(i8 5, i16 10)
          switch i16 %x, other [ 0: zero, 1: one ]
        zero:
          ret void
        one:
          ret void
        other:
          ret void
        }
        """;

    [Test]
    public async Task Canonical_ParsesAndVerifiesClean()
    {
        var module = IrParser.Parse(Canonical);
        var errors = IrVerifier.Verify(module);
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task Canonical_HasExpectedStructure()
    {
        var module = IrParser.Parse(Canonical);
        await Assert.That(module.Name).IsEqualTo("test");
        await Assert.That(module.Globals.Count).IsEqualTo(2);
        await Assert.That(module.Functions.Count).IsEqualTo(3);

        var compute = module.FindFunction("compute")!;
        await Assert.That(compute.Bank).IsEqualTo(2);
        await Assert.That(compute.Blocks.Count).IsEqualTo(4);
        await Assert.That(compute.Parameters.Count).IsEqualTo(2);

        var wait = module.FindFunction("wait_vblank")!;
        await Assert.That(wait.IsExternal).IsTrue();

        var table = module.FindGlobal("table")!;
        await Assert.That(table.Initializer!.Length).IsEqualTo(3);
        await Assert.That(table.Initializer![1]).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task Print_Parse_Print_IsStable()
    {
        var first = IrPrinter.Print(IrParser.Parse(Canonical));
        var second = IrPrinter.Print(IrParser.Parse(first));
        await Assert.That(second).IsEqualTo(first);
    }

    /// <summary>A pointer and an integer of the address width reinterpret via <c>bitcast</c>.</summary>
    private const string BitcastModule = """
        module "t"

        func @reinterpret(%a : i16) : i16 {
        entry:
          %p = bitcast i16 %a to i8*
          %b = bitcast i8* %p to i16
          ret i16 %b
        }
        """;

    [Test]
    public async Task Bitcast_RoundTripsAndVerifies()
    {
        var module = IrParser.Parse(BitcastModule);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
        var printed = IrPrinter.Print(module);
        await Assert.That(printed).Contains("bitcast i16 %a to i8*");
        await Assert.That(IrPrinter.Print(IrParser.Parse(printed))).IsEqualTo(printed);
    }

    [Test]
    public async Task Bitcast_RejectsMismatchedSize()
    {
        // i8 (1 byte) cannot bitcast to i16 (2 bytes): sizes must match.
        var module = IrParser.Parse("""
            module "t"
            func @bad(%a : i8) : i16 {
            entry:
              %b = bitcast i8 %a to i16
              ret i16 %b
            }
            """);
        await Assert.That(IrVerifier.Verify(module)).IsNotEmpty();
    }
}
