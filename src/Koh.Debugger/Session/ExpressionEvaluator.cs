using Koh.Emulator.Core;

namespace Koh.Debugger.Session;

/// <summary>
/// Minimal condition-expression evaluator for breakpoint `condition` strings.
/// Supports:
///   &lt;operand&gt; &lt;op&gt; &lt;operand&gt;
/// where operand is one of:
///   - 8-bit CPU register: A, F, B, C, D, E, H, L
///   - 16-bit register pair: AF, BC, DE, HL, SP, PC
///   - Memory deref: [addr], [HL], [BC], [DE]
///   - Literal: decimal (42), hex ($1A or 0x1A)
/// and op is one of: ==, !=, &lt;, &lt;=, &gt;, &gt;=
/// Returns false on parse failure so the breakpoint does not halt on malformed
/// conditions (matches VS Code behaviour for most debug adapters).
/// </summary>
public static class ExpressionEvaluator
{
    public static bool Evaluate(string expression, GameBoySystem gb)
    {
        try { return TryEvaluate(expression, gb); }
        catch { return false; }
    }

    private static bool TryEvaluate(string expression, GameBoySystem gb)
    {
        string s = expression.Trim();
        // Find the operator. Two-char ops take precedence.
        string[] ops2 = { "==", "!=", "<=", ">=" };
        string[] ops1 = { "<", ">" };
        int opIndex = -1;
        string? op = null;
        foreach (var candidate in ops2)
        {
            int i = s.IndexOf(candidate, StringComparison.Ordinal);
            if (i > 0) { opIndex = i; op = candidate; break; }
        }
        if (op is null)
        {
            foreach (var candidate in ops1)
            {
                int i = s.IndexOf(candidate, StringComparison.Ordinal);
                if (i > 0) { opIndex = i; op = candidate; break; }
            }
        }
        if (op is null) return false;

        int lhs = ReadOperand(s[..opIndex].Trim(), gb);
        int rhs = ReadOperand(s[(opIndex + op.Length)..].Trim(), gb);

        return op switch
        {
            "==" => lhs == rhs,
            "!=" => lhs != rhs,
            "<"  => lhs <  rhs,
            "<=" => lhs <= rhs,
            ">"  => lhs >  rhs,
            ">=" => lhs >= rhs,
            _ => false,
        };
    }

    private static int ReadOperand(string token, GameBoySystem gb)
    {
        if (token.StartsWith('[') && token.EndsWith(']'))
        {
            string inner = token[1..^1].Trim();
            ushort addr = (ushort)ResolveAddress(inner, gb);
            return gb.Mmu.ReadByte(addr);
        }

        switch (token.ToUpperInvariant())
        {
            case "A":  return gb.Registers.A;
            case "F":  return gb.Registers.F;
            case "B":  return gb.Registers.B;
            case "C":  return gb.Registers.C;
            case "D":  return gb.Registers.D;
            case "E":  return gb.Registers.E;
            case "H":  return gb.Registers.H;
            case "L":  return gb.Registers.L;
            case "AF": return gb.Registers.AF;
            case "BC": return gb.Registers.BC;
            case "DE": return gb.Registers.DE;
            case "HL": return gb.Registers.HL;
            case "SP": return gb.Registers.Sp;
            case "PC": return gb.Registers.Pc;
        }

        return ParseLiteral(token);
    }

    private static int ResolveAddress(string token, GameBoySystem gb)
    {
        switch (token.ToUpperInvariant())
        {
            case "HL": return gb.Registers.HL;
            case "BC": return gb.Registers.BC;
            case "DE": return gb.Registers.DE;
        }
        return ParseLiteral(token);
    }

    private static int ParseLiteral(string token)
    {
        if (token.StartsWith('$'))
            return int.Parse(token[1..], System.Globalization.NumberStyles.HexNumber);
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.Parse(token[2..], System.Globalization.NumberStyles.HexNumber);
        return int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
    }
}
