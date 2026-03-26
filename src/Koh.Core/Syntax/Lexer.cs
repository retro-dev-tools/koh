using System.Diagnostics;
using Koh.Core.Syntax.InternalSyntax;
using Koh.Core.Text;

namespace Koh.Core.Syntax;

public sealed class Lexer
{
    private readonly SourceText _source;
    private int _position;

    private static readonly Dictionary<string, SyntaxKind> Keywords =
        new(StringComparer.OrdinalIgnoreCase)
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
            while (IsHexDigit(Current)) _position++;
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        if (c == '%' && IsBinaryDigit(Peek()))
        {
            _position++;
            while (IsBinaryDigit(Current)) _position++;
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        if (c == '&' && IsOctalDigit(Peek()))
        {
            _position++;
            while (IsOctalDigit(Current)) _position++;
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        if (char.IsDigit(c))
        {
            while (char.IsDigit(Current)) _position++;
            return (SyntaxKind.NumberLiteral, Substring(start, _position));
        }

        // String literals
        if (c == '"')
        {
            _position++; // opening quote
            while (!IsAtEnd && Current != '"' && Current != '\n' && Current != '\r')
            {
                if (Current == '\\') _position++; // skip escape char
                _position++;
            }
            if (Current == '"') _position++; // closing quote
            return (SyntaxKind.StringLiteral, Substring(start, _position));
        }

        // Local labels: .loop, .done, .retry
        if (c == '.' && IsIdentifierStart(Peek()))
        {
            _position++; // consume the dot prefix
            while (IsIdentifierPart(Current)) _position++;
            return (SyntaxKind.LocalLabelToken, Substring(start, _position));
        }

        // Identifiers / keywords
        if (IsIdentifierStart(c))
        {
            while (IsIdentifierPart(Current)) _position++;
            string word = Substring(start, _position);
            if (Keywords.TryGetValue(word, out var keywordKind))
                return (keywordKind, word);
            return (SyntaxKind.IdentifierToken, word);
        }

        // Multi-character punctuation (check before single-char)
        if (c == '<' && Peek() == '<') { _position += 2; return (SyntaxKind.LessThanLessThanToken, "<<"); }
        if (c == '>' && Peek() == '>') { _position += 2; return (SyntaxKind.GreaterThanGreaterThanToken, ">>"); }
        if (c == '=' && Peek() == '=') { _position += 2; return (SyntaxKind.EqualsEqualsToken, "=="); }
        if (c == '!' && Peek() == '=') { _position += 2; return (SyntaxKind.BangEqualsToken, "!="); }
        if (c == '<' && Peek() == '=') { _position += 2; return (SyntaxKind.LessThanEqualsToken, "<="); }
        if (c == '>' && Peek() == '=') { _position += 2; return (SyntaxKind.GreaterThanEqualsToken, ">="); }
        if (c == '&' && Peek() == '&') { _position += 2; return (SyntaxKind.AmpersandAmpersandToken, "&&"); }
        if (c == '|' && Peek() == '|') { _position += 2; return (SyntaxKind.PipePipeToken, "||"); }
        if (c == ':' && Peek() == ':') { _position += 2; return (SyntaxKind.DoubleColonToken, "::"); }

        // Single-character punctuation
        _position++;
        var result = c switch
        {
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

    private GreenTrivia ScanNewline()
    {
        int start = _position;
        if (Current == '\r')
        {
            _position++;
            if (!IsAtEnd && Current == '\n') _position++;
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
}
