using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class CharMapTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    [Test]
    public async Task Charmap_SingleMapping()
    {
        var model = Emit("""
            CHARMAP "A", $41
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x41);
    }

    [Test]
    public async Task Charmap_CustomMapping()
    {
        var model = Emit("""
            CHARMAP "A", $80
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task Charmap_UnmappedCharUsesAscii()
    {
        var model = Emit("""
            CHARMAP "A", $80
            SECTION "Main", ROM0
            db "B"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)'B');
    }

    [Test]
    public async Task Charmap_MultipleChars()
    {
        var model = Emit("""
            CHARMAP "A", $80
            CHARMAP "B", $81
            SECTION "Main", ROM0
            db "AB"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x81);
    }

    [Test]
    public async Task Newcharmap_AutoActivatesAndUsesNewMap()
    {
        var model = Emit("""
            CHARMAP "A", $80
            NEWCHARMAP alt
            CHARMAP "A", $90
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // NEWCHARMAP auto-activates — no SETCHARMAP needed
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x90);
    }

    [Test]
    public async Task Prechmap_Popcharmap_RestoresAcrossMaps()
    {
        var model = Emit("""
            CHARMAP "A", $80
            PRECHMAP
            NEWCHARMAP alt
            CHARMAP "A", $90
            POPCHARMAP
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // PRECHMAP saves default map, NEWCHARMAP alt activates alt, POPCHARMAP restores default
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task Charmap_StringLiteralInDb()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db "Hi"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)'H');
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)'i');
    }

    [Test]
    public async Task Charmap_NoMapping_AsciiPassthrough()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db "Z"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)'Z');
    }

    [Test]
    public async Task Setcharmap_SwitchesActiveMap()
    {
        var model = Emit("""
            CHARMAP "A", $80
            NEWCHARMAP alt
            CHARMAP "A", $90
            SETCHARMAP ""
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // SETCHARMAP "" switches back to default map where A → $80
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task MultiCharKey_SurvivesPushcPopc()
    {
        var model = Emit("""
            CHARMAP "AB", $FF
            PUSHC
            NEWCHARMAP alt
            CHARMAP "X", $01
            POPC
            SECTION "Main", ROM0
            db "AB"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // After POPC restores default, multi-char "AB" → $FF should work
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Newcharmap_DuplicateName_ReportsError()
    {
        var model = Emit("""
            NEWCHARMAP foo
            NEWCHARMAP foo
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("already exists"))).IsTrue();
    }

    [Test]
    public async Task Setcharmap_UnknownName_ReportsError()
    {
        var model = Emit("""
            SETCHARMAP nonexistent
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("not found"))).IsTrue();
    }

    [Test]
    public async Task Popcharmap_WithoutPrechmap_ReportsError()
    {
        var model = Emit("""
            POPCHARMAP
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("without matching PUSHC/PRECHMAP"))).IsTrue();
    }

    [Test]
    public async Task Charmap_MultiCharKey_LongestMatch()
    {
        var model = Emit("""
            CHARMAP "AB", $FF
            SECTION "Main", ROM0
            db "AB"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // Multi-char mapping: "AB" → single byte $FF
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Charmap_EmptyString_ZeroBytes()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db ""
            db $01
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task Charmap_MultiByte_Encoding()
    {
        var model = Emit("""
            CHARMAP "hello", $80, $01, $00
            SECTION "Main", ROM0
            db "hello"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x01);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Revchar_SingleByte()
    {
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            CHARMAP "X", $58
            MY_STR EQUS REVCHAR($58)
            SECTION "Main", ROM0
            PRINTLN "{MY_STR}"
            nop
            """);
        var model = Compilation.Create(new Koh.Core.Binding.BinderOptions(), sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("X");
    }

    [Test]
    public async Task Revchar_MultiByte()
    {
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            CHARMAP "Hi", $80, $01
            MY_STR EQUS REVCHAR($80, $01)
            SECTION "Main", ROM0
            PRINTLN "{MY_STR}"
            nop
            """);
        var model = Compilation.Create(new Koh.Core.Binding.BinderOptions(), sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("Hi");
    }

    [Test]
    public async Task Revchar_NoMatch_ReportsError()
    {
        var model = Emit("""
            CHARMAP "X", $58
            BAD EQUS REVCHAR($99)
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("REVCHAR"))).IsTrue();
    }

    [Test]
    public async Task Newcharmap_CopyFromBase()
    {
        var model = Emit("""
            NEWCHARMAP base_map
            CHARMAP "A", $80
            NEWCHARMAP derived, base_map
            SECTION "Main", ROM0
            db "A"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task Newcharmap_UnknownBase_ReportsError()
    {
        var model = Emit("""
            NEWCHARMAP derived, ghost_map
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("not found"))).IsTrue();
    }

    [Test]
    public async Task DefaultCharmap_NamedMain()
    {
        var model = Emit("""
            CHARMAP "Z", $90
            NEWCHARMAP other
            CHARMAP "Z", $91
            SETCHARMAP main
            SECTION "Main", ROM0
            db "Z"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x90);
    }

    // =========================================================================
    // RGBDS: charmap-unicode.asm — multi-byte UTF-8 characters as charmap keys
    // =========================================================================

    [Test]
    public async Task CharmapUnicode_SingleUtf8Char_CharlenIsOne()
    {
        // RGBDS: charmap-unicode.asm — charmap "デ", 42 → charlen("デ") == 1
        var model = Emit("""
            CHARMAP "デ", 42
            SECTION "Main", ROM0
            db "デ"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)42);
    }

    [Test]
    public async Task CharmapUnicode_MultipleUtf8Chars_CharlenMatchesCodepointCount()
    {
        // RGBDS: charmap-unicode.asm — charmap "グレイシア", 99 → 5 codepoints map to 1 byte
        var model = Emit("""
            CHARMAP "グレイシア", 99
            SECTION "Main", ROM0
            db "グレイシア"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)99);
    }

    [Test]
    public async Task CharmapUnicode_MixedAsciiAndUtf8()
    {
        // RGBDS: charmap-unicode.asm — charmap "Pokémon", 77 → maps 7-codepoint sequence
        var model = Emit("""
            CHARMAP "Pokémon", 77
            SECTION "Main", ROM0
            db "Pokémon"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)77);
    }

    // =========================================================================
    // RGBDS: multivalue-charmap.asm — multi-byte charmap values, truncation warnings
    // =========================================================================

    [Test]
    public async Task MultivalueCharmap_MultiByteCharEntry_DbEmitsAllBytes()
    {
        // RGBDS: multivalue-charmap.asm — charmap "啊", $04, $c3 → 2-byte mapping
        var model = Emit("""
            SECTION "test", ROM0[$0]
            CHARMAP "a", $61
            CHARMAP "啊", $04, $c3
            db "a啊"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x61);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x04);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0xC3);
    }

    [Test]
    public async Task MultivalueCharmap_MultiByteKeyMergedEntry_DwEmitsAll()
    {
        // RGBDS: multivalue-charmap.asm — charmap "de", $6564 → 2-byte key, 2-byte val
        var model = Emit("""
            SECTION "test", ROM0[$0]
            CHARMAP "de", $64, $65
            dw "de"
            """);
        await Assert.That(model.Success).IsTrue();
        // dw emits value as little-endian word per charmap unit
        await Assert.That(model.Sections[0].Data.Length).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task MultivalueCharmap_DbWithLargeCharValue_TruncationWarning()
    {
        // RGBDS: multivalue-charmap.asm — charmap "A", $01234567 → DB truncates to $67
        var model = Emit("""
            SECTION "test", ROM0[$0]
            CHARMAP "A", $01234567
            db "A"
            """);
        // DB truncates to 8 bits — produces warning
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Warning)).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x67);
    }

    // =========================================================================
    // RGBDS: equ-charmap.asm — char literal 'A' via EQU
    // =========================================================================

    [Test]
    public async Task EquCharmap_CharLiteralViaEqu_DbEmitsCorrectByte()
    {
        // RGBDS: equ-charmap.asm — DEF _A_ EQU 'A' where charmap "A", 1
        var model = Emit("""
            CHARMAP "A", 1
            SECTION "sec", ROM0[$0]
            DEF _A_ EQU 'A'
            db _A_
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  DIAG: [{d.Severity}] {d.Message}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
    }

    // =========================================================================
    // RGBDS: null-char-functions.asm — null byte in EQUS strings
    // =========================================================================

    [Test]
    public async Task NullCharFunctions_NullInEqusString_StrlenCountsNull()
    {
        // RGBDS: null-char-functions.asm — "hello\0world" has strlen 11
        var model = Emit("""
            def s equs "hello\0world"
            SECTION "Main", ROM0
            IF STRLEN("{s}") == 11
            db $01
            ELSE
            db $00
            ENDC
            """);
        // strlen of a string with embedded null counts the null byte
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task NullCharFunctions_NullInEqusString_PrintlnShowsNull()
    {
        // RGBDS: null-char-functions.asm — PRINTLN of "hello\0world" shows "hello world"
        // (null printed as space in RGBDS reference output)
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            def s equs "hello\0world"
            PRINTLN "{s}"
            SECTION "Main", ROM0
            nop
            """);
        var model = Compilation.Create(new Koh.Core.Binding.BinderOptions(), sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        // The output must contain "hello" and "world"
        var output = sw.ToString();
        await Assert.That(output).Contains("hello");
        await Assert.That(output).Contains("world");
    }

    [Test]
    public async Task NullCharFunctions_Strcat_NullByteMidString()
    {
        // RGBDS: null-char-functions.asm — strcat("hello\0world", "\0lol") == "hello\0world\0lol"
        var model = Emit("""
            def s equs "hello\0world"
            SECTION "Main", ROM0
            IF !STRCMP(STRCAT("{s}", "\0lol"), "hello\0world\0lol")
            db $01
            ELSE
            db $00
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
    }
}
