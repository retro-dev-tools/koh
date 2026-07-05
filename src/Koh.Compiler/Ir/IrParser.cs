using System.Globalization;
using Koh.Compiler.Targets;

namespace Koh.Compiler.Ir;

/// <summary>Thrown when textual IR cannot be parsed.</summary>
public sealed class IrParseException : Exception
{
    public IrParseException(string message) : base(message) { }
}

/// <summary>
/// Parses the textual IR form produced by <see cref="IrPrinter"/> back into an
/// <see cref="IrModule"/>. Parsing is two-pass at module scope (declarations first, then
/// bodies) so calls and global references resolve regardless of order, and per-function it
/// pre-creates blocks so branches and phi back-edges resolve.
/// </summary>
public static class IrParser
{
    public static IrModule Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? moduleName = null;
        var pending = new List<(IrFunction Function, List<string> Body)>();

        // Discover the module name first.
        int i = 0;
        for (; i < lines.Length; i++)
        {
            var toks = Tokenize(lines[i]);
            if (toks.Count == 0) continue;
            if (toks[0].Text == "module")
            {
                if (toks.Count < 2 || !toks[1].IsString)
                    throw new IrParseException("expected: module \"name\"");
                moduleName = toks[1].Text;
                i++;
                break;
            }
            throw new IrParseException($"expected 'module' declaration, got: {lines[i].Trim()}");
        }

        if (moduleName is null)
            throw new IrParseException("missing 'module' declaration");

        var module = new IrModule(moduleName);

        // Pass 1: declarations (globals + function signatures); collect body line ranges.
        for (; i < lines.Length; i++)
        {
            var reader = new TokenReader(Tokenize(lines[i]));
            if (reader.End) continue;

            var head = reader.Peek;
            if (head == "global")
            {
                ParseGlobal(reader, module);
            }
            else if (head == "func" || head == "extern")
            {
                var fn = ParseFunctionHeader(reader, module);
                if (!fn.IsExternal)
                {
                    var body = new List<string>();
                    for (i++; i < lines.Length; i++)
                    {
                        if (Tokenize(lines[i]) is { Count: 1 } t && t[0].Text == "}")
                            break;
                        body.Add(lines[i]);
                    }
                    pending.Add((fn, body));
                }
            }
            else
            {
                throw new IrParseException($"unexpected top-level line: {lines[i].Trim()}");
            }
        }

        // Pass 2: function bodies.
        foreach (var (fn, body) in pending)
            ParseFunctionBody(fn, body, module);

