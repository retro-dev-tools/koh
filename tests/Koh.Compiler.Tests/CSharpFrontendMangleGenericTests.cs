using Koh.Compiler.Frontends.CSharp;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Tests;

/// <summary>Stage 2 Phase 1: <see cref="CSharpFrontend.MangleGeneric"/> now produces legal C# identifiers
/// (<c>Max__g1_4_byte</c> instead of the pre-migration <c>Max$1_4_byte</c>), a prerequisite for a later
/// phase that binds monomorphized instances in a real syntax tree. These tests pin the new scheme's shape
/// and prove <see cref="CSharpFrontend.EncodeTypeArg"/>/<see cref="CSharpFrontend.MangleGeneric"/> stay
/// injective on adversarial inputs designed to alias under a naive scheme.</summary>
public class CSharpFrontendMangleGenericTests
{
    private static TypeSyntax Type(string text) => SyntaxFactory.ParseTypeName(text);

    // ---- Shape ------------------------------------------------------------------------------------

    [Test]
    public async Task SingleTypeArg_ProducesDocumentedShape()
    {
        await Assert
            .That(CSharpFrontend.MangleGeneric("Max", [Type("byte")]))
            .IsEqualTo("Max__g1_4_byte");
    }

    [Test]
    public async Task TwoTypeArgs_ProducesDocumentedShape()
    {
        await Assert
            .That(CSharpFrontend.MangleGeneric("Max", [Type("byte"), Type("int")]))
            .IsEqualTo("Max__g2_4_byte_3_int");
    }

    [Test]
    public async Task PointerTypeArg_EscapesNonIdentifierChar()
    {
        // `*` is not [A-Za-z0-9_], so it hex-escapes: byte* -> byte_2a.
        await Assert.That(CSharpFrontend.EncodeTypeArg("byte*")).IsEqualTo("byte_2a");
        await Assert
            .That(CSharpFrontend.MangleGeneric("Read", [Type("byte*")]))
            .IsEqualTo("Read__g1_7_byte_2a");
    }

    // ---- Injectivity: adversarial pairs that would alias under a naive scheme -----------------------

    [Test]
    public async Task EncodeTypeArg_HexEscapeVsDoubledUnderscore_StayDistinct()
    {
        // A user type literally named `A_2a` doubles its underscore (`A__2a`); the pointer type `A*`
        // hex-escapes its `*` (`A_2a`). Both must decode unambiguously to different encodings.
        var literalUnderscore = CSharpFrontend.EncodeTypeArg("A_2a");
        var hexEscapedStar = CSharpFrontend.EncodeTypeArg("A*");
        await Assert.That(literalUnderscore).IsEqualTo("A__2a");
        await Assert.That(hexEscapedStar).IsEqualTo("A_2a");
        await Assert.That(literalUnderscore).IsNotEqualTo(hexEscapedStar);
    }

    [Test]
    public async Task MangleGeneric_ByteVsBytePointer_StayDistinct()
    {
        var plain = CSharpFrontend.MangleGeneric("Max", [Type("byte")]);
        var pointer = CSharpFrontend.MangleGeneric("Max", [Type("byte*")]);
        await Assert.That(plain).IsNotEqualTo(pointer);
    }

    [Test]
    public async Task EncodeTypeArg_UnderscoreStacking_StaysDistinct()
    {
        // My_T (one real underscore) vs My__T (two real underscores) must not collapse once each is
        // doubled: My_T -> My__T, My__T -> My____T.
        var one = CSharpFrontend.EncodeTypeArg("My_T");
        var two = CSharpFrontend.EncodeTypeArg("My__T");
        await Assert.That(one).IsEqualTo("My__T");
        await Assert.That(two).IsEqualTo("My____T");
        await Assert.That(one).IsNotEqualTo(two);
    }

    [Test]
    public async Task EncodeTypeArg_WideCharUsesDistinct4HexEscape()
    {
        // A char above 0xFF must not use the 2-hex form (its hex is 4 digits, and digits are legal
        // pass-through chars, so `_` + 4 digits would be ambiguous with escape(0x..) + 2 literal digits).
        // It gets the distinct `_u` + 4-hex escape instead: 'ᄀ' (U+1100) -> _u1100.
        var wide = CSharpFrontend.EncodeTypeArg("Tᄀ");
        var narrowPlusDigits = CSharpFrontend.EncodeTypeArg("T\u001100"); // 0x11 escape, then "00"
        await Assert.That(wide).IsEqualTo("T_u1100");
        await Assert.That(narrowPlusDigits).IsEqualTo("T_1100");
        await Assert.That(wide).IsNotEqualTo(narrowPlusDigits);
    }

    [Test]
    public async Task MangleGeneric_TwoArgsVsOneArgShapedLikeTheJoin_StayDistinct()
    {
        // Two arguments (byte, int) vs a single argument whose own text looks like the encoded join of
        // those two args ("byte_3_int"). The per-argument length prefix (plus the leading arg-count) keeps
        // these from aliasing even though the encoded content overlaps heavily.
        var twoArgs = CSharpFrontend.MangleGeneric("Max", [Type("byte"), Type("int")]);
        var oneArgShapedLikeTheJoin = CSharpFrontend.MangleGeneric("Max", [Type("byte_3_int")]);
        await Assert.That(twoArgs).IsEqualTo("Max__g2_4_byte_3_int");
        await Assert.That(oneArgShapedLikeTheJoin).IsEqualTo("Max__g1_12_byte__3__int");
        await Assert.That(twoArgs).IsNotEqualTo(oneArgShapedLikeTheJoin);
    }
}
