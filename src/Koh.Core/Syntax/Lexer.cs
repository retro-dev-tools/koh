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
        ["mul"] = SyntaxKind.MulKeyword,
        ["div"] = SyntaxKind.DivFuncKeyword,
        ["pow"] = SyntaxKind.PowKeyword,
        ["log"] = SyntaxKind.LogKeyword,
        ["round"] = SyntaxKind.RoundKeyword,
        ["ceil"] = SyntaxKind.CeilKeyword,
        ["floor"] = SyntaxKind.FloorKeyword,
        ["fmod"] = SyntaxKind.FmodKeyword,
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
            while (IsHexDigit(Current) || Current == '_')
                _position++;
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        if (c == '%' && IsBinaryDigit(Peek()))
        {
            _position++;
            while (IsBinaryDigit(Current) || Current == '_')
                _position++;
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
            while (IsOctalDigit(Current) || Current == '_')
                _position++;
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        if (char.IsDigit(c))
        {
            while (char.IsDigit(Current))
                _position++;
            // Fixed-point literal: digits.digits (e.g. 5.0, 2.5)
            if (Current == '.' && char.IsDigit(Peek()))
            {
                _position++; // consume the dot
                while (char.IsDigit(Current))
                    _position++;
            }
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        // String literals
        if (c == '"')
        {
            _position++; // opening quote
            while (!IsAtEnd && Current != '"' && Current != '\n' && Current != '\r')
            {
                if (Current == '\\')
                    _position++; // skip escape char
                _position++;
            }
            if (Current == '"')
                _position++; // closing quote
            return (SyntaxKind.StringLiteral, Substring(start, _position));
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
        }

        // Multi-character punctuation (check before single-char)
        if (c == '<' && Peek() == '<')
        {
            _position += 2;
            return (SyntaxKind.LessThanLessThanToken, "<<");
        }
        if (c == '>' && Peek() == '>')
        {
            _position += 2;
            return (SyntaxKind.GreaterThanGreaterThanToken, ">>");
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
    /// Returns true if the character immediately before <paramref name="pos"/>
    /// (skipping whitespace/tabs) is one that can end a primary expression:
    /// an identifier/keyword character, a decimal/hex digit, or a closing
    /// bracket, paren, or quote.  This is used to distinguish the bitwise-AND
    /// operator <c>&amp;</c> from the octal literal prefix <c>&amp;digits</c>.
    /// </summary>
    private bool PrecedingCharIsExpressionEnd(int pos)
    {
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