        return module;
    }

    // ---- Declarations ----------------------------------------------------

    private static void ParseGlobal(TokenReader r, IrModule module)
    {
        r.Expect("global");
        var name = AtName(r.Next(), '@');
        r.Expect(":");
        var type = ParseType(r);

        var space = AddressSpace.Default;
        int? bank = null;
        string? section = null;
        byte[]? initializer = null;

        while (!r.End && r.Peek != "=")
        {
            switch (r.Next().Text)
            {
                case "addrspace":
                    r.Expect("(");
                    space = ParseSpace(r.Next().Text);
                    r.Expect(")");
                    break;
                case "bank":
                    r.Expect("(");
                    bank = (int)ParseLong(r.Next().Text);
                    r.Expect(")");
                    break;
                case "section":
                    r.Expect("(");
                    section = r.Next().Text;
                    r.Expect(")");
                    break;
                default:
                    throw new IrParseException($"unexpected token in global '{name}'");
            }
        }

        if (r.Eat("="))
        {
            var bytes = new List<byte>();
            do { bytes.Add((byte)ParseLong(r.Next().Text)); }
            while (r.Eat(","));
            initializer = bytes.ToArray();
        }

        module.Globals.Add(new IrGlobal(name, type, space, bank, section, initializer));
    }

    private static IrFunction ParseFunctionHeader(TokenReader r, IrModule module)
    {
        bool external = r.Eat("extern");
        r.Expect("func");
        var name = AtName(r.Next(), '@');

        r.Expect("(");
        var parameters = new List<IrParameter>();
        if (r.Peek != ")")
        {
            do
            {
                var pName = AtName(r.Next(), '%');
                r.Expect(":");
                var pType = ParseType(r);
                parameters.Add(new IrParameter(pName, pType));
            }
            while (r.Eat(","));
        }
        r.Expect(")");
        r.Expect(":");
        var returnType = ParseType(r);

        int? bank = null;
        if (r.Eat("bank"))
        {
            r.Expect("(");
            bank = (int)ParseLong(r.Next().Text);
            r.Expect(")");
        }
        r.Eat("{");

        var fn = new IrFunction(name, returnType, parameters, bank, external);
        module.Functions.Add(fn);
        return fn;
    }

    // ---- Bodies ----------------------------------------------------------

    private static void ParseFunctionBody(IrFunction fn, List<string> lines, IrModule module)
    {
        var blocks = new Dictionary<string, IrBasicBlock>(StringComparer.Ordinal);
        var env = new Dictionary<string, IrValue>(StringComparer.Ordinal);
        foreach (var p in fn.Parameters)
            env[p.Name!] = p;

        // Pre-create blocks (in order) so branches and phi back-edges resolve.
        foreach (var line in lines)
        {
            var toks = Tokenize(line);
            if (toks is { Count: 2 } t && t[1].Text == ":" && IsBareName(t[0].Text))
            {
                var label = t[0].Text;
                if (!blocks.ContainsKey(label))
                    blocks[label] = fn.AppendBlock(label);
            }
        }

        var builder = new IrBuilder();
        var phiFixups = new List<(PhiInstruction Phi, List<(string? Name, IrValue? Value, IrBasicBlock Block)> Items)>();
        IrBasicBlock? current = null;

        foreach (var line in lines)
        {
            var r = new TokenReader(Tokenize(line));
            if (r.End) continue;

            if (r.Peek is { } h && IsBareName(h) && r.PeekAt(1) == ":")
            {
                current = blocks[h];
                builder.PositionAtEnd(current);
                continue;
            }

            if (current is null)
                throw new IrParseException($"instruction outside any block in '@{fn.Name}': {line.Trim()}");

            ParseInstruction(r, fn, module, builder, blocks, env, phiFixups);
        }

        foreach (var (phi, items) in phiFixups)
            foreach (var (name, value, block) in items)
            {
                var v = value ?? (env.TryGetValue(name!, out var found)
                    ? found
                    : throw new IrParseException($"phi references undefined value %{name} in '@{fn.Name}'"));
                phi.AddIncoming(v, block);
            }
    }

    private static void ParseInstruction(
        TokenReader r,
        IrFunction fn,
        IrModule module,
        IrBuilder builder,
        Dictionary<string, IrBasicBlock> blocks,
        Dictionary<string, IrValue> env,
        List<(PhiInstruction, List<(string?, IrValue?, IrBasicBlock)>)> phiFixups)
    {
        string? resultName = null;
        if (r.Peek is { } first && first.StartsWith('%') && r.PeekAt(1) == "=")
        {
            resultName = first[1..];
            r.Next();
            r.Expect("=");
        }

        var op = r.Next().Text;
        IrInstruction instr;

        switch (op)
        {
            case "add" or "sub" or "mul" or "udiv" or "sdiv" or "urem" or "srem"
                or "and" or "or" or "xor" or "shl" or "lshr" or "ashr":
            {
                var type = ParseType(r);
                var lhs = ParseOperand(r, type, module, env);
                r.Expect(",");
                var rhs = ParseOperand(r, type, module, env);
                instr = builder.Binary(ParseBinaryOp(op), lhs, rhs);
                break;
            }
            case "icmp":
            {
                var pred = ParseCompareOp(r.Next().Text);
                var type = ParseType(r);
                var lhs = ParseOperand(r, type, module, env);
                r.Expect(",");
                var rhs = ParseOperand(r, type, module, env);
                instr = builder.Compare(pred, lhs, rhs);
                break;
            }
            case "zext" or "sext" or "trunc":
            {
                var srcType = ParseType(r);
                var val = ParseOperand(r, srcType, module, env);
                r.Expect("to");
                var dstType = ParseType(r);
                instr = builder.Conv(ParseConvOp(op), val, dstType);
                break;
            }
            case "alloca":
                instr = builder.Alloca(ParseType(r));
                break;
            case "load":
            {
                var pType = ParseType(r);
                var ptr = ParseOperand(r, pType, module, env);
                instr = builder.Load(ptr);
                break;
            }
            case "store":
            {
                var vType = ParseType(r);
                var val = ParseOperand(r, vType, module, env);
                r.Expect(",");
                var pType = ParseType(r);
                var ptr = ParseOperand(r, pType, module, env);
                instr = builder.Store(val, ptr);
                break;
            }
            case "gep":
            {
                var elemType = ParseType(r);
                r.Expect(",");
                var pType = ParseType(r);
                var ptr = ParseOperand(r, pType, module, env);
                r.Expect(",");
                var iType = ParseType(r);
                var idx = ParseOperand(r, iType, module, env);
                instr = builder.Gep(ptr, idx, elemType);
                break;
            }
            case "call":
            {
                ParseType(r); // return type — recovered from the callee signature
                var calleeName = AtName(r.Next(), '@');
                var callee = module.FindFunction(calleeName)
                    ?? throw new IrParseException($"call to undefined function '@{calleeName}'");
                r.Expect("(");
                var args = new List<IrValue>();
                if (r.Peek != ")")
                {
                    do
                    {
                        var aType = ParseType(r);
                        args.Add(ParseOperand(r, aType, module, env));
                    }
                    while (r.Eat(","));
                }
                r.Expect(")");
                instr = builder.Call(callee, args);
                break;
            }
            case "phi":
            {
                var type = ParseType(r);
                var phi = builder.Phi(type);
                var items = new List<(string?, IrValue?, IrBasicBlock)>();
                do
                {
                    r.Expect("[");
                    var valTok = r.Next();
                    string? pendingName = null;
                    IrValue? resolved = null;
                    if (valTok.Text.StartsWith('%'))
                        pendingName = valTok.Text[1..];
                    else if (valTok.Text.StartsWith('@'))
                        resolved = GlobalOperand(valTok.Text, module);
                    else
                        resolved = new IrConstInt(type, ParseLong(valTok.Text));
                    r.Expect(",");
                    var block = ResolveBlock(r.Next().Text, blocks);
                    r.Expect("]");
                    items.Add((pendingName, resolved, block));
                }
                while (r.Eat(","));
                phiFixups.Add((phi, items));
                instr = phi;
                break;
            }
            case "ret":
                if (r.Peek == "void")
                {
                    r.Next();
                    instr = builder.Ret();
                }
                else
                {
                    var type = ParseType(r);
                    instr = builder.Ret(ParseOperand(r, type, module, env));
                }
                break;
            case "br":
                instr = builder.Br(ResolveBlock(r.Next().Text, blocks));
                break;
            case "condbr":
            {
                var cond = ParseOperand(r, IrType.I8, module, env);
                r.Expect(",");
                var ifTrue = ResolveBlock(r.Next().Text, blocks);
                r.Expect(",");
                var ifFalse = ResolveBlock(r.Next().Text, blocks);
                instr = builder.CondBr(cond, ifTrue, ifFalse);
                break;
            }
            case "switch":
            {
                var type = ParseType(r);
                var value = ParseOperand(r, type, module, env);
                r.Expect(",");
                var def = ResolveBlock(r.Next().Text, blocks);
                r.Expect("[");
                var cases = new List<(IrConstInt, IrBasicBlock)>();
                if (r.Peek != "]")
                {
                    do
                    {
                        var caseVal = new IrConstInt(type, ParseLong(r.Next().Text));
                        r.Expect(":");
                        var target = ResolveBlock(r.Next().Text, blocks);
                        cases.Add((caseVal, target));
                    }
                    while (r.Eat(","));
                }
                r.Expect("]");
                instr = builder.Switch(value, def, cases);
                break;
            }
            default:
                throw new IrParseException($"unknown opcode '{op}'");
        }

        if (resultName is not null && instr.Type.Kind != IrTypeKind.Void)
        {
            instr.Name = resultName;
            env[resultName] = instr;
        }
    }

    // ---- Operands & helpers ---------------------------------------------

    private static IrValue ParseOperand(
        TokenReader r, IrType expected, IrModule module, Dictionary<string, IrValue> env)
    {
        var tok = r.Next().Text;
        if (tok.StartsWith('%'))
            return env.TryGetValue(tok[1..], out var v)
                ? v
                : throw new IrParseException($"undefined value {tok}");
        if (tok.StartsWith('@'))
            return GlobalOperand(tok, module);
        return new IrConstInt(expected, ParseLong(tok));
    }

    private static IrGlobalRef GlobalOperand(string token, IrModule module)
    {
        var name = token[1..];
        var g = module.FindGlobal(name)
            ?? throw new IrParseException($"reference to undefined global '@{name}'");
        return new IrGlobalRef(g);
    }

    private static IrBasicBlock ResolveBlock(string name, Dictionary<string, IrBasicBlock> blocks) =>
        blocks.TryGetValue(name, out var b)
            ? b
            : throw new IrParseException($"reference to undefined block '{name}'");

    private static IrType ParseType(TokenReader r)
    {
        var type = ParseAtomType(r);
        var pendingSpace = AddressSpace.Default;
        bool hasPending = false;
        while (true)
        {
            // Only consume an 'addrspace(...)' clause when it qualifies a pointer (a '*' follows
            // the four tokens 'addrspace' '(' <space> ')'). Otherwise it belongs to an enclosing
            // declaration (e.g. a global's address-space clause) and must be left in place.
            if (r.Peek == "addrspace" && r.PeekAt(4) == "*")
            {
                r.Next();
                r.Expect("(");
                pendingSpace = ParseSpace(r.Next().Text);
                r.Expect(")");
                hasPending = true;
            }
            else if (r.Peek == "*")
            {
                r.Next();
                type = IrType.Pointer(type, hasPending ? pendingSpace : AddressSpace.Default);
                pendingSpace = AddressSpace.Default;
                hasPending = false;
            }
            else
            {
                return type;
            }
        }
    }

    private static IrType ParseAtomType(TokenReader r)
    {
        var tok = r.Next().Text;
        if (tok == "void") return IrType.Void;
        if (tok == "[")
        {
            int length = (int)ParseLong(r.Next().Text);
            var x = r.Next().Text;
            if (x != "x") throw new IrParseException($"expected 'x' in array type, got '{x}'");
            var element = ParseType(r);
            r.Expect("]");
            return IrType.Array(element, length);
        }
        if (tok.Length >= 2 && tok[0] == 'i' && tok[1..].All(char.IsDigit))
            return IrType.Int(int.Parse(tok[1..], CultureInfo.InvariantCulture));
        throw new IrParseException($"expected a type, got '{tok}'");
    }

    private static AddressSpace ParseSpace(string name) =>
        Enum.TryParse<AddressSpace>(name, ignoreCase: true, out var s)
            ? s
            : throw new IrParseException($"unknown address space '{name}'");

    private static IrBinaryOp ParseBinaryOp(string op) => op switch
    {
        "add" => IrBinaryOp.Add,
        "sub" => IrBinaryOp.Sub,
        "mul" => IrBinaryOp.Mul,
        "udiv" => IrBinaryOp.UDiv,
        "sdiv" => IrBinaryOp.SDiv,
        "urem" => IrBinaryOp.URem,
        "srem" => IrBinaryOp.SRem,
        "and" => IrBinaryOp.And,
        "or" => IrBinaryOp.Or,
        "xor" => IrBinaryOp.Xor,
        "shl" => IrBinaryOp.Shl,
        "lshr" => IrBinaryOp.LShr,
        "ashr" => IrBinaryOp.AShr,
        _ => throw new IrParseException($"unknown binary op '{op}'"),
    };

    private static IrCompareOp ParseCompareOp(string op) =>
        Enum.TryParse<IrCompareOp>(op, ignoreCase: true, out var c)
            ? c
            : throw new IrParseException($"unknown compare predicate '{op}'");

    private static IrConvOp ParseConvOp(string op) => op switch
    {
        "trunc" => IrConvOp.Trunc,
        "zext" => IrConvOp.ZExt,
        "sext" => IrConvOp.SExt,
        _ => throw new IrParseException($"unknown conversion '{op}'"),
    };

    private static long ParseLong(string text)
    {
        bool neg = text.StartsWith('-');
        var body = neg ? text[1..] : text;
        long value = body.StartsWith("0x") || body.StartsWith("0X")
            ? Convert.ToInt64(body[2..], 16)
            : long.Parse(body, CultureInfo.InvariantCulture);
        return neg ? -value : value;
    }

    private static string AtName(Token token, char sigil)
    {
        if (!token.Text.StartsWith(sigil))
            throw new IrParseException($"expected name starting with '{sigil}', got '{token.Text}'");
        return token.Text[1..];
    }

    private static bool IsBareName(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_')
        && !text.StartsWith('%') && !text.StartsWith('@');

    // ---- Lexing ----------------------------------------------------------

    private readonly record struct Token(string Text, bool IsString);

    private sealed class TokenReader
    {
        private readonly List<Token> _tokens;
        private int _pos;

        public TokenReader(List<Token> tokens) => _tokens = tokens;

        public bool End => _pos >= _tokens.Count;
        public string? Peek => _pos < _tokens.Count ? _tokens[_pos].Text : null;
        public string? PeekAt(int offset) =>
            _pos + offset < _tokens.Count ? _tokens[_pos + offset].Text : null;

        public Token Next() => _pos < _tokens.Count
            ? _tokens[_pos++]
            : throw new IrParseException("unexpected end of line");

        public bool Eat(string text)
        {
            if (Peek == text) { _pos++; return true; }
            return false;
        }

        public string Expect(string text)
        {
            if (Peek != text)
                throw new IrParseException($"expected '{text}', got '{Peek ?? "<eol>"}'");
            return Next().Text;
        }
    }

    private static List<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c is ' ' or '\t') { i++; continue; }
            if (c == ';') break; // comment to end of line

            if (c == '"')
            {
                int j = i + 1;
                while (j < line.Length && line[j] != '"') j++;
                tokens.Add(new Token(line[(i + 1)..j], IsString: true));
                i = j + 1;
                continue;
            }

            if ("{}()[]:,*=".IndexOf(c) >= 0)
            {
                tokens.Add(new Token(c.ToString(), IsString: false));
                i++;
                continue;
            }

            int start = i;
            if (c is '%' or '@') i++;
            else if (c == '-') i++;
            while (i < line.Length && IsWordChar(line[i])) i++;
            tokens.Add(new Token(line[start..i], IsString: false));
        }
        return tokens;
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '.';
}
