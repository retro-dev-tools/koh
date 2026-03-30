using System.Diagnostics;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax.InternalSyntax;
using Koh.Core.Text;

namespace Koh.Core.Syntax;

public sealed class Lexer
{
    private readonly SourceText _source;
    private int _position;
    private readonly DiagnosticBag _diagnostics = new();
    private SyntaxKind _lastTokenKind = SyntaxKind.None;

    /// <summary>
    /// Diagnostics produced during lexing (e.g. unterminated block comments).
    /// The parser merges these into its own bag after lexing is complete.
    /// </summary>
    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.ToList();

    private static readonly Dictionary<string, SyntaxKind> Keywords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["nop"] = SyntaxKind.NopKeyword,
        ["ld"] = SyntaxKind.LdKeyword,
        ["add"] = SyntaxKind.AddKeyword,
        ["adc"] = SyntaxKind.AdcKeyword,
        ["sub"] = SyntaxKind.SubKeyword,
        ["sbc"] = SyntaxKind.SbcKeyword,
        ["and"] = SyntaxKind.AndKeyword,
        ["or"] = SyntaxKind.OrKeyword,
        ["xor"] = SyntaxKind.XorKeyword,
        ["cp"] = SyntaxKind.CpKeyword,
        ["inc"] = SyntaxKind.IncKeyword,
        ["dec"] = SyntaxKind.DecKeyword,
        ["daa"] = SyntaxKind.DaaKeyword,
        ["cpl"] = SyntaxKind.CplKeyword,
        ["rlca"] = SyntaxKind.RlcaKeyword,
        ["rla"] = SyntaxKind.RlaKeyword,
        ["rrca"] = SyntaxKind.RrcaKeyword,
        ["rra"] = SyntaxKind.RraKeyword,
        ["rlc"] = SyntaxKind.RlcKeyword,
        ["rl"] = SyntaxKind.RlKeyword,
        ["rrc"] = SyntaxKind.RrcKeyword,
        ["rr"] = SyntaxKind.RrKeyword,
        ["sla"] = SyntaxKind.SlaKeyword,
        ["sra"] = SyntaxKind.SraKeyword,
        ["srl"] = SyntaxKind.SrlKeyword,
        ["swap"] = SyntaxKind.SwapKeyword,
        ["bit"] = SyntaxKind.BitKeyword,
        ["set"] = SyntaxKind.SetKeyword,
        ["res"] = SyntaxKind.ResKeyword,
        ["jp"] = SyntaxKind.JpKeyword,
        ["jr"] = SyntaxKind.JrKeyword,
        ["call"] = SyntaxKind.CallKeyword,
        ["ret"] = SyntaxKind.RetKeyword,
        ["reti"] = SyntaxKind.RetiKeyword,
        ["rst"] = SyntaxKind.RstKeyword,
        ["pop"] = SyntaxKind.PopKeyword,
        ["push"] = SyntaxKind.PushKeyword,
        ["di"] = SyntaxKind.DiKeyword,
        ["ei"] = SyntaxKind.EiKeyword,
        ["halt"] = SyntaxKind.HaltKeyword,
        ["stop"] = SyntaxKind.StopKeyword,
        ["ccf"] = SyntaxKind.CcfKeyword,
        ["scf"] = SyntaxKind.ScfKeyword,
        ["ldi"] = SyntaxKind.LdiKeyword,
        ["ldd"] = SyntaxKind.LddKeyword,
        ["ldh"] = SyntaxKind.LdhKeyword,
        ["z"] = SyntaxKind.ZKeyword,
        ["nz"] = SyntaxKind.NzKeyword,
        ["nc"] = SyntaxKind.NcKeyword,
        ["a"] = SyntaxKind.AKeyword,
        ["b"] = SyntaxKind.BKeyword,
        ["c"] = SyntaxKind.CKeyword,
        ["d"] = SyntaxKind.DKeyword,
        ["e"] = SyntaxKind.EKeyword,
        ["h"] = SyntaxKind.HKeyword,
        ["l"] = SyntaxKind.LKeyword,
        ["hl"] = SyntaxKind.HlKeyword,
        ["sp"] = SyntaxKind.SpKeyword,
        ["af"] = SyntaxKind.AfKeyword,
        ["bc"] = SyntaxKind.BcKeyword,
        ["de"] = SyntaxKind.DeKeyword,
        ["section"] = SyntaxKind.SectionKeyword,
        ["db"] = SyntaxKind.DbKeyword,
        ["dw"] = SyntaxKind.DwKeyword,
        ["dl"] = SyntaxKind.DlKeyword,
        ["ds"] = SyntaxKind.DsKeyword,
        ["equ"] = SyntaxKind.EquKeyword,
        ["equs"] = SyntaxKind.EqusKeyword,
        ["redef"] = SyntaxKind.RedefKeyword,
        ["export"] = SyntaxKind.ExportKeyword,
        ["purge"] = SyntaxKind.PurgeKeyword,
        ["rom0"] = SyntaxKind.Rom0Keyword,
        ["romx"] = SyntaxKind.RomxKeyword,
        ["wram0"] = SyntaxKind.Wram0Keyword,
        ["wramx"] = SyntaxKind.WramxKeyword,
        ["vram"] = SyntaxKind.VramKeyword,
        ["hram"] = SyntaxKind.HramKeyword,
        ["sram"] = SyntaxKind.SramKeyword,
        ["oam"] = SyntaxKind.OamKeyword,
        ["align"] = SyntaxKind.AlignKeyword,
        ["fragment"] = SyntaxKind.FragmentKeyword,
        ["union"] = SyntaxKind.UnionKeyword,
        ["if"] = SyntaxKind.IfKeyword,
        ["elif"] = SyntaxKind.ElifKeyword,
        ["else"] = SyntaxKind.ElseKeyword,
        ["endc"] = SyntaxKind.EndcKeyword,
        ["macro"] = SyntaxKind.MacroKeyword,
        ["endm"] = SyntaxKind.EndmKeyword,
        ["shift"] = SyntaxKind.ShiftKeyword,
        ["rept"] = SyntaxKind.ReptKeyword,
        ["for"] = SyntaxKind.ForKeyword,
        ["endr"] = SyntaxKind.EndrKeyword,
        ["break"] = SyntaxKind.BreakKeyword,
        ["include"] = SyntaxKind.IncludeKeyword,
        ["incbin"] = SyntaxKind.IncbinKeyword,
        ["charmap"] = SyntaxKind.CharmapKeyword,
        ["newcharmap"] = SyntaxKind.NewcharmapKeyword,
        ["setcharmap"] = SyntaxKind.SetcharmapKeyword,
        ["prechmap"] = SyntaxKind.PrecharmapKeyword,
        ["popcharmap"] = SyntaxKind.PopcharmapKeyword,
        // RGBDS canonical forms: PUSHC/POPC
        ["pushc"] = SyntaxKind.PrecharmapKeyword,
        ["popc"] = SyntaxKind.PopcharmapKeyword,
        ["nextu"] = SyntaxKind.NextuKeyword,
        ["endu"] = SyntaxKind.EnduKeyword,
        ["load"] = SyntaxKind.LoadKeyword,
        ["endl"] = SyntaxKind.EndlKeyword,
        ["rb"] = SyntaxKind.RbKeyword,
        ["rw"] = SyntaxKind.RwKeyword,
        // "rl" is already mapped to RlKeyword in the instruction range above
        ["rsreset"] = SyntaxKind.RsresetKeyword,
        ["rsset"] = SyntaxKind.RssetKeyword,
        ["assert"] = SyntaxKind.AssertKeyword,
        ["static_assert"] = SyntaxKind.StaticAssertKeyword,
        ["warn"] = SyntaxKind.WarnKeyword,
        ["fail"] = SyntaxKind.FailKeyword,
        ["fatal"] = SyntaxKind.FatalKeyword,
        ["print"] = SyntaxKind.PrintKeyword,
        ["println"] = SyntaxKind.PrintlnKeyword,
        ["pushs"] = SyntaxKind.PushsKeyword,
        ["pops"] = SyntaxKind.PopsKeyword,
        ["pusho"] = SyntaxKind.PushoKeyword,
        ["popo"] = SyntaxKind.PopoKeyword,
        ["opt"] = SyntaxKind.OptKeyword,
        ["high"] = SyntaxKind.HighKeyword,
        ["low"] = SyntaxKind.LowKeyword,
        ["bank"] = SyntaxKind.BankKeyword,
        ["sizeof"] = SyntaxKind.SizeofKeyword,
        ["startof"] = SyntaxKind.StartofKeyword,
        ["def"] = SyntaxKind.DefKeyword,
        ["isconst"] = SyntaxKind.IsConstKeyword,
        ["strlen"] = SyntaxKind.StrlenKeyword,
        ["strcat"] = SyntaxKind.StrcatKeyword,
        ["strsub"] = SyntaxKind.StrsubKeyword,
        ["revchar"] = SyntaxKind.RevcharKeyword,
        ["charlen"] = SyntaxKind.CharlenKeyword,
        ["incharmap"] = SyntaxKind.IncharmapKeyword,
        ["strcmp"] = SyntaxKind.StrcmpKeyword,
        ["dl"] = SyntaxKind.DlKeyword,
        // Math functions
        ["mul"] = SyntaxKind.MulKeyword,
        ["div"] = SyntaxKind.DivFuncKeyword,
        ["fmod"] = SyntaxKind.FmodKeyword,
        ["pow"] = SyntaxKind.PowKeyword,
        ["log"] = SyntaxKind.LogKeyword,
        ["round"] = SyntaxKind.RoundKeyword,
        ["ceil"] = SyntaxKind.CeilKeyword,
        ["floor"] = SyntaxKind.FloorKeyword,
        // Trig functions
        ["sin"] = SyntaxKind.SinKeyword,
        ["cos"] = SyntaxKind.CosKeyword,
        ["tan"] = SyntaxKind.TanKeyword,
        ["asin"] = SyntaxKind.AsinKeyword,
        ["acos"] = SyntaxKind.AcosKeyword,
        ["atan"] = SyntaxKind.AtanKeyword,
        ["atan2"] = SyntaxKind.Atan2Keyword,
        // String functions
        ["strfind"] = SyntaxKind.StrfindKeyword,
        ["strrfind"] = SyntaxKind.StrrfindKeyword,
        ["strupr"] = SyntaxKind.StruprKeyword,
        ["strlwr"] = SyntaxKind.StrlwrKeyword,
        ["bytelen"] = SyntaxKind.BytelenKeyword,
        ["strbyte"] = SyntaxKind.StrbyteKeyword,
        ["strchar"] = SyntaxKind.StrcharKeyword,
        ["readfile"] = SyntaxKind.ReadfileKeyword,
        ["strfmt"] = SyntaxKind.StrfmtKeyword,
        // Bit query functions
        ["bitwidth"] = SyntaxKind.BitwidthKeyword,
        ["tzcount"] = SyntaxKind.TzcountKeyword,
    };

    public Lexer(SourceText source)
    {
        _source = source;
    }

    private char Current => _position < _source.Length ? _source[_position] : '\0';

    private char Peek(int offset = 1) =>
        _position + offset < _source.Length ? _source[_position + offset] : '\0';

    private bool IsAtEnd => _position >= _source.Length;

    public SyntaxToken NextToken()
    {
        int tokenStart = _position;

        var leadingTrivia = ScanLeadingTrivia();
        int textStart = _position;

        SyntaxKind kind;
        string text;

        if (IsAtEnd)
        {
            kind = SyntaxKind.EndOfFileToken;
            text = "";
        }
        else
        {
            (kind, text) = ScanToken();
        }

        var trailingTrivia = ScanTrailingTrivia();

        var green = new GreenToken(kind, text, leadingTrivia, trailingTrivia);
        _lastTokenKind = kind;
        return new SyntaxToken(green, null, tokenStart);
    }

    internal GreenToken NextGreenToken()
    {
        int tokenStart = _position;
        var leadingTrivia = ScanLeadingTrivia();

        SyntaxKind kind;
        string text;

        if (IsAtEnd)
        {
            kind = SyntaxKind.EndOfFileToken;
            text = "";
        }
        else
        {
            (kind, text) = ScanToken();
        }

        var trailingTrivia = ScanTrailingTrivia();
        _lastTokenKind = kind;
        return new GreenToken(kind, text, leadingTrivia, trailingTrivia);
    }

    private (SyntaxKind kind, string text) ScanToken()
    {
        int start = _position;
        char c = Current;

        // Numbers: $hex, %binary, &octal, decimal
        if (c == '$' && IsHexDigit(Peek()))
        {
            _position++;
            ScanDigitsWithUnderscores(start, IsHexDigit);
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        if (c == '%' && IsBinaryDigit(Peek()))
        {
            _position++;
            ScanDigitsWithUnderscores(start, IsBinaryDigit);
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        // &octal is a number literal only when & appears where a prefix (unary) is
        // expected — i.e. the character immediately before the & (ignoring whitespace)
        // is not a token that can end a primary expression.  If the & follows an
        // identifier, digit, closing bracket/paren, or closing quote it is the
        // bitwise-AND operator and must not be consumed as a number prefix.
        if (c == '&' && IsOctalDigit(Peek()) && !PrecedingCharIsExpressionEnd(start))
        {
            _position++;
            ScanDigitsWithUnderscores(start, IsOctalDigit);
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        if (char.IsDigit(c))
        {
            // Check for C-style prefixes: 0x, 0b, 0o
            if (c == '0')
            {
                char next = Peek();
                if (next is 'x' or 'X')
                {
                    _position += 2; // consume 0x
                    ScanDigitsWithUnderscores(start, IsHexDigit);
                    return (SyntaxKind.NumberLiteral, Substring(start, _position));
                }
                if (next is 'b' or 'B')
                {
                    _position += 2; // consume 0b
                    ScanDigitsWithUnderscores(start, IsBinaryDigit);
                    return (SyntaxKind.NumberLiteral, Substring(start, _position));
                }
                if (next is 'o' or 'O')
                {
                    _position += 2; // consume 0o
                    ScanDigitsWithUnderscores(start, IsOctalDigit);
                    return (SyntaxKind.NumberLiteral, Substring(start, _position));
                }
            }

            ScanDigitsWithUnderscores(start, char.IsDigit);
            // Fixed-point literal: digits.digits (e.g. 5.0, 2.5)
            if (Current == '.' && Peek() != '\0' && char.IsDigit(Peek()))
            {
                _position++; // consume the dot
                while (char.IsDigit(Current) || Current == '_')
                    _position++;
                return (SyntaxKind.FixedPointLiteral, Substring(start, _position));
            }
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        // Raw string literals: #"..." or #"""..."""
        if (c == '#' && Peek() == '"')
        {
            return ScanRawString(start);
        }

        // String literals
        if (c == '"')
        {
            return ScanString(start);
        }

        // Character literals: 'A', '\n', etc. — single character in single quotes
        if (c == '\'' && !IsAtEnd)
        {
            _position++; // opening quote
            if (!IsAtEnd && Current == '\\')
            {
                _position++; // skip backslash
                if (!IsAtEnd) _position++; // skip escaped char
            }
            else if (!IsAtEnd && Current != '\'')
            {
                _position++; // skip the character
            }
            if (!IsAtEnd && Current == '\'')
                _position++; // closing quote
            return (SyntaxKind.CharLiteralToken, Substring(start, _position));
        }

        // Local labels: .loop, .done, .retry
        if (c == '.' && IsIdentifierStart(Peek()))
        {
            _position++; // consume the dot prefix
            while (IsIdentifierPart(Current))
                _position++;
            return (SyntaxKind.LocalLabelToken, Substring(start, _position));
        }

        // Identifiers / keywords
        if (IsIdentifierStart(c))
        {
            while (IsIdentifierPart(Current))
                _position++;
            string word = Substring(start, _position);
            if (Keywords.TryGetValue(word, out var keywordKind))
                return (keywordKind, word);
            return (SyntaxKind.IdentifierToken, word);
        }

        // Macro parameter tokens: \1..\9, \@, \#, \NARG
        // Must be checked before the single-character fallthrough so that \1 is a
        // single two-character token rather than BadToken(\) + NumberLiteral(1).
        if (c == '\\')
        {
            char next = Peek();
            if (next >= '1' && next <= '9')
            {
                _position += 2;
                return (SyntaxKind.MacroParamToken, Substring(start, _position));
            }
            if (next is '@' or '#' or ',')
            {
                _position += 2;
                return (SyntaxKind.MacroParamToken, Substring(start, _position));
            }
            // \NARG — consumed as a single identifier-like token (case-insensitive)
            if ((next is 'N' or 'n') && _position + 5 <= _source.Length
                && _source.ToString(new TextSpan(_position + 1, 4)).Equals("NARG", StringComparison.OrdinalIgnoreCase))
            {
                _position += 5;
                return (SyntaxKind.MacroParamToken, Substring(start, _position));
            }
            // Backslash followed by non-whitespace, non-newline, non-macro-param:
            // invalid line continuation. Report error and consume as BadToken.
            if (next != '\r' && next != '\n' && next != ' ' && next != '\t' && next != '\0' && next != ';')
            {
                _diagnostics.Report(
                    new TextSpan(start, 1),
                    "Invalid character after line continuation backslash");
                _position++;
                return (SyntaxKind.BadToken, "\\");
            }
        }

        // Multi-character punctuation (check before single-char)
        if (c == '<' && Peek() == '<')
        {
            _position += 2;
            return (SyntaxKind.LessThanLessThanToken, "<<");
        }
        if (c == '>' && Peek() == '>' && Peek(2) == '>')
        {
            _position += 3;
            return (SyntaxKind.TripleGreaterThanToken, ">>>");
        }
        if (c == '>' && Peek() == '>')
        {
            _position += 2;
            return (SyntaxKind.GreaterThanGreaterThanToken, ">>");
        }
        if (c == '*' && Peek() == '*')
        {
            _position += 2;
            return (SyntaxKind.StarStarToken, "**");
        }
        if (c == '+' && Peek() == '+')
        {
            _position += 2;
            return (SyntaxKind.PlusPlusToken, "++");
        }
        if (c == '=' && Peek() == '=' && Peek(2) == '=')
        {
            _position += 3;
            return (SyntaxKind.TripleEqualsToken, "===");
        }
        if (c == '!' && Peek() == '=' && Peek(2) == '=')
        {
            _position += 3;
            return (SyntaxKind.BangEqualsEqualsToken, "!==");
        }
        if (c == '=' && Peek() == '=')
        {
            _position += 2;
            return (SyntaxKind.EqualsEqualsToken, "==");
        }
        if (c == '!' && Peek() == '=')
        {
            _position += 2;
            return (SyntaxKind.BangEqualsToken, "!=");
        }
        if (c == '<' && Peek() == '=')
        {
            _position += 2;
            return (SyntaxKind.LessThanEqualsToken, "<=");
        }
        if (c == '>' && Peek() == '=')
        {
            _position += 2;
            return (SyntaxKind.GreaterThanEqualsToken, ">=");
        }
        if (c == '&' && Peek() == '&')
        {
            _position += 2;
            return (SyntaxKind.AmpersandAmpersandToken, "&&");
        }
        if (c == '|' && Peek() == '|')
        {
            _position += 2;
            return (SyntaxKind.PipePipeToken, "||");
        }
        if (c == ':' && Peek() == ':')
        {
            _position += 2;
            return (SyntaxKind.DoubleColonToken, "::");
        }
        // Anonymous label references: :+ :++ :+++ etc. and :- :-- :--- etc.
        if (c == ':' && Peek() == '+')
        {
            _position += 2;
            while (Current == '+') _position++;
            return (SyntaxKind.AnonLabelForwardToken, Substring(start, _position));
        }
        if (c == ':' && Peek() == '-')
        {
            _position += 2;
            while (Current == '-') _position++;
            return (SyntaxKind.AnonLabelBackwardToken, Substring(start, _position));
        }

        // Single-character punctuation
        _position++;
        var result = c switch
        {
            '=' => (SyntaxKind.EqualsToken, "="),
            ',' => (SyntaxKind.CommaToken, ","),
            '(' => (SyntaxKind.OpenParenToken, "("),
            ')' => (SyntaxKind.CloseParenToken, ")"),
            '[' => (SyntaxKind.OpenBracketToken, "["),
            ']' => (SyntaxKind.CloseBracketToken, "]"),
            ':' => (SyntaxKind.ColonToken, ":"),
            '.' => (SyntaxKind.DotToken, "."),
            '#' => (SyntaxKind.HashToken, "#"),
            '+' => (SyntaxKind.PlusToken, "+"),
            '-' => (SyntaxKind.MinusToken, "-"),
            '*' => (SyntaxKind.StarToken, "*"),
            '/' => (SyntaxKind.SlashToken, "/"),
            '%' => (SyntaxKind.PercentToken, "%"),
            '&' => (SyntaxKind.AmpersandToken, "&"),
            '|' => (SyntaxKind.PipeToken, "|"),
            '^' => (SyntaxKind.CaretToken, "^"),
            '~' => (SyntaxKind.TildeToken, "~"),
            '!' => (SyntaxKind.BangToken, "!"),
            '<' => (SyntaxKind.LessThanToken, "<"),
            '>' => (SyntaxKind.GreaterThanToken, ">"),
            '$' => (SyntaxKind.CurrentAddressToken, "$"),
            '@' => (SyntaxKind.AtToken, "@"),
            _ => (SyntaxKind.BadToken, c.ToString()),
        };

        Debug.Assert(_position > start, "Lexer failed to advance position");
        return result;
    }

    private IReadOnlyList<GreenTrivia> ScanLeadingTrivia()
    {
        var trivia = new List<GreenTrivia>();
        while (!IsAtEnd)
        {
            if (Current == ' ' || Current == '\t')
            {
                trivia.Add(ScanWhitespace());
            }
            else if (Current == '/' && Peek() == '*')
            {
                trivia.Add(ScanBlockComment());
            }
            else if (Current == ';')
            {
                trivia.Add(ScanLineComment());
            }
            else if (Current == '\\' && IsLineContinuation())
            {
                trivia.Add(ScanLineContinuation());
            }
            else if (Current == '\r' || Current == '\n')
            {
                trivia.Add(ScanNewline());
            }
            else
            {
                break;
            }
        }
        return trivia;
    }

    private IReadOnlyList<GreenTrivia> ScanTrailingTrivia()
    {
        var trivia = new List<GreenTrivia>();
        while (!IsAtEnd)
        {
            if (Current == ' ' || Current == '\t')
            {
                trivia.Add(ScanWhitespace());
            }
            else if (Current == '/' && Peek() == '*')
            {
                var comment = ScanBlockComment();
                trivia.Add(comment);
                // If the block comment spanned lines, stop here. The newline is
                // embedded in the comment text; the parser cannot see it as a
                // NewlineTrivia node, so we treat the comment as the line
                // terminator for the purposes of statement-boundary detection.
                if (comment.Text.Contains('\n') || comment.Text.Contains('\r'))
                    break;
            }
            else if (Current == ';')
            {
                trivia.Add(ScanLineComment());
            }
            else if (Current == '\\' && IsLineContinuation())
            {
                trivia.Add(ScanLineContinuation());
                // After consuming \<newline>, continue scanning trivia — do NOT break.
                // The next line's tokens are part of the same logical line.
            }
            else if (Current == '\r' || Current == '\n')
            {
                trivia.Add(ScanNewline());
                break; // trailing trivia stops after first newline
            }
            else
            {
                break;
            }
        }
        return trivia;
    }

    private GreenTrivia ScanWhitespace()
    {
        int start = _position;
        while (!IsAtEnd && (Current == ' ' || Current == '\t'))
            _position++;
        return new GreenTrivia(SyntaxKind.WhitespaceTrivia, Substring(start, _position));
    }

    private GreenTrivia ScanLineComment()
    {
        int start = _position;
        while (!IsAtEnd && Current != '\n' && Current != '\r')
            _position++;
        return new GreenTrivia(SyntaxKind.LineCommentTrivia, Substring(start, _position));
    }

    private GreenTrivia ScanBlockComment()
    {
        int start = _position;
        _position += 2; // consume /*
        int depth = 1;

        while (!IsAtEnd && depth > 0)
        {
            if (Current == '/' && Peek() == '*')
            {
                _position += 2;
                depth++;
            }
            else if (Current == '*' && Peek() == '/')
            {
                _position += 2;
                depth--;
            }
            else
            {
                _position++;
            }
        }

        if (depth > 0)
        {
            // Consumed to EOF without finding the closing */
            _diagnostics.Report(
                new TextSpan(start, _position - start),
                "Unterminated block comment"
            );
        }

        return new GreenTrivia(SyntaxKind.BlockCommentTrivia, Substring(start, _position));
    }

    private GreenTrivia ScanNewline()
    {
        int start = _position;
        if (Current == '\r')
        {
            _position++;
            if (!IsAtEnd && Current == '\n')
                _position++;
        }
        else
        {
            _position++;
        }
        return new GreenTrivia(SyntaxKind.NewlineTrivia, Substring(start, _position));
    }

    private string Substring(int start, int end)
    {
        var span = new TextSpan(start, end - start);
        return _source.ToString(span);
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsHexDigit(char c) => char.IsAsciiHexDigit(c);

    private static bool IsBinaryDigit(char c) => c is '0' or '1';

    private static bool IsOctalDigit(char c) => c is >= '0' and <= '7';

    /// <summary>
    /// Scans digits (validated by <paramref name="isDigit"/>) with optional underscore
    /// separators. Reports diagnostics for double underscores and trailing underscores.
    /// </summary>
    private void ScanDigitsWithUnderscores(int literalStart, Func<char, bool> isDigit)
    {
        bool lastWasUnderscore = false;
        while (!IsAtEnd && (isDigit(Current) || Current == '_'))
        {
            if (Current == '_')
            {
                if (lastWasUnderscore)
                {
                    _diagnostics.Report(
                        new TextSpan(literalStart, _position - literalStart + 1),
                        "Double underscore in numeric constant");
                }
                lastWasUnderscore = true;
            }
            else
            {
                lastWasUnderscore = false;
            }
            _position++;
        }
        if (lastWasUnderscore)
        {
            _diagnostics.Report(
                new TextSpan(literalStart, _position - literalStart),
                "Trailing underscore in numeric constant");
        }
    }

    /// <summary>
    /// Scans a regular (non-raw) string literal starting at the opening quote.
    /// Handles \&lt;newline&gt; as line continuation (string continues on next line).
    /// </summary>
    private (SyntaxKind kind, string text) ScanString(int start)
    {
        _position++; // opening quote
        while (!IsAtEnd && Current != '"')
        {
            if (Current == '\\')
            {
                _position++; // skip past backslash
                if (!IsAtEnd && (Current == '\r' || Current == '\n'))
                {
                    // Line continuation in string: consume newline and continue
                    if (Current == '\r') _position++;
                    if (!IsAtEnd && Current == '\n') _position++;
                    continue;
                }
                if (!IsAtEnd) _position++; // skip escaped char
                continue;
            }
            // Unescaped newline terminates the string (unterminated string)
            if (Current == '\n' || Current == '\r')
                break;
            _position++;
        }
        if (Current == '"')
            _position++; // closing quote
        return (SyntaxKind.StringLiteral, Substring(start, _position));
    }

    /// <summary>
    /// Scans a raw string literal: #"..." or #"""...""".
    /// Backslashes are not treated as escape characters.
    /// </summary>
    private (SyntaxKind kind, string text) ScanRawString(int start)
    {
        _position++; // consume #
        _position++; // consume first "

        // Check for triple-quote: #"""..."""
        if (Current == '"' && Peek() == '"')
        {
            _position += 2; // consume second and third opening quotes
            // Scan until closing """
            while (!IsAtEnd)
            {
                if (Current == '"' && Peek() == '"' && Peek(2) == '"')
                {
                    _position += 3; // consume closing """
                    return (SyntaxKind.StringLiteral, Substring(start, _position));
                }
                _position++;
            }
            // Unterminated triple-quote raw string
            _diagnostics.Report(
                new TextSpan(start, _position - start),
                "Unterminated raw string literal");
            return (SyntaxKind.StringLiteral, Substring(start, _position));
        }

        // Single-quote raw string: #"..."
        while (!IsAtEnd && Current != '"' && Current != '\n' && Current != '\r')
        {
            _position++; // no escape processing
        }
        if (Current == '"')
            _position++; // closing quote
        return (SyntaxKind.StringLiteral, Substring(start, _position));
    }

    /// <summary>
    /// Returns true if the current position is a backslash followed by optional
    /// whitespace/comment and then a newline — i.e. a valid line continuation.
    /// </summary>
    private bool IsLineContinuation()
    {
        int i = _position + 1; // skip the backslash
        while (i < _source.Length && (_source[i] == ' ' || _source[i] == '\t'))
            i++;
        // Allow a line comment before the newline
        if (i < _source.Length && _source[i] == ';')
        {
            while (i < _source.Length && _source[i] != '\n' && _source[i] != '\r')
                i++;
        }
        return i < _source.Length && (_source[i] == '\n' || _source[i] == '\r');
    }

    /// <summary>
    /// Consumes a line continuation: backslash, optional whitespace/comment, and newline.
    /// </summary>
    private GreenTrivia ScanLineContinuation()
    {
        int start = _position;
        _position++; // consume backslash
        // Consume optional whitespace
        while (!IsAtEnd && (Current == ' ' || Current == '\t'))
            _position++;
        // Consume optional line comment
        if (!IsAtEnd && Current == ';')
        {
            while (!IsAtEnd && Current != '\n' && Current != '\r')
                _position++;
        }
        // Consume newline
        if (!IsAtEnd && Current == '\r')
        {
            _position++;
            if (!IsAtEnd && Current == '\n')
                _position++;
        }
        else if (!IsAtEnd && Current == '\n')
        {
            _position++;
        }
        return new GreenTrivia(SyntaxKind.WhitespaceTrivia, Substring(start, _position));
    }

    /// <summary>
    /// Returns true if the last emitted token can end a primary expression.
    /// Used to distinguish the bitwise-AND operator <c>&amp;</c> from the
    /// octal literal prefix <c>&amp;digits</c>.
    /// </summary>
    private bool PrecedingCharIsExpressionEnd(int pos)
    {
        // Use the last token kind for precise disambiguation.
        // When no prior token exists, fall back to character inspection.
        if (_lastTokenKind != SyntaxKind.None)
        {
            return _lastTokenKind switch
            {
                SyntaxKind.NumberLiteral => true,
                SyntaxKind.StringLiteral => true,
                SyntaxKind.IdentifierToken => true,
                SyntaxKind.LocalLabelToken => true,
                SyntaxKind.CurrentAddressToken => true,
                SyntaxKind.CloseParenToken => true,
                SyntaxKind.CloseBracketToken => true,
                // Register keywords can appear as operands in expressions
                >= SyntaxKind.AKeyword and <= SyntaxKind.DeKeyword => true,
                // Condition flag keywords (z, nz, nc)
                SyntaxKind.ZKeyword or SyntaxKind.NzKeyword or SyntaxKind.NcKeyword => true,
                _ => false,
            };
        }

        // Fallback: character-level inspection for first token in stream
        int i = pos - 1;
        while (i >= 0 && (_source[i] == ' ' || _source[i] == '\t'))
            i--;
        if (i < 0)
            return false;
        char prev = _source[i];
        return char.IsLetterOrDigit(prev)
            || prev == '_'
            || prev == ')'
            || prev == ']'
            || prev == '"';
    }
}
