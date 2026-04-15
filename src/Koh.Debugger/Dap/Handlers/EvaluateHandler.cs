using System.Globalization;
using System.Text.Json;
using Koh.Debugger.Dap.Messages;
using Koh.Emulator.Core.Cpu;

namespace Koh.Debugger.Dap.Handlers;

/// <summary>
/// Evaluates simple expressions in the DAP Watch / REPL: hex literals
/// (<c>$1234</c>, <c>0x1234</c>), decimal literals, register names
/// (<c>A</c>, <c>HL</c>, <c>PC</c>), and symbol names from the loaded .kdbg.
/// </summary>
public sealed class EvaluateHandler
{
    private readonly DebugSession _session;
    public EvaluateHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.EvaluateArguments);
        if (args is null)
            return new Response { Success = false, Message = "evaluate: missing args" };

        string expr = args.Expression.Trim();
        if (expr.Length == 0)
            return new Response { Success = false, Message = "evaluate: empty expression" };

        if (_session.System is not { } gb)
            return new Response { Success = false, Message = "evaluate: no active session" };

        if (TryParseNumericLiteral(expr, out int value))
        {
            return Success(value, kind: "literal");
        }

        if (TryEvaluateRegister(gb, expr, out value))
        {
            return Success(value, kind: "register");
        }

        var sym = _session.DebugInfo.SymbolMap.Lookup(expr);
        if (sym is not null)
        {
            int addr = sym.Address;
            return new Response
            {
                Success = true,
                Body = new EvaluateResponseBody
                {
                    Result = $"${sym.Bank:X2}:${sym.Address:X4}",
                    Type = sym.Kind.ToString(),
                    MemoryReference = "0x" + sym.Address.ToString("X4"),
                },
            };
        }

        return new Response { Success = false, Message = $"evaluate: unknown identifier '{expr}'" };
    }

    private static Response Success(int value, string kind)
    {
        string result = value <= 0xFF
            ? $"${value:X2} ({value})"
            : value <= 0xFFFF
                ? $"${value:X4} ({value})"
                : $"${value:X8} ({value})";
        return new Response
        {
            Success = true,
            Body = new EvaluateResponseBody
            {
                Result = result,
                Type = kind,
                MemoryReference = value <= 0xFFFF ? "0x" + value.ToString("X4") : null,
            },
        };
    }

    private static bool TryParseNumericLiteral(string expr, out int value)
    {
        value = 0;
        if (expr.StartsWith("$"))
            return int.TryParse(expr[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (expr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(expr[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (expr.StartsWith("%"))
        {
            try { value = Convert.ToInt32(expr[1..], 2); return true; }
            catch { return false; }
        }
        return int.TryParse(expr, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryEvaluateRegister(Emulator.Core.GameBoySystem gb, string name, out int value)
    {
        ref var r = ref gb.Registers;
        value = name.ToUpperInvariant() switch
        {
            "A" => r.A,
            "F" => r.F,
            "B" => r.B,
            "C" => r.C,
            "D" => r.D,
            "E" => r.E,
            "H" => r.H,
            "L" => r.L,
            "AF" => r.AF,
            "BC" => r.BC,
            "DE" => r.DE,
            "HL" => r.HL,
            "SP" => r.Sp,
            "PC" => r.Pc,
            "IME" => gb.Cpu.Ime ? 1 : 0,
            "IF" => gb.Io.Interrupts.IF,
            "IE" => gb.Io.Interrupts.IE,
            "LY" => gb.Ppu.LY,
            _ => -1,
        };
        return value >= 0;
    }
}
