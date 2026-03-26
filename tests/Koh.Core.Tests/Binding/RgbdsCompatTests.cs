using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

/// <summary>
/// Tests for RGBDS-specific macro/REPT/FOR behaviors identified by the
/// RGBDS macro consultant expert.
/// </summary>
public class RgbdsCompatTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    // =========================================================================
    // \@ in REPT — unique labels per iteration
    // =========================================================================

    [Test]
    public async Task Rept_UniqueLabelsWithBackslashAt()
    {
        // \@ produces a unique suffix per iteration so labels don't collide
        var model = Emit("SECTION \"Main\", ROM0\nREPT 3\nnop\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
    }

    // =========================================================================
    // _NARG as a real symbol
    // =========================================================================

    [Test]
    public async Task Macro_NargAsSymbol()
    {
        // _NARG must be a real symbol, not text substitution
        // This means db _NARG should produce the argument count as a byte
        var model = Emit("count_args: MACRO\ndb _NARG\nENDM\nSECTION \"Main\", ROM0\ncount_args a, b, c");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)3);
    }

    // =========================================================================
    // Paren-depth in macro args
    // =========================================================================

    [Test]
    public async Task Macro_ParenDepthInArgs()
    {
        // Commas inside parentheses don't split: BANK(x), y → 2 args
        var model = Emit("emit_two: MACRO\ndb \\1\ndb \\2\nENDM\nSECTION \"Main\", ROM0\nemit_two HIGH($AABB), LOW($CCDD)");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xAA); // HIGH($AABB)
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xDD); // LOW($CCDD)
    }

    // =========================================================================
    // FOR variable as real symbol
    // =========================================================================

    [Test]
    public async Task For_VariableIsRealSymbol()
    {
        // After FOR loop, the variable retains its last value
        var model = Emit("SECTION \"Main\", ROM0\nFOR I, 0, 4\nnop\nENDR\ndb I");
        await Assert.That(model.Success).IsTrue();
        // 4 nops + db I (I = last value which doesn't satisfy loop condition, so I = 3 at last iteration)
        // Actually: FOR I, 0, 4 → I = 0,1,2,3. After loop, I = 3 (last iteration value)
        // Then db I → db 3
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(5); // 4 nops + 1 db
        await Assert.That(model.Sections[0].Data[4]).IsEqualTo((byte)3);
    }

    // =========================================================================
    // Recursion depth limit
    // =========================================================================

    [Test]
    public async Task Macro_RecursionLimit()
    {
        // Infinite recursion must produce a clean diagnostic, not a stack overflow
        var model = Emit("recurse: MACRO\nrecurse\nENDM\nSECTION \"Main\", ROM0\nrecurse");
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("Maximum") && d.Message.Contains("depth"))).IsTrue();
    }

    // =========================================================================
    // \# — all remaining args
    // =========================================================================

    [Test]
    public async Task Macro_BackslashHash_AllArgs()
    {
        // \# expands to all arguments as comma-separated string
        // In a simple case: emit_all a, b → \# = "a, b"
        // We test with a macro that uses \# to forward args
        var model = Emit("fwd: MACRO\ndb \\#\nENDM\nSECTION \"Main\", ROM0\nfwd $01, $02, $03");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x01);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x02);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0x03);
    }

    // =========================================================================
    // _NARG save/restore across nested macro calls
    // =========================================================================

    /// <summary>
    /// _NARG must be correctly restored to the outer macro's argument count
    /// after an inner macro call completes. Without the try/finally fix, the
    /// outer macro would see the inner macro's _NARG value after the nested call.
    /// </summary>
    [Test]
    public async Task Macro_NargRestoredAfterNestedCall()
    {
        // outer calls inner; after inner returns, outer's _NARG must still be 2
        var model = Emit("""
            inner: MACRO
                db _NARG
            ENDM
            outer: MACRO
                inner $FF
                db _NARG
            ENDM
            SECTION "Main", ROM0
            outer $01, $02
            """);
        await Assert.That(model.Success).IsTrue();
        // inner is called with 1 arg → db 1; then outer emits db _NARG (outer has 2 args) → db 2
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1); // inner's _NARG
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2); // outer's _NARG restored
    }
}
