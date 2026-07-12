using System.Collections.Immutable;
using Koh.Compiler.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// Lowers one method body to an IR function. Parameters and locals become <c>alloca</c>s read via
/// <c>load</c> and written via <c>store</c>, so control flow (if/while) only needs br/condbr — no
/// phi construction. Expression types are tracked with C-like rules (8-bit arithmetic stays 8-bit)
/// and drive signed vs. unsigned operation selection — this is Koh's own typing, independent of
/// (and authoritative over) whatever Roslyn would infer. Name/member/call resolution goes through
/// <see cref="CSharpSemantics"/> (<see cref="_semantics"/>) symbol-first: a resolved symbol
/// identifies the exact declaration Roslyn's own binder would pick, checked by identity rather than
/// spelled text, against the very same <c>CsMethod</c>/<c>CsEnum</c>/<c>IrGlobal</c>/... instances
/// the string-keyed tables below hold (both populated by the same declaration passes). Callee/field
/// sites use <see cref="CSharpSemantics.SymOrCandidate"/> rather than the plain <see
/// cref="CSharpSemantics.Sym"/> (Stage-2 P3): Roslyn's own binder rejects some Koh-legal code outright
/// (mixed-width arithmetic in a call argument fails overload resolution; Koh ignores accessibility
/// entirely, so a cross-class `private` reference is `Inaccessible` to Roslyn) — for exactly those two
/// reasons, a resolvable <c>CandidateSymbols</c> entry is accepted in place of a null <c>Symbol</c>.
/// Every such site keeps the pre-migration string-keyed lookup as a fallback for when no symbol or
/// candidate resolves — a detached monomorphized-generic body, no compilation built, or a genuine
/// resolution failure — so an already-working program can only keep working; those fallbacks are
/// load-bearing, not dead code.
/// </summary>
internal sealed class MethodLowerer
{
    private readonly CsMethod _method;
    private readonly BlockSyntax? _body;
    private readonly ArrowExpressionClauseSyntax? _arrow;
    private readonly IReadOnlyDictionary<string, CsMethod> _methods;
    private readonly IReadOnlyDictionary<string, CsEnum> _enums;
    private readonly IReadOnlyDictionary<string, CsStruct> _structs;
    private readonly IReadOnlyDictionary<string, CsClass> _classes;
    private readonly IReadOnlyDictionary<string, (IrGlobal Global, CsType Type)> _globals;
    private readonly IReadOnlyDictionary<string, (CsType Type, long Value)> _moduleConsts;
    private readonly HardwareRegisters _hardware;
    private readonly string _file;
    private readonly IReadOnlyList<(IrGlobal Global, long Value, CsType Type)> _staticInits;
    private readonly IReadOnlyDictionary<
        string,
        (IrGlobal Global, CsType Element, int Length)
    > _moduleArrays;
    private readonly IrBuilder _b = new();
    private readonly Dictionary<string, (IrValue Slot, CsType Type)> _locals = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, (IrValue Address, CsType Element)> _refs = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, (IrValue ArrayPtr, CsType Element, int Length)> _arrays =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IrValue BasePtr, CsStruct Info)> _structLocals = new(
        StringComparer.Ordinal
    );

    // A class local holds a pointer to its heap instance; field access loads that pointer as the base.
    private readonly Dictionary<string, (IrValue Slot, CsClass Info)> _classLocals = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<
        string,
        (IrValue ArrayPtr, CsStruct Info, int Length)
    > _structArrays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CsType Type, long Value)> _consts = new(
        StringComparer.Ordinal
    );
    private readonly Stack<(IrBasicBlock Break, IrBasicBlock Continue)> _loops = new();

    // The enclosing top-level `static class` (from CsMethod.DeclaringClass), if this is one of its
    // static methods. Unqualified calls/fields in the body resolve to `Class.member` first, so a method
    // can reach its siblings by bare name. Null for legacy top-level functions and instance methods.
    private readonly string? _staticClass;

    // Roslyn as a resolution oracle (see CSharpFrontend.Semantics.cs). The intrinsic-recognition sites
    // (Hardware/Gb/Mem/BitConverter — see IsIntrinsicSubject/IsBitConverterSubject) and the enum-member
    // lookup in LowerMemberAccess stay on plain Sym — a bare type-qualifier identifier never produces a
    // resolvable candidate (see each site's own remarks for why). Call/field resolution (LowerCall's
    // symCallee, LowerInstanceCall's symbol parameter, TryGlobal, TryModuleConst, ResolvedFieldName) uses
    // SymOrCandidate (Stage-2 P3) instead, since those sites are exactly where Roslyn's OverloadResolutionFailure/
    // Inaccessible reasons show up on ordinary Koh-legal code. Unresolved-name diagnostic text
    // (BetterUnresolvedMessage) consults DiagnosticsAt, unrelated to either. Every consulting site falls
    // back to the pre-migration string match when neither a symbol nor an acceptable candidate resolves
    // (a detached monomorphized-generic body, no compilation built, or a genuine resolution failure).
    private readonly CSharpSemantics _semantics;

    /// <summary>The Roslyn resolution oracle this instance was constructed with. Exposed for tests
    /// asserting the plumbing wires it through.</summary>
    internal CSharpSemantics Semantics => _semantics;

    // ---- Unresolved-name diagnostics wording via the semantic model --------------------------------
    //
    // Roslyn diagnostic IDs whitelisted to improve one of Koh's own generic "unresolved name" messages
    // (see BetterUnresolvedMessage below): CS0103 (name not found), CS0117/CS1061 (member not found,
    // with/without an extension-method search), CS0246 (type/namespace not found). Never consulted to
    // decide whether lowering fails — only to reword a message after Koh's own lowering already decided
    // to fail (the design's Roslyn diagnostics policy: Koh-legal code is routinely C#-illegal, e.g.
    // CS0266 on `byte c = a + b;`, so Roslyn diagnostics never gate compilation).
    private static readonly ImmutableHashSet<string> UnresolvedNameDiagnosticIds =
        ImmutableHashSet.Create("CS0103", "CS0117", "CS1061", "CS0246");

    /// <summary>When Koh's own lowering is about to report <paramref name="kohMessage"/> because a name
    /// at <paramref name="node"/> failed to resolve, look for a whitelisted Roslyn diagnostic
    /// (<see cref="UnresolvedNameDiagnosticIds"/>) at the same span and, if one exists, use its clearer
    /// message text instead — still reported as a Koh diagnostic through the unchanged <c>Report</c> span
    /// mapping in <c>CSharpFrontend</c> (only the text changes).
    /// Only ever called from an already-failing throw site, so a successful compile never pays for this:
    /// the query (<see cref="CSharpSemantics.DiagnosticsAt"/>) is scoped to <paramref name="node"/>'s own
    /// span, not the whole compilation. Returns <paramref name="kohMessage"/> unchanged for a detached
    /// node (a monomorphized generic instance's body), when no compilation could be built, or when no
    /// whitelisted diagnostic covers the span.</summary>
    private string BetterUnresolvedMessage(SyntaxNode node, string kohMessage)
    {
        foreach (var d in _semantics.DiagnosticsAt(node))
            if (UnresolvedNameDiagnosticIds.Contains(d.Id))
                return KohStyle(d.GetMessage());
        return kohMessage;
    }

    /// <summary>Reformat a Roslyn diagnostic message to read like an existing Koh diagnostic:
    /// <list type="bullet">
    /// <item>strip the synthetic <see cref="CSharpFrontend.WrapperClassName"/> qualifier a nested
    /// top-level static class picks up from being physically nested inside the wrapper (e.g. Roslyn's
    /// "'__KohProgram.Board' does not contain a definition for 'Ghost'" names the user's type as it would
    /// never be spelled in their own source) — the wrapper is an implementation detail the diagnostic
    /// must never leak;</item>
    /// <item>lowercase the leading word (Roslyn's messages are sentence-cased — "The name ..."; Koh's are
    /// not — "unknown identifier ..."; a leading quoted identifier, e.g. CS0117's "'Board' does not ...",
    /// is left as-is, since it's a name, not a sentence start);</item>
    /// <item>ensure a trailing period (Roslyn's usually have none; a few already end in ')' from a
    /// parenthetical aside, which reads fine without one).</item>
    /// </list></summary>
    private static string KohStyle(string roslynMessage)
    {
        if (roslynMessage.Length == 0)
            return roslynMessage;
        var stripped = roslynMessage.Replace(
            CSharpFrontend.WrapperClassName + ".",
            "",
            StringComparison.Ordinal
        );
        var s = char.ToLowerInvariant(stripped[0]) + stripped[1..];
        return s.EndsWith('.') || s.EndsWith(')') ? s : s + ".";
    }

    public MethodLowerer(
        CsMethod method,
        BlockSyntax? body,
        ArrowExpressionClauseSyntax? arrow,
        IReadOnlyDictionary<string, CsMethod> methods,
        IReadOnlyDictionary<string, CsEnum> enums,
        IReadOnlyDictionary<string, CsStruct> structs,
        IReadOnlyDictionary<string, (IrGlobal Global, CsType Type)> globals,
        IReadOnlyDictionary<string, (CsType Type, long Value)> moduleConsts,
        HardwareRegisters hardware,
        string file,
        IReadOnlyList<(IrGlobal Global, long Value, CsType Type)> staticInits,
        IReadOnlyDictionary<string, (IrGlobal Global, CsType Element, int Length)> moduleArrays,
        IReadOnlyDictionary<string, CsClass> classes,
        CSharpSemantics semantics
    )
    {
        _file = file;
        _method = method;
        _body = body;
        _arrow = arrow;
        _methods = methods;
        _enums = enums;
        _structs = structs;
        _classes = classes;
        _globals = globals;
        _moduleConsts = moduleConsts;
        _hardware = hardware;
        _staticInits = staticInits;
        _moduleArrays = moduleArrays;
        _staticClass = method.DeclaringClass;
        _semantics = semantics;
    }

    /// <summary>Resolve a bare callee name to a static method of the enclosing class if one exists,
    /// else leave it as a top-level function name.</summary>
    private string ResolveStaticCallee(string name) =>
        _staticClass is not null && _methods.ContainsKey($"{_staticClass}.{name}")
            ? $"{_staticClass}.{name}"
            : name;

    /// <summary>Look up a static global by simple name, preferring the enclosing static class's member
    /// (Class.name) over an unqualified program-scope global. Symbol-first when <paramref name="node"/>
    /// is given: a resolved field symbol identifies the exact global Roslyn's own member lookup would
    /// pick (which already agrees with the "enclosing class wins" preference below, since C#'s own
    /// scoping rules are the same), consulting <see cref="CSharpSemantics.Globals"/> — the very same
    /// <see cref="IrGlobal"/>/<see cref="CsType"/> instances the string-keyed table holds, since both are
    /// populated by the same declaration-pass registration. Uses <see cref="CSharpSemantics.SymOrCandidate"/>
    /// (Stage-2 P3), not the plain <see cref="CSharpSemantics.Sym"/>, so a cross-class read of a
    /// `private` global — <c>Inaccessible</c> to Roslyn, since Koh itself ignores accessibility — still
    /// resolves symbol-first instead of silently taking the string fallback below. Falls back to the
    /// pre-migration string match when no symbol/candidate resolves (a detached monomorphized-generic
    /// body, no compilation, or a genuine resolution failure).</summary>
    private bool TryGlobal(SyntaxNode? node, string name, out (IrGlobal Global, CsType Type) global)
    {
        if (
            node is not null
            && _semantics.SymOrCandidate(node) is IFieldSymbol fieldSym
            && _semantics.Globals.TryGetValue(fieldSym, out global)
        )
            return true;
        if (_staticClass is not null && _globals.TryGetValue($"{_staticClass}.{name}", out global))
            return true;
        return _globals.TryGetValue(name, out global);
    }

    /// <summary>Look up a module const by simple name, preferring the enclosing static class's member.
    /// Symbol-first when <paramref name="node"/> is given, same shape as <see cref="TryGlobal"/> (including
    /// the candidate-aware <c>Inaccessible</c> acceptance for a cross-class private const read).</summary>
    private bool TryModuleConst(SyntaxNode? node, string name, out (CsType Type, long Value) value)
    {
        if (
            node is not null
            && _semantics.SymOrCandidate(node) is IFieldSymbol fieldSym
            && _semantics.ModuleConsts.TryGetValue(fieldSym, out value)
        )
            return true;
        if (
            _staticClass is not null
            && _moduleConsts.TryGetValue($"{_staticClass}.{name}", out value)
        )
            return true;
        return _moduleConsts.TryGetValue(name, out value);
    }

    /// <summary>Whether <paramref name="subject"/> — the bare identifier naming an intrinsic surface's
    /// static class as the receiver of a member access (e.g. the "Hardware" in <c>Hardware.LCDC</c>) —
    /// actually denotes that intrinsic. Symbol-first: when Roslyn resolves the identifier (it is always
    /// used here as a type qualifier, so resolution yields the type symbol itself), recognition follows
    /// symbol identity against <paramref name="stubType"/> rather than the spelled name — so a
    /// user-declared local/field/type that happens to share the name (e.g. a local variable named
    /// "Hardware" holding some other type) is never mistaken for the intrinsic surface; the resolved
    /// symbol wins. Falls back to the pre-migration exact string match when no symbol resolves at all
    /// (a detached monomorphized-generic body, no compilation built, or resolution failure) — the
    /// fallback only runs when there is nothing to check identity against, so an already-working program
    /// can only keep working, never regress. Deliberately still plain Sym, not SymOrCandidate (Stage-2
    /// P3): `subject` is always a bare type-qualifier identifier naming a public stub type
    /// (<see cref="IntrinsicsStub"/>'s Hardware/Gb/Mem are public by construction), never an overload, so
    /// there is no accessibility or overload-resolution failure this could ever recover from — Roslyn
    /// either binds it outright or the name isn't in scope at all (no CandidateSymbols to consult).</summary>
    private bool IsIntrinsicSubject(
        IdentifierNameSyntax subject,
        string fallbackName,
        INamedTypeSymbol? stubType
    ) =>
        _semantics.Sym(subject) is { } resolved
            ? SymbolEqualityComparer.Default.Equals(resolved, stubType)
            : subject.Identifier.Text == fallbackName;

    /// <summary>Same shape as <see cref="IsIntrinsicSubject"/>, but for <c>BitConverter</c>: there is no
    /// stub type for it (<see cref="IntrinsicsStub"/>'s <c>global using System;</c> makes the bare name
    /// bind to the real BCL <c>System.BitConverter</c>), so the identity check is a namespace+name match
    /// on the resolved type instead of equality against a pre-resolved stub symbol. Same reasoning as
    /// <see cref="IsIntrinsicSubject"/> for staying plain-Sym: a public BCL type named by a bare
    /// qualifier identifier, never an overload — no candidate case to recover.</summary>
    private bool IsBitConverterSubject(IdentifierNameSyntax subject) =>
        _semantics.Sym(subject) is { } resolved
            ? resolved is INamedTypeSymbol { Name: "BitConverter" } bcl
                && bcl.ContainingNamespace?.ToDisplayString() == "System"
            : subject.Identifier.Text == "BitConverter";

    /// <summary>Whether <paramref name="type"/> is the real BCL <c>System.MathF</c> — the trap a
    /// qualified <c>System.MathF.Round(...)</c> call falls into once real BCL references are in the
    /// compilation: `System.MathF` resolves to the actual .NET type (never in <see
    /// cref="CSharpSemantics.Methods"/>, since Koh's own compiled <c>MathF</c> library is a distinct
    /// in-tree declaration that shadows the bare, unqualified name — see <see
    /// cref="IntrinsicsStub"/>'s <c>global using System;</c> remarks). A bare <c>MathF.Round(...)</c> call
    /// needs no special-casing: it binds to Koh's own in-tree <c>MathF</c> class directly, which is an
    /// ordinary registered method reached by the ordinary symbol/Methods lookup in <see
    /// cref="LowerCall"/>.</summary>
    private static bool IsBclMathF(INamedTypeSymbol? type) =>
        type is { Name: "MathF" } && type.ContainingNamespace?.ToDisplayString() == "System";

    public void Lower()
    {
        var entry = _method.Fn.AppendBlock("entry");
        _b.PositionAtEnd(entry);

        // Static data arrays are visible by simple name: a program-scope array (unqualified) everywhere,
        // and a static class's array (Class.name) only inside that class. Index/Length treat them like
        // local arrays, but the base is the global's address (ROM tables or WRAM buffers) not an alloca.
        foreach (var (name, a) in _moduleArrays)
        {
            var (owner, simple) = CSharpFrontend.SplitQualified(name);
            if (owner is null || owner == _staticClass)
                _arrays[simple] = (IrBuilder.GlobalRef(a.Global), a.Element, a.Length);
        }

        // Parameters: a normal one gets a mutable slot seeded with its value; a ref/out parameter
        // arrives as an address, so its "place" is that address itself (reads/writes deref it).
        for (int i = 0; i < _method.Fn.Parameters.Count; i++)
        {
            var p = _method.Fn.Parameters[i];
            // The implicit `this` of an instance method: a pointer to the instance, registered so field
            // access (this.f or bare f) resolves against the class layout.
            if (i == 0 && _method.ThisClass is { } thisClass)
            {
                var thisSlot = _b.Alloca(p.Type);
                _b.Store(p, thisSlot);
                _locals["this"] = (thisSlot, new CsType(IrType.Pointer(IrType.I8), Signed: false));
                _classLocals["this"] = (thisSlot, thisClass);
                continue;
            }
            // A class-instance parameter: the value is the heap pointer. Register it as a class local so
            // field/method access on the argument resolves against the class layout.
            if (_method.ParamClasses?[i] is { } classParam)
            {
                var slot = _b.Alloca(p.Type);
                _b.Store(p, slot);
                _classLocals[p.Name!] = (slot, classParam);
                continue;
            }
            if (_method.ParamStructs[i] is { } structParam)
            {
                _structLocals[p.Name!] = (p, structParam); // the param value is the struct's address
            }
            else if (_method.RefParams[i])
            {
                _refs[p.Name!] = (p, _method.Params[i]);
            }
            else
            {
                var slot = _b.Alloca(p.Type);
                _b.Store(p, slot);
                _locals[p.Name!] = (slot, _method.Params[i]);
            }
        }

        // Entry function only: apply static-field initializers.
        foreach (var (global, value, type) in _staticInits)
            _b.Store(IrBuilder.ConstInt(type.Ir, value), IrBuilder.GlobalRef(global));

        if (_body is { } body)
            foreach (var stmt in body.Statements)
                LowerStatement(stmt);
        else if (_arrow is { } arrow)
            EmitReturn(arrow.Expression);

        // Ensure the final block is terminated (fell off the end).
        if (_b.CurrentBlock.Terminator is null)
        {
            if (_method.Return is { } rt)
                _b.Ret(IrBuilder.ConstInt(rt.Ir, 0));
            else
                _b.Ret();
        }
    }

    // ---- Statements --------------------------------------------------------

    private void LowerStatement(StatementSyntax stmt)
    {
        // Stamp instructions with this statement's source line. The source was wrapped in a
        // one-line `static class {` prefix, so the wrapped 0-based line equals the user's 1-based line.
        _b.CurrentSource = new IrSourceLocation(
            _file,
            (uint)stmt.GetLocation().GetLineSpan().StartLinePosition.Line
        );

        switch (stmt)
        {
            case BlockSyntax block:
                foreach (var s in block.Statements)
                    LowerStatement(s);
                break;

            case LocalDeclarationStatementSyntax local:
                LowerLocalDeclaration(
                    local.Declaration,
                    local.Modifiers.Any(m => m.ValueText == "const")
                );
                break;

            case ExpressionStatementSyntax expr:
                LowerExpression(expr.Expression, expected: null);
                break;

            case IfStatementSyntax ifStmt:
                LowerIf(ifStmt);
                break;

            case WhileStatementSyntax whileStmt:
                LowerWhile(whileStmt);
                break;

            case ForStatementSyntax forStmt:
                LowerFor(forStmt);
                break;

            case DoStatementSyntax doStmt:
                LowerDo(doStmt);
                break;

            case SwitchStatementSyntax switchStmt:
                LowerSwitch(switchStmt);
                break;

            case BreakStatementSyntax:
                if (_loops.Count == 0)
                    throw new CSharpNotSupportedException("'break' outside a loop.");
                _b.Br(_loops.Peek().Break);
                break;

            case ContinueStatementSyntax:
                if (_loops.Count == 0)
                    throw new CSharpNotSupportedException("'continue' outside a loop.");
                _b.Br(_loops.Peek().Continue);
                break;

            case ReturnStatementSyntax ret:
                if (ret.Expression is { } value)
                    EmitReturn(value);
                else
                    _b.Ret();
                break;

            default:
                throw new CSharpNotSupportedException(
                    $"unsupported statement '{stmt.Kind()}'.",
                    stmt.GetLocation()
                );
        }
    }

    private void LowerLocalDeclaration(VariableDeclarationSyntax decl, bool isConst)
    {
        if (decl.Type is ArrayTypeSyntax arrayType)
        {
            foreach (var v in decl.Variables)
                LowerArrayLocal(v.Identifier.Text, arrayType, v.Initializer);
            return;
        }

        // A struct-typed local: reserve its bytes; fields default to zero (WRAM/emulator-zeroed).
        if (
            decl.Type is IdentifierNameSyntax typeName
            && _structs.TryGetValue(typeName.Identifier.Text, out var structInfo)
        )
        {
            foreach (var v in decl.Variables)
                _structLocals[v.Identifier.Text] = (
                    _b.Alloca(IrType.Array(IrType.I8, structInfo.Size)),
                    structInfo
                );
            return;
        }

        // A class-typed local: a slot holding a pointer to the heap instance (e.g. from `new C()`).
        if (
            decl.Type is IdentifierNameSyntax className
            && _classes.TryGetValue(className.Identifier.Text, out var classInfo)
        )
        {
            foreach (var v in decl.Variables)
            {
                var slot = _b.Alloca(IrType.Pointer(IrType.I8));
                _classLocals[v.Identifier.Text] = (slot, classInfo);
                if (v.Initializer is { } init)
                    _b.Store(
                        _b.Conv(
                            IrConvOp.Bitcast,
                            LowerExpression(init.Value, null).Item1,
                            IrType.Pointer(IrType.I8)
                        ),
                        slot
                    );
            }
            return;
        }

        var type = CSharpFrontend.ResolveType(decl.Type, _enums);
        foreach (var v in decl.Variables)
        {
            if (isConst)
            {
                if (v.Initializer is null)
                    throw new CSharpNotSupportedException(
                        $"const '{v.Identifier.Text}' needs an initializer."
                    );
                _consts[v.Identifier.Text] = (
                    type,
                    CSharpFrontend.ConstEval(
                        v.Initializer.Value,
                        ResolveConst,
                        unsigned: !type.Signed
                    )
                );
                continue;
            }

            var slot = _b.Alloca(type.Ir);
            _locals[v.Identifier.Text] = (slot, type);
            if (v.Initializer is { } init)
                _b.Store(
                    init.Value is StackAllocArrayCreationExpressionSyntax sa
                        ? LowerStackAlloc(sa)
                        : Coerce(LowerExpression(init.Value, type), type),
                    slot
                );
        }
    }

    /// <summary>Lower <c>stackalloc T[n]</c>: reserve <c>n</c> elements in the frame and yield a
    /// <c>T*</c> to the first, so the local is a plain pointer (like the raw address the ROM uses).</summary>
    private IrValue LowerStackAlloc(StackAllocArrayCreationExpressionSyntax sa)
    {
        if (sa.Type is not ArrayTypeSyntax arr)
            throw new CSharpNotSupportedException("stackalloc requires an array type.");
        var element = CSharpFrontend.ResolveType(arr.ElementType, _enums);
        int length = (int)CSharpFrontend.ConstEval(arr.RankSpecifiers[0].Sizes[0], ResolveConst);
        if (length < 0)
            throw new CSharpNotSupportedException($"stackalloc has a negative length ({length}).");
        var storage = _b.Alloca(IrType.Array(element.Ir, length));
        return _b.Gep(storage, IrBuilder.ConstInt(IrType.I16, 0), element.Ir);
    }

    /// <summary>Resolve a bare name to a constant value (local const), for constant folding.</summary>
    private long? ResolveConst(string name) =>
        _consts.TryGetValue(name, out var c) ? c.Value : null;

    private void LowerArrayLocal(
        string name,
        ArrayTypeSyntax arrayType,
        EqualsValueClauseSyntax? initializer
    )
    {
        // An array of structs (`Sprite[] s = new Sprite[n]`) reserves n * structSize bytes; elements
        // are accessed as `s[i].field`. It must be sized with `new T[n]` (there are no struct literals).
        if (
            arrayType.ElementType is IdentifierNameSyntax structName
            && _structs.TryGetValue(structName.Identifier.Text, out var structElem)
        )
        {
            int count = initializer?.Value switch
            {
                ArrayCreationExpressionSyntax { Initializer: null } c => (int)
                    CSharpFrontend.ConstEval(c.Type.RankSpecifiers[0].Sizes[0], ResolveConst),
                _ => throw new CSharpNotSupportedException(
                    $"struct array '{name}' must be created with 'new {structName.Identifier.Text}[n]'."
                ),
            };
            if (count < 0)
                throw new CSharpNotSupportedException(
                    $"array '{name}' has a negative length ({count})."
                );
            var basePtr = _b.Alloca(IrType.Array(IrType.I8, structElem.Size * count));
            _structArrays[name] = (basePtr, structElem, count);
            return;
        }

        var element = CSharpFrontend.ResolveType(arrayType.ElementType, _enums);

        // A string literal initializes a byte array with its characters' codes (`byte[] s = "HI"`).
        if (initializer?.Value is LiteralExpressionSyntax { Token.Value: string text })
        {
            var strPtr = _b.Alloca(IrType.Array(element.Ir, text.Length));
            _arrays[name] = (strPtr, element, text.Length);
            for (int i = 0; i < text.Length; i++)
                _b.Store(
                    IrBuilder.ConstInt(element.Ir, (byte)text[i]),
                    _b.Gep(strPtr, IrBuilder.ConstInt(IrType.I16, i), element.Ir)
                );
            return;
        }

        List<ExpressionSyntax>? elements = null;
        int length;

        switch (initializer?.Value)
        {
            case ArrayCreationExpressionSyntax create:
                if (create.Initializer is { } listInit)
                {
                    elements = listInit.Expressions.ToList();
                    length = elements.Count;
                }
                else
                {
                    var size = create.Type.RankSpecifiers[0].Sizes[0];
                    length = (int)CSharpFrontend.ConstEval(size, ResolveConst);
                }
                break;
            case InitializerExpressionSyntax bare: // byte[] a = { 1, 2, 3 };
                elements = bare.Expressions.ToList();
                length = elements.Count;
                break;
            default:
                throw new CSharpNotSupportedException(
                    $"array '{name}' needs a size or initializer."
                );
        }

        if (length < 0)
            throw new CSharpNotSupportedException(
                $"array '{name}' has a negative length ({length})."
            );
        var arrayPtr = _b.Alloca(IrType.Array(element.Ir, length));
        _arrays[name] = (arrayPtr, element, length);

        if (elements is not null)
            for (int i = 0; i < elements.Count; i++)
            {
                var slot = _b.Gep(arrayPtr, IrBuilder.ConstInt(IrType.I16, i), element.Ir);
                _b.Store(Coerce(LowerExpression(elements[i], element), element), slot);
            }
    }

    /// <summary>Compute the pointer to <c>arr[index]</c> or <c>ptr[index]</c> (the latter is
    /// <c>*(ptr + index)</c>, so a pointer local can be indexed like an array).</summary>
    private (IrValue Pointer, CsType Element) ArrayElementPointer(
        ElementAccessExpressionSyntax access
    )
    {
        if (access.Expression is IdentifierNameSyntax id)
        {
            var (index, _) = LowerExpression(
                access.ArgumentList.Arguments[0].Expression,
                CsType.U16
            );
            if (_arrays.TryGetValue(id.Identifier.Text, out var arr))
                return (_b.Gep(arr.ArrayPtr, index, arr.Element.Ir), arr.Element);
            if (
                _locals.TryGetValue(id.Identifier.Text, out var local)
                && local.Type.Ir.Kind == IrTypeKind.Pointer
            )
            {
                var elementIr = Pointee(local.Type);
                return (
                    _b.Gep(_b.Load(local.Slot), index, elementIr),
                    new CsType(elementIr, Signed: false)
                );
            }
        }
        throw new CSharpNotSupportedException("indexing requires an array or pointer variable.");
    }

    /// <summary>An assignable member: a struct field or a hardware register.</summary>
    private (IrValue Pointer, CsType Type)? MemberPointer(MemberAccessExpressionSyntax member)
    {
        if (StructFieldPointer(member) is { } field)
            return field;
        if (
            member.Expression is IdentifierNameSyntax subject
            && IsIntrinsicSubject(subject, "Hardware", _semantics.HardwareType)
            && _hardware.IsRegister(member.Name.Identifier.Text)
        )
            return (
                IrBuilder.GlobalRef(_hardware.Register(member.Name.Identifier.Text)),
                CsType.U8
            );
        return null;
    }

    /// <summary>Compute the pointer to a struct field: <c>s.field</c> on a struct local, or
    /// <c>arr[i].field</c> on an element of a struct array.</summary>
    private (IrValue Pointer, CsType Type)? StructFieldPointer(MemberAccessExpressionSyntax member)
    {
        if (StructBaseOf(member.Expression) is not { } b)
            return null;

        var fieldName = ResolvedFieldName(member, b.Info, member.Name.Identifier.Text);
        foreach (var field in b.Info.Fields)
            if (field.Name == fieldName)
            {
                if (field.Struct is not null)
                    return null; // a struct-typed field is an aggregate base, resolved by StructBaseOf
                // The offset is aligned to the field size, so index = offset / size is exact.
                int index = field.Offset / field.Type.Ir.SizeInBytes;
                return (
                    _b.Gep(b.Base, IrBuilder.ConstInt(IrType.I16, index), field.Type.Ir),
                    field.Type
                );
            }
        throw new CSharpNotSupportedException(
            BetterUnresolvedMessage(member.Name, $"struct has no field '{fieldName}'.")
        );
    }

    /// <summary>The (base pointer, struct layout) a member access reads a field of: a named struct
    /// local (<c>s.field</c>) or an element of a struct array (<c>arr[i].field</c>).</summary>
    private (IrValue Base, CsStruct Info)? StructBaseOf(ExpressionSyntax expr)
    {
        if (
            expr is IdentifierNameSyntax id
            && _structLocals.TryGetValue(id.Identifier.Text, out var s)
        )
            return (s.BasePtr, s.Info);
        // A class local (or `this`): the instance base is the pointer it holds, loaded each access.
        if (ClassLocalOf(expr) is { } c)
            return (Reinterpret(_b.Load(c.Slot), IrType.Pointer(IrType.I8)), c.Info.Layout);
        if (
            expr is ElementAccessExpressionSyntax access
            && access.Expression is IdentifierNameSyntax arrayId
            && _structArrays.TryGetValue(arrayId.Identifier.Text, out var arr)
        )
        {
            var (index, _) = LowerExpression(
                access.ArgumentList.Arguments[0].Expression,
                CsType.U16
            );
            var elementPtr = _b.Gep(arr.ArrayPtr, index, IrType.Array(IrType.I8, arr.Info.Size));
            return (elementPtr, arr.Info);
        }
        // A struct-typed field of another struct, e.g. `e.pos` in `e.pos.x` — recurse into the parent
        // and step to the field's bytes.
        if (
            expr is MemberAccessExpressionSyntax nested
            && StructBaseOf(nested.Expression) is { } parent
        )
        {
            var nestedFieldName = ResolvedFieldName(
                nested,
                parent.Info,
                nested.Name.Identifier.Text
            );
            foreach (var field in parent.Info.Fields)
                if (field.Name == nestedFieldName && field.Struct is { } sub)
                    return (
                        _b.Gep(
                            parent.Base,
                            IrBuilder.ConstInt(IrType.I16, field.Offset),
                            IrType.I8
                        ),
                        sub
                    );
        }
        return null;
    }

    private void LowerIf(IfStatementSyntax ifStmt)
    {
        var cond = Coerce(LowerExpression(ifStmt.Condition, CsType.Bool), CsType.Bool);
        var thenBlock = _method.Fn.AppendBlock("if.then");
        var elseBlock = ifStmt.Else is not null ? _method.Fn.AppendBlock("if.else") : null;
        var endBlock = _method.Fn.AppendBlock("if.end");

        _b.CondBr(cond, thenBlock, elseBlock ?? endBlock);

        _b.PositionAtEnd(thenBlock);
        LowerStatement(ifStmt.Statement);
        if (_b.CurrentBlock.Terminator is null)
            _b.Br(endBlock);

        if (elseBlock is not null)
        {
            _b.PositionAtEnd(elseBlock);
            LowerStatement(ifStmt.Else!.Statement);
            if (_b.CurrentBlock.Terminator is null)
                _b.Br(endBlock);
        }

        _b.PositionAtEnd(endBlock);
    }

    private void LowerWhile(WhileStatementSyntax whileStmt)
    {
        var condBlock = _method.Fn.AppendBlock("while.cond");
        var bodyBlock = _method.Fn.AppendBlock("while.body");
        var endBlock = _method.Fn.AppendBlock("while.end");

        _b.Br(condBlock);
        _b.PositionAtEnd(condBlock);
        _b.CondBr(
            Coerce(LowerExpression(whileStmt.Condition, CsType.Bool), CsType.Bool),
            bodyBlock,
            endBlock
        );

        _b.PositionAtEnd(bodyBlock);
        _loops.Push((endBlock, condBlock));
        LowerStatement(whileStmt.Statement);
        _loops.Pop();
        if (_b.CurrentBlock.Terminator is null)
            _b.Br(condBlock);

        _b.PositionAtEnd(endBlock);
    }

    private void LowerDo(DoStatementSyntax doStmt)
    {
        var bodyBlock = _method.Fn.AppendBlock("do.body");
        var condBlock = _method.Fn.AppendBlock("do.cond");
        var endBlock = _method.Fn.AppendBlock("do.end");

        _b.Br(bodyBlock);
        _b.PositionAtEnd(bodyBlock);
        _loops.Push((endBlock, condBlock));
        LowerStatement(doStmt.Statement);
        _loops.Pop();
        if (_b.CurrentBlock.Terminator is null)
            _b.Br(condBlock);

        _b.PositionAtEnd(condBlock);
        _b.CondBr(
            Coerce(LowerExpression(doStmt.Condition, CsType.Bool), CsType.Bool),
            bodyBlock,
            endBlock
        );

        _b.PositionAtEnd(endBlock);
    }

    private void LowerSwitch(SwitchStatementSyntax sw)
    {
        var (value, valueType) = LowerExpression(sw.Expression, expected: null);
        var endBlock = _method.Fn.AppendBlock("switch.end");
        IrBasicBlock? defaultBlock = null;
        var cases = new List<(IrConstInt, IrBasicBlock)>();
        var sections = new List<(SwitchSectionSyntax Section, IrBasicBlock Block)>();

        foreach (var section in sw.Sections)
        {
            var block = _method.Fn.AppendBlock("switch.case");
            sections.Add((section, block));
            foreach (var label in section.Labels)
            {
                if (label is CaseSwitchLabelSyntax c)
                {
                    var (v, _) = LowerExpression(c.Value, valueType);
                    if (v is not IrConstInt constant)
                        throw new CSharpNotSupportedException(
                            "switch case label must be a constant."
                        );
                    cases.Add((constant, block));
                }
                else if (label is DefaultSwitchLabelSyntax)
                {
                    defaultBlock = block;
                }
            }
        }

        _b.Switch(value, defaultBlock ?? endBlock, cases);

        var continueTarget = _loops.Count > 0 ? _loops.Peek().Continue : endBlock;
        foreach (var (section, block) in sections)
        {
            _b.PositionAtEnd(block);
            _loops.Push((endBlock, continueTarget)); // break -> switch end; continue -> enclosing loop
            foreach (var stmt in section.Statements)
                LowerStatement(stmt);
            _loops.Pop();
            if (_b.CurrentBlock.Terminator is null)
                _b.Br(endBlock);
        }

        _b.PositionAtEnd(endBlock);
    }

    private void LowerFor(ForStatementSyntax forStmt)
    {
        // Initializers.
        if (forStmt.Declaration is { } decl)
            LowerLocalDeclaration(decl, isConst: false);
        foreach (var init in forStmt.Initializers)
            LowerExpression(init, expected: null);

        var condBlock = _method.Fn.AppendBlock("for.cond");
        var bodyBlock = _method.Fn.AppendBlock("for.body");
        var incrBlock = _method.Fn.AppendBlock("for.incr");
        var endBlock = _method.Fn.AppendBlock("for.end");

        _b.Br(condBlock);
        _b.PositionAtEnd(condBlock);
        if (forStmt.Condition is { } cond)
            _b.CondBr(Coerce(LowerExpression(cond, CsType.Bool), CsType.Bool), bodyBlock, endBlock);
        else
            _b.Br(bodyBlock); // for(;;)

        _b.PositionAtEnd(bodyBlock);
        _loops.Push((endBlock, incrBlock)); // continue runs the incrementors
        LowerStatement(forStmt.Statement);
        _loops.Pop();
        if (_b.CurrentBlock.Terminator is null)
            _b.Br(incrBlock);

        _b.PositionAtEnd(incrBlock);
        foreach (var incr in forStmt.Incrementors)
            LowerExpression(incr, expected: null);
        _b.Br(condBlock);

        _b.PositionAtEnd(endBlock);
    }

    private void EmitReturn(ExpressionSyntax value)
    {
        if (_method.Return is not { } rt)
            throw new CSharpNotSupportedException("return with a value from a void method.");
        _b.Ret(Coerce(LowerExpression(value, rt), rt));
    }

    // ---- Expressions -------------------------------------------------------

    /// <summary>Lower an expression, returning its value and Koh C# type. <paramref name="expected"/>
    /// types otherwise-ambiguous literals.</summary>
    private (IrValue Value, CsType Type) LowerExpression(ExpressionSyntax expr, CsType? expected)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax paren:
                return LowerExpression(paren.Expression, expected);

            case LiteralExpressionSyntax lit:
                return LowerLiteral(lit, expected);

            case IdentifierNameSyntax id:
            {
                var name = id.Identifier.Text;
                if (_consts.TryGetValue(name, out var localConst))
                    return (
                        IrBuilder.ConstInt(localConst.Type.Ir, localConst.Value),
                        localConst.Type
                    );
                if (TryModuleConst(id, name, out var moduleConst))
                    return (
                        IrBuilder.ConstInt(moduleConst.Type.Ir, moduleConst.Value),
                        moduleConst.Type
                    );
                if (WritePlace(id) is { } place)
                    return (_b.Load(place.Pointer), place.Type);
                // A class-instance local used as a value: its slot holds the heap pointer (so it can be
                // returned or passed as byte*). Field/method access goes through ClassLocalOf instead.
                if (_classLocals.TryGetValue(name, out var classLocal))
                    return (_b.Load(classLocal.Slot), new CsType(IrType.Pointer(IrType.I8), false));
                throw new CSharpNotSupportedException(
                    BetterUnresolvedMessage(id, $"unknown identifier '{name}'.")
                );
            }

            case ThisExpressionSyntax when _classLocals.TryGetValue("this", out var self):
                // `this` used as a value (e.g. `return this;` or passing the instance to another method):
                // load the instance pointer. Member access on `this` still goes through ClassLocalOf.
                return (_b.Load(self.Slot), new CsType(IrType.Pointer(IrType.I8), false));

            case ElementAccessExpressionSyntax access:
            {
                var (pointer, element) = ArrayElementPointer(access);
                return (_b.Load(pointer), element);
            }

            case MemberAccessExpressionSyntax member:
                return LowerMemberAccess(member, expected);

            case ObjectCreationExpressionSyntax objNew:
                return LowerNew(objNew);

            case CastExpressionSyntax cast:
            {
                var target = CSharpFrontend.ResolveType(cast.Type, _enums);
                return (Coerce(LowerExpression(cast.Expression, target), target), target);
            }

            case PrefixUnaryExpressionSyntax unary:
                return LowerUnary(unary, expected);

            case PostfixUnaryExpressionSyntax post:
                return LowerIncDec(
                    post.Operand,
                    post.Kind() == SyntaxKind.PostIncrementExpression,
                    prefix: false
                );

            case ConditionalExpressionSyntax cond:
                return LowerConditional(cond, expected);

            case BinaryExpressionSyntax binary:
                return LowerBinary(binary, expected);

            case AssignmentExpressionSyntax assign:
                return LowerAssignment(assign);

            case InvocationExpressionSyntax call:
                return LowerCall(call);

            default:
                throw new CSharpNotSupportedException(
                    $"unsupported expression '{expr.Kind()}'.",
                    expr.GetLocation()
                );
        }
    }

    private (IrValue, CsType) LowerLiteral(LiteralExpressionSyntax lit, CsType? expected)
    {
        if (lit.Kind() == SyntaxKind.TrueLiteralExpression)
            return (IrBuilder.ConstInt(IrType.I8, 1), CsType.Bool);
        if (lit.Kind() == SyntaxKind.FalseLiteralExpression)
            return (IrBuilder.ConstInt(IrType.I8, 0), CsType.Bool);

        // A float/double literal folds to its exact IEEE-754 bit pattern on the host (real .NET, so it
        // matches the managed build), carried in an i32/i64 constant. An `expected` float type selects
        // the width (C#'s implicit constant float/double conversions, e.g. `float x = 1.5;`).
        if (lit.Token.Value is float or double)
        {
            double d = Convert.ToDouble(lit.Token.Value);
            bool asF32 =
                expected?.IsFloat == true ? expected.Value.Ir.Bits == 32 : lit.Token.Value is float;
            return asF32
                ? (
                    IrBuilder.ConstInt(IrType.I32, BitConverter.SingleToUInt32Bits((float)d)),
                    CsType.F32
                )
                : (
                    IrBuilder.ConstInt(
                        IrType.I64,
                        unchecked((long)BitConverter.DoubleToUInt64Bits(d))
                    ),
                    CsType.F64
                );
        }

        // A string literal is only valid as a byte-array initializer (handled in LowerArrayLocal);
        // elsewhere `Convert.ToInt64` would throw or silently parse it, so report it cleanly.
        if (
            lit.Token.Value
            is not (long or int or char or byte or short or ushort or uint or sbyte or ulong)
        )
            throw new CSharpNotSupportedException(
                $"a {lit.Kind()} is not a value here (string literals are only allowed as byte[] initializers).",
                lit.GetLocation()
            );

        long value = unchecked((long)Convert.ToUInt64(lit.Token.Value));
        // With no expected type, size the literal to its value so a wide constant isn't truncated
        // to a neighbouring narrow type (e.g. `1000 == x`), and a small one still stays 8-bit. A float
        // `expected` is NOT adopted for an integer literal (that would reinterpret the int as raw float
        // bits) — keep it integer-typed so the caller's Coerce numerically converts it (e.g. `float x = 5`).
        var type = expected is { IsFloat: false } exp
            ? exp
            : value switch
            {
                >= 0 and <= 0xFF => CsType.U8,
                >= 0 and <= 0xFFFF => CsType.U16,
                >= 0 and <= 0xFFFFFFFF => CsType.U32,
                _ => CsType.U64,
            };
        return (IrBuilder.ConstInt(type.Ir, value), type);
    }

    private (IrValue, CsType) LowerUnary(PrefixUnaryExpressionSyntax unary, CsType? expected)
    {
        switch (unary.Kind())
        {
            case SyntaxKind.PreIncrementExpression:
                return LowerIncDec(unary.Operand, increment: true, prefix: true);
            case SyntaxKind.PreDecrementExpression:
                return LowerIncDec(unary.Operand, increment: false, prefix: true);
            case SyntaxKind.AddressOfExpression:
            {
                var address = LvalueAddress(unary.Operand);
                return (address, new CsType(address.Type, Signed: false));
            }
            case SyntaxKind.PointerIndirectionExpression: // *p
            {
                var place = DerefPlace(unary.Operand);
                return (_b.Load(place.Pointer), place.Type);
            }
        }

        var (value, type) = LowerExpression(unary.Operand, expected);
        return unary.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => LowerNegate(value, type, expected),
            SyntaxKind.BitwiseNotExpression => LowerComplement(value, type),
            SyntaxKind.LogicalNotExpression => (
                _b.Binary(IrBinaryOp.Xor, value, IrBuilder.ConstInt(IrType.I8, 1)),
                CsType.Bool
            ),
            SyntaxKind.UnaryPlusExpression => (value, type),
            _ => throw new CSharpNotSupportedException(
                $"unsupported unary operator '{unary.OperatorToken.Text}'."
            ),
        };
    }

    /// <summary>Lower bitwise complement <c>~x</c> as an xor with an all-ones mask of the operand's
    /// width, folding a constant operand. The result keeps the operand's type (width and signedness),
    /// matching how <see cref="InferType"/> sizes it; the backend's per-byte xor masks each byte, so
    /// a <c>-1</c> constant complements every width correctly.</summary>
    private (IrValue, CsType) LowerComplement(IrValue value, CsType type) =>
        value is IrConstInt k
            ? (IrBuilder.ConstInt(type.Ir, ~k.Value), type)
            : (_b.Binary(IrBinaryOp.Xor, value, IrBuilder.ConstInt(type.Ir, -1)), type);

    /// <summary>Lower unary minus. A negated value is signed — C# promotes <c>-x</c> to a signed type —
    /// and a negated literal folds to a signed constant so it sign-extends correctly and can adopt a
    /// wider operand's type in a mixed expression. Without this, <c>-5</c> (from the unsigned literal 5)
    /// would be an unsigned 251, so <c>x &lt; -5</c> would compare against 251 instead of -5.</summary>
    private (IrValue, CsType) LowerNegate(IrValue value, CsType type, CsType? expected)
    {
        // `-x` on a float flips the sign bit (defined for zero/NaN/Inf); route to the runtime rather
        // than integer-negating the IEEE bit pattern.
        if (type.IsFloat)
            return EmitFloatRuntimeCall(
                type.Bits == 64 ? "__f64_neg" : "__f32_neg",
                type,
                (value, type)
            );
        if (value is IrConstInt k)
        {
            long neg = -k.Value;
            CsType t =
                expected is { Signed: true } e && Fits(neg, e) ? e
                : neg is >= -128 and <= 127 ? CsType.I8
                : neg is >= -32768 and <= 32767 ? CsType.I16
                : neg is >= int.MinValue and <= int.MaxValue ? CsType.I32
                : CsType.I64;
            return (IrBuilder.ConstInt(t.Ir, neg), t);
        }
        var signed = type.Signed ? type : new CsType(type.Ir, Signed: true);
        return (_b.Sub(IrBuilder.ConstInt(signed.Ir, 0), value), signed);
    }

    private (IrValue, CsType) LowerIncDec(ExpressionSyntax operand, bool increment, bool prefix)
    {
        if (operand is not IdentifierNameSyntax id || WritePlace(id) is not { } place)
            throw new CSharpNotSupportedException("++/-- requires a variable.");

        var old = _b.Load(place.Pointer);
        // A pointer steps by one element (scaled by the pointee size via gep); an integer by one.
        IrValue updated =
            place.Type.Ir.Kind == IrTypeKind.Pointer
                ? _b.Gep(
                    old,
                    IrBuilder.ConstInt(IrType.I16, increment ? 1 : -1),
                    Pointee(place.Type)
                )
                : _b.Binary(
                    increment ? IrBinaryOp.Add : IrBinaryOp.Sub,
                    old,
                    IrBuilder.ConstInt(place.Type.Ir, 1)
                );
        _b.Store(updated, place.Pointer);
        return (prefix ? updated : old, place.Type);
    }

    /// <summary>An assignable storage location — a local's alloca or a global's address — or null.</summary>
    private (IrValue Pointer, CsType Type)? WritePlace(IdentifierNameSyntax node)
    {
        var name = node.Identifier.Text;
        if (_locals.TryGetValue(name, out var local))
            return (local.Slot, local.Type);
        if (_refs.TryGetValue(name, out var reference))
            return (reference.Address, reference.Element); // ref param: the address is the place
        if (TryGlobal(node, name, out var global))
            return (IrBuilder.GlobalRef(global.Global), global.Type);
        // A class-instance local: its slot holds the heap pointer. Assignment stores a new pointer
        // (reference semantics); reads load the pointer.
        if (name != "this" && _classLocals.TryGetValue(name, out var classLocal))
            return (classLocal.Slot, new CsType(IrType.Pointer(IrType.I8), Signed: false));
        // A bare field reference inside an instance method resolves against `this`. Symbol-first: a
        // resolved field symbol confirms which field of `self`'s own class Roslyn's member lookup would
        // pick (the field-name text always agrees with Koh's own field name today, but this keeps the
        // site consistent with the rest of the migration and protects against any future divergence).
        if (_classLocals.TryGetValue("this", out var self))
        {
            var fieldName = ResolvedFieldName(node, self.Info.Layout, name);
            foreach (var field in self.Info.Layout.Fields)
                if (field.Name == fieldName && field.Struct is null)
                {
                    var basePtr = Reinterpret(_b.Load(self.Slot), IrType.Pointer(IrType.I8));
                    int index = field.Offset / field.Type.Ir.SizeInBytes;
                    return (
                        _b.Gep(basePtr, IrBuilder.ConstInt(IrType.I16, index), field.Type.Ir),
                        field.Type
                    );
                }
        }
        return null;
    }

    /// <summary>The field name a field reference denotes: the symbol Roslyn resolves for <paramref
    /// name="node"/>, if it is a field of <paramref name="info"/>'s own type (struct or class) —
    /// confirmed via <see cref="CSharpSemantics.Structs"/>/<see cref="CSharpSemantics.Classes"/> against
    /// the same <see cref="CsStruct"/>/<see cref="CsClass"/> instance already selected for the receiver
    /// (by the pre-migration, locals-based receiver resolution — out of this migration's scope) — else
    /// <paramref name="fallbackName"/> (the written text, the pre-migration behavior). Uses <see
    /// cref="CSharpSemantics.SymOrCandidate"/> (Stage-2 P3): Koh ignores field accessibility, so
    /// <c>s.field</c> reading a `private` field of another class/struct is exactly Roslyn's
    /// <c>Inaccessible</c> candidate reason — the receiver is already known good (it's <paramref
    /// name="info"/>), so accepting that candidate here can't misattribute the field to an unrelated
    /// type. Never lets a symbol/candidate that resolves to an unrelated type's field override the
    /// receiver Koh already established; that would risk "struct has no field" on a program that lowers
    /// fine today.</summary>
    private string ResolvedFieldName(SyntaxNode node, CsStruct info, string fallbackName)
    {
        if (
            _semantics.SymOrCandidate(node)
                is IFieldSymbol { ContainingType: { } containingType } fieldSym
            && (
                (
                    _semantics.Structs.TryGetValue(containingType, out var s)
                    && ReferenceEquals(s, info)
                )
                || (
                    _semantics.Classes.TryGetValue(containingType, out var c)
                    && ReferenceEquals(c.Layout, info)
                )
            )
        )
            return fieldSym.Name;
        return fallbackName;
    }

    /// <summary>The place denoted by <c>*ptr</c>: the pointer's value is the address to load/store.</summary>
    private (IrValue Pointer, CsType Type) DerefPlace(ExpressionSyntax pointerExpr)
    {
        var (pointerValue, pointerType) = LowerExpression(pointerExpr, expected: null);
        if (pointerType.Ir.Element is not { } element)
            throw new CSharpNotSupportedException("'*' requires a pointer.");
        return (pointerValue, new CsType(element, Signed: false));
    }

    /// <summary>The address of an lvalue, for taking a reference (ref argument or &amp;).</summary>
    private IrValue LvalueAddress(ExpressionSyntax expr) =>
        expr switch
        {
            // A struct value (local, array element, or nested field) is referenced by its base address.
            _ when StructBaseOf(expr) is { } s => s.Base,
            IdentifierNameSyntax id when WritePlace(id) is { } p => p.Pointer,
            ElementAccessExpressionSyntax ea => ArrayElementPointer(ea).Pointer,
            MemberAccessExpressionSyntax ma when MemberPointer(ma) is { } mp => mp.Pointer,
            _ => throw new CSharpNotSupportedException($"cannot take a reference to '{expr}'."),
        };

    /// <summary>Short-circuit <c>&amp;&amp;</c>/<c>||</c> via a result slot and a conditional branch.</summary>
    private (IrValue, CsType) LowerLogical(BinaryExpressionSyntax binary, bool isAnd)
    {
        var result = _b.Alloca(IrType.I8);
        var left = Coerce(LowerExpression(binary.Left, CsType.Bool), CsType.Bool);
        _b.Store(left, result);

        var evalRight = _method.Fn.AppendBlock("logic.rhs");
        var done = _method.Fn.AppendBlock("logic.end");
        if (isAnd)
            _b.CondBr(left, evalRight, done); // && : only evaluate rhs when lhs is true
        else
            _b.CondBr(left, done, evalRight); // || : only evaluate rhs when lhs is false

        _b.PositionAtEnd(evalRight);
        _b.Store(Coerce(LowerExpression(binary.Right, CsType.Bool), CsType.Bool), result);
        _b.Br(done);

        _b.PositionAtEnd(done);
        return (_b.Load(result), CsType.Bool);
    }

    /// <summary>Best-effort static type of an expression without emitting IR, for sizing a result
    /// slot (e.g. a ternary with no expected type). Returns null when the type isn't obvious, and the
    /// caller falls back to a default.</summary>
    private CsType? InferType(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax p:
                return InferType(p.Expression);
            case CastExpressionSyntax cast:
                try
                {
                    return CSharpFrontend.ResolveType(cast.Type, _enums);
                }
                catch (CSharpNotSupportedException)
                {
                    return null;
                }
            case LiteralExpressionSyntax lit
                when lit.Token.Value
                    is long
                        or int
                        or char
                        or byte
                        or short
                        or ushort
                        or uint
                        or sbyte:
                long v = Convert.ToInt64(lit.Token.Value);
                return v switch
                {
                    >= 0 and <= 0xFF => CsType.U8,
                    >= 0 and <= 0xFFFF => CsType.U16,
                    _ => CsType.U32,
                };
            case IdentifierNameSyntax id:
                if (_locals.TryGetValue(id.Identifier.Text, out var local))
                    return local.Type;
                if (_refs.TryGetValue(id.Identifier.Text, out var reference))
                    return reference.Element;
                if (_consts.TryGetValue(id.Identifier.Text, out var c))
                    return c.Type;
                if (TryModuleConst(id, id.Identifier.Text, out var mc))
                    return mc.Type;
                if (TryGlobal(id, id.Identifier.Text, out var g))
                    return g.Type;
                return null;
            case PrefixUnaryExpressionSyntax u:
                return u.Kind() switch
                {
                    SyntaxKind.LogicalNotExpression => CsType.Bool,
                    SyntaxKind.UnaryMinusExpression
                    or SyntaxKind.UnaryPlusExpression
                    or SyntaxKind.BitwiseNotExpression => InferType(u.Operand),
                    _ => null,
                };
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax fn }
                when _methods.TryGetValue(fn.Identifier.Text, out var callee):
                return callee.Return;
            case ConditionalExpressionSyntax nested:
                return CommonInferred(nested.WhenTrue, nested.WhenFalse, nested);
            case BinaryExpressionSyntax bin
                when IsComparison(bin.Kind())
                    || bin.Kind()
                        is SyntaxKind.LogicalAndExpression
                            or SyntaxKind.LogicalOrExpression:
                return CsType.Bool;
            case BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LeftShiftExpression or (int)SyntaxKind.RightShiftExpression
            } sh:
                return InferType(sh.Left); // a shift result follows its left operand
            case BinaryExpressionSyntax bin:
                return (InferType(bin.Left), InferType(bin.Right)) is ({ } bl, { } br)
                    ? CommonType(bl, br, signMatters: false, bin)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>The inferred common type of two branch expressions, or the one that is inferable, or
    /// null when neither is. Used to size a ternary's result slot from its branches.</summary>
    private CsType? CommonInferred(
        ExpressionSyntax whenTrue,
        ExpressionSyntax whenFalse,
        ExpressionSyntax site
    ) =>
        (InferType(whenTrue), InferType(whenFalse)) switch
        {
            ({ } t, { } f) => CommonType(t, f, signMatters: false, site),
            ({ } t, null) => t,
            (null, { } f) => f,
            _ => null,
        };

    private (IrValue, CsType) LowerConditional(ConditionalExpressionSyntax cond, CsType? expected)
    {
        // The result slot must be sized before the branches run, so when there is no expected type
        // infer one from the branches (else a wide branch, e.g. an int, would truncate to the default).
        var type = expected ?? CommonInferred(cond.WhenTrue, cond.WhenFalse, cond) ?? CsType.U16;
        var result = _b.Alloca(type.Ir);
        var c = Coerce(LowerExpression(cond.Condition, CsType.Bool), CsType.Bool);

        var thenBlock = _method.Fn.AppendBlock("cond.then");
        var elseBlock = _method.Fn.AppendBlock("cond.else");
        var done = _method.Fn.AppendBlock("cond.end");
        _b.CondBr(c, thenBlock, elseBlock);

        _b.PositionAtEnd(thenBlock);
        _b.Store(Coerce(LowerExpression(cond.WhenTrue, type), type), result);
        _b.Br(done);

        _b.PositionAtEnd(elseBlock);
        _b.Store(Coerce(LowerExpression(cond.WhenFalse, type), type), result);
        _b.Br(done);

        _b.PositionAtEnd(done);
        return (_b.Load(result), type);
    }

    private (IrValue, CsType) LowerBinary(BinaryExpressionSyntax binary, CsType? expected)
    {
        var kind = binary.Kind();

        if (kind == SyntaxKind.LogicalAndExpression)
            return LowerLogical(binary, isAnd: true);
        if (kind == SyntaxKind.LogicalOrExpression)
            return LowerLogical(binary, isAnd: false);

        if (IsComparison(kind))
        {
            // Lower each operand by its own type (not the outer `expected`, which is the Bool result
            // type and would truncate a literal operand); a common type reconciles the two.
            var leftOp = LowerExpression(binary.Left, expected: null);
            var rightOp = LowerExpression(binary.Right, expected: null);
            // Floating point: route to a softfloat compare (not icmp) — -0.0 == +0.0 and NaN != NaN
            // are wrong under an integer bit compare.
            if (leftOp.Type.IsFloat || rightOp.Type.IsFloat)
                return LowerFloatCompare(kind, leftOp, rightOp, binary);
            leftOp = AdoptConstant(leftOp, rightOp.Type);
            rightOp = AdoptConstant(rightOp, leftOp.Type);
            var (left, lt) = leftOp;
            var (right, rt) = rightOp;
            if (lt.Ir.Kind == IrTypeKind.Pointer || rt.Ir.Kind == IrTypeKind.Pointer)
            {
                // Compare two addresses as unsigned 16-bit integers (icmp stays integer-only).
                var li = Coerce((left, lt), CsType.U16);
                var ri = Coerce((right, rt), CsType.U16);
                return (_b.Compare(CompareOp(kind, signed: false), li, ri), CsType.Bool);
            }
            // Convert both to their common type, then the predicate's signedness follows it — so a
            // mixed comparison like `sbyte < byte` isn't silently governed by the left operand.
            // Ordering needs signedness; equality does not (it is a pure bit test).
            bool ordering =
                kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression);
            var cmpType = CommonType(lt, rt, signMatters: ordering, binary);
            return (
                _b.Compare(
                    CompareOp(kind, cmpType.Signed),
                    Coerce((left, lt), cmpType),
                    Coerce((right, rt), cmpType)
                ),
                CsType.Bool
            );
        }

        // The outer `expected` types int literals for width promotion, but a pointer expectation
        // would mistype an integer literal operand as a pointer — drop it in that case.
        var (l, ltype) = LowerExpression(
            binary.Left,
            expected?.Ir.Kind == IrTypeKind.Pointer ? null : expected
        );

        // Pointer arithmetic (p + i / p - i) lowers to a gep: the index is scaled by the pointee
        // size and widened to the 16-bit address. A plain add would instead try to coerce the index
        // *into* the pointer type, which is not a valid integer conversion and drops the high byte.
        if (
            ltype.Ir.Kind == IrTypeKind.Pointer
            && kind is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression
        )
            return (
                PointerOffset(
                    l,
                    ltype,
                    binary.Right,
                    subtract: kind == SyntaxKind.SubtractExpression
                ),
                ltype
            );

        var (r, rtype) = LowerExpression(binary.Right, ltype);

        // Floating point: a float operand can never take an integer/pointer/shift path — every float
        // op routes to the softfloat runtime. This must precede the shift/arithmetic paths below.
        if (ltype.IsFloat || rtype.IsFloat)
            return LowerFloatBinary(kind, (l, ltype), (r, rtype), binary);

        // Commuted form: i + p. The pointer is on the right; gep from it with the left as the index.
        if (kind == SyntaxKind.AddExpression && rtype.Ir.Kind == IrTypeKind.Pointer)
            return (_b.Gep(r, Coerce((l, ltype), CsType.U16), Pointee(rtype)), rtype);

        // A shift result follows its left operand; the count is an independent operand.
        if (kind is SyntaxKind.LeftShiftExpression or SyntaxKind.RightShiftExpression)
            return (_b.Binary(ArithOp(kind, ltype.Signed), l, Coerce((r, rtype), ltype)), ltype);

        // Otherwise both operands convert to their common type (C-like usual arithmetic
        // conversions), which also selects signed vs. unsigned div/rem and avoids narrowing the
        // wider operand to the left's width. Signedness only affects div/rem (and comparisons).
        (l, ltype) = AdoptConstant((l, ltype), rtype);
        (r, rtype) = AdoptConstant((r, rtype), ltype);
        bool signMatters = kind is SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression;
        var common = CommonType(ltype, rtype, signMatters, binary);
        return (
            _b.Binary(
                ArithOp(kind, common.Signed),
                Coerce((l, ltype), common),
                Coerce((r, rtype), common)
            ),
            common
        );
    }

    // ---- Floating point ---------------------------------------------------
    //
    // `float`/`double` values are IEEE-754 bit patterns carried in i32/i64 IR values (see CsType). They
    // must NEVER flow through an integer IR op: every float arithmetic/comparison/conversion lowers to a
    // call into the softfloat runtime (ordinary Koh-subset C# source, e.g. `__f32_add`, compiled into the
    // same unit). A stray integer op on float bits is a silent wrong result (-0.0 == +0.0, NaN != NaN).

    /// <summary>Emit a call into a softfloat runtime function by name, coercing args to its parameters
    /// and retyping the result. Throws a clear diagnostic if the runtime source was not included.</summary>
    private (IrValue, CsType) EmitFloatRuntimeCall(
        string name,
        CsType resultType,
        params (IrValue Value, CsType Type)[] args
    )
    {
        if (!_methods.TryGetValue(name, out var callee))
        {
            // `double` reaches here via a __f64_* callee the (single-precision) runtime doesn't define.
            if (name.StartsWith("__f64", StringComparison.Ordinal))
                throw new CSharpNotSupportedException(
                    "double is not yet supported in Koh C# (use float)."
                );
            throw new CSharpNotSupportedException(
                $"floating-point support needs the Koh softfloat runtime, but '{name}' is not in the "
                    + "compilation (include the numerics runtime source)."
            );
        }
        if (callee.Params.Count != args.Length)
            throw new CSharpNotSupportedException(
                $"softfloat runtime function '{name}' must take {args.Length} argument(s), but takes "
                    + $"{callee.Params.Count}."
            );
        var callArgs = new List<IrValue>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            // An arg carries a value's raw bits (a float is its IEEE bits); pass it to the runtime's
            // integer parameter as a same-size reinterpret, never the numeric Coerce path.
            var v = args[i].Value;
            var pt = callee.Fn.Parameters[i].Type;
            callArgs.Add(v.Type.StructurallyEquals(pt) ? v : _b.Conv(IrConvOp.Bitcast, v, pt));
        }
        return (_b.Call(callee.Fn, callArgs), resultType);
    }

    /// <summary>Widen/convert a value to a target float type: a same-width float is a no-op; an integer
    /// converts through the runtime (`__i32_to_f32` etc.); float↔float resizes through the runtime.</summary>
    private IrValue CoerceToFloat((IrValue Value, CsType Type) v, CsType target)
    {
        if (v.Type.IsFloat && v.Type.Bits == target.Bits)
            return v.Value;
        if (v.Type.IsFloat) // f32 <-> f64
            return EmitFloatRuntimeCall(
                target.Bits == 64 ? "__f32_to_f64" : "__f64_to_f32",
                target,
                v
            ).Item1;
        // integer -> float: first make the integer exactly i32/i64 (signed or unsigned), then convert.
        var itype = v.Type.Signed
            ? (target.Bits == 64 ? CsType.I64 : CsType.I32)
            : (target.Bits == 64 ? CsType.U64 : CsType.U32);
        var iv = Coerce(v, itype);
        string conv = itype.Signed
            ? (target.Bits == 64 ? "__i64_to_f64" : "__i32_to_f32")
            : (target.Bits == 64 ? "__u64_to_f64" : "__u32_to_f32");
        return EmitFloatRuntimeCall(conv, target, (iv, itype)).Item1;
    }

    /// <summary>Convert a float value to an integer target through the runtime (`__f32_to_i32` etc.),
    /// then resize the integer result to the exact target width/signedness.</summary>
    private IrValue FloatToInt((IrValue Value, CsType Type) v, CsType target)
    {
        bool src64 = v.Type.Bits == 64;
        var wide = target.Signed
            ? (src64 ? CsType.I64 : CsType.I32)
            : (src64 ? CsType.U64 : CsType.U32);
        string conv = src64
            ? (target.Signed ? "__f64_to_i64" : "__f64_to_u64")
            : (target.Signed ? "__f32_to_i32" : "__f32_to_u32");
        var (iv, _) = EmitFloatRuntimeCall(conv, wide, v);
        return Coerce((iv, wide), target);
    }

    /// <summary>Lower a float arithmetic op (`+ - * /`) to a softfloat runtime call.</summary>
    private (IrValue, CsType) LowerFloatBinary(
        SyntaxKind kind,
        (IrValue, CsType) left,
        (IrValue, CsType) right,
        Microsoft.CodeAnalysis.SyntaxNode at
    )
    {
        var ft = (left.Item2.Bits == 64 || right.Item2.Bits == 64) ? CsType.F64 : CsType.F32;
        var lf = CoerceToFloat(left, ft);
        var rf = CoerceToFloat(right, ft);
        string op = kind switch
        {
            SyntaxKind.AddExpression => "add",
            SyntaxKind.SubtractExpression => "sub",
            SyntaxKind.MultiplyExpression => "mul",
            SyntaxKind.DivideExpression => "div",
            _ => throw new CSharpNotSupportedException(
                $"operator is not supported on floating-point values.",
                at.GetLocation()
            ),
        };
        string prefix = ft.Bits == 64 ? "__f64_" : "__f32_";
        return EmitFloatRuntimeCall(prefix + op, ft, (lf, ft), (rf, ft));
    }

    /// <summary>Lower a float comparison to a softfloat runtime call returning a bool.</summary>
    private (IrValue, CsType) LowerFloatCompare(
        SyntaxKind kind,
        (IrValue, CsType) left,
        (IrValue, CsType) right,
        Microsoft.CodeAnalysis.SyntaxNode at
    )
    {
        var ft = (left.Item2.Bits == 64 || right.Item2.Bits == 64) ? CsType.F64 : CsType.F32;
        var lf = CoerceToFloat(left, ft);
        var rf = CoerceToFloat(right, ft);
        string op = kind switch
        {
            SyntaxKind.EqualsExpression => "eq",
            SyntaxKind.NotEqualsExpression => "ne",
            SyntaxKind.LessThanExpression => "lt",
            SyntaxKind.LessThanOrEqualExpression => "le",
            SyntaxKind.GreaterThanExpression => "gt",
            SyntaxKind.GreaterThanOrEqualExpression => "ge",
            _ => throw new CSharpNotSupportedException(
                $"comparison is not supported on floating-point values.",
                at.GetLocation()
            ),
        };
        string prefix = ft.Bits == 64 ? "__f64_" : "__f32_";
        var (result, _) = EmitFloatRuntimeCall(prefix + op, CsType.Bool, (lf, ft), (rf, ft));
        return (result, CsType.Bool);
    }

    /// <summary>If <paramref name="operand"/> is a constant whose value fits <paramref name="other"/>,
    /// retype it to that type. This mirrors C#'s constant conversions: a bare literal (which defaults
    /// to the smallest unsigned type holding it) adopts the signedness/width of the value it is used
    /// with, so `intVar &lt; 1000` stays a signed compare instead of becoming a mixed-sign one.</summary>
    private static (IrValue Value, CsType Type) AdoptConstant(
        (IrValue Value, CsType Type) operand,
        CsType other
    )
    {
        if (
            operand.Value is IrConstInt c
            && other.Ir.Kind == IrTypeKind.Int
            && Fits(c.Value, other)
        )
            return (IrBuilder.ConstInt(other.Ir, c.Value), other);
        return operand;
    }

    private static bool Fits(long value, CsType type)
    {
        int bits = type.Ir.SizeInBits;
        if (type.Signed)
            return value >= -(1L << (bits - 1)) && value <= (1L << (bits - 1)) - 1;
        return value >= 0 && (bits >= 64 || value <= (1L << bits) - 1);
    }

    /// <summary>
    /// The common type two operands convert to, following C-like usual arithmetic conversions over
    /// the Koh numeric types: the wider storage width wins. When the operands' signedness differs
    /// <em>and</em> it affects the result, the pair is promoted to a signed type wide enough to hold
    /// both ranges (so <c>sbyte</c> vs <c>byte</c> becomes a signed <c>short</c>, matching C#). When
    /// no wider signed type exists on the target (a 32-bit unsigned operand mixed with a signed one,
    /// which would need a 64-bit signed type): if the operator's signedness affects the result (divide,
    /// remainder, ordering) that is a diagnostic asking for an explicit cast; otherwise the common width
    /// is used unsigned, since the bits are identical.
    /// </summary>
    private static CsType CommonType(CsType a, CsType b, bool signMatters, ExpressionSyntax site)
    {
        int width = Math.Max(a.Ir.SizeInBits, b.Ir.SizeInBits);
        if (a.Signed == b.Signed)
            return new CsType(IrType.Int(width), a.Signed);

        // Mixed signedness: prefer a signed type wide enough to hold the unsigned operand's range,
        // because the result's signedness governs how a later widening sign- vs. zero-extends it —
        // so `(sbyte)a + (byte)b` must stay signed even though the add's bits don't depend on sign.
        var unsignedOp = a.Signed ? b : a;
        int need = Math.Max(width, unsignedOp.Ir.SizeInBits + 1);
        if (need <= 16)
            return new CsType(IrType.I16, Signed: true);
        if (need <= 32)
            return new CsType(IrType.I32, Signed: true); // e.g. ushort vs sbyte -> signed int
        if (need <= 64)
            return new CsType(IrType.I64, Signed: true); // e.g. uint vs int -> signed long
        if (need <= 128)
            return new CsType(IrType.Int(128), Signed: true); // e.g. ulong vs long -> signed Int128

        // No usable wider signed type exists on the target (128-bit unsigned mixed with signed would
        // need a 256-bit signed type). If the operator's signedness affects the result it needs an
        // explicit cast; otherwise use the common width unsigned, since the bits are identical.
        if (signMatters)
            throw new CSharpNotSupportedException(
                $"mixed signed/unsigned operation on '{a.Ir}' and '{b.Ir}' needs a wider signed type "
                    + "than this target provides; cast one operand explicitly.",
                site.GetLocation()
            );
        return new CsType(IrType.Int(width), Signed: false);
    }

    /// <summary>Offset a pointer by an index expression via a gep (scaled by the pointee size).</summary>
    private IrValue PointerOffset(
        IrValue pointer,
        CsType pointerType,
        ExpressionSyntax indexExpr,
        bool subtract
    )
    {
        var index = Coerce(LowerExpression(indexExpr, CsType.U16), CsType.U16);
        if (subtract)
            index = _b.Binary(IrBinaryOp.Sub, IrBuilder.ConstInt(IrType.I16, 0), index);
        return _b.Gep(pointer, index, Pointee(pointerType));
    }

    private (IrValue, CsType) LowerAssignment(AssignmentExpressionSyntax assign)
    {
        // Whole-struct copy: `a = b` where a is a value-type struct (local or array element) copies its
        // bytes. A class value is a reference: it assigns by copying the pointer, so exclude class
        // locals here and let them fall through to the pointer-store path below.
        if (
            assign.Kind() == SyntaxKind.SimpleAssignmentExpression
            && ClassLocalOf(assign.Left) is null
            && StructBaseOf(assign.Left) is { } dest
        )
        {
            if (StructBaseOf(assign.Right) is not { } src || !ReferenceEquals(src.Info, dest.Info))
                throw new CSharpNotSupportedException(
                    "a struct can only be assigned from another value of the same struct type."
                );
            for (int k = 0; k < dest.Info.Size; k++)
            {
                var from = _b.Gep(src.Base, IrBuilder.ConstInt(IrType.I16, k), IrType.I8);
                var to = _b.Gep(dest.Base, IrBuilder.ConstInt(IrType.I16, k), IrType.I8);
                _b.Store(_b.Load(from), to);
            }
            return (dest.Base, CsType.U8);
        }

        IrValue pointer;
        CsType type;
        if (assign.Left is IdentifierNameSyntax id && WritePlace(id) is { } place)
            (pointer, type) = place;
        else if (assign.Left is ElementAccessExpressionSyntax access)
            (pointer, type) = ArrayElementPointer(access);
        else if (
            assign.Left is MemberAccessExpressionSyntax fieldAccess
            && MemberPointer(fieldAccess) is { } field
        )
            (pointer, type) = field;
        else if (
            assign.Left is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.PointerIndirectionExpression
            } deref
        )
            (pointer, type) = DerefPlace(deref.Operand);
        else
            throw new CSharpNotSupportedException(
                "assignment target must be a variable, array element, struct field, or *pointer."
            );

        var kind = assign.Kind();
        IrValue result;
        if (kind == SyntaxKind.SimpleAssignmentExpression)
        {
            result = Coerce(LowerExpression(assign.Right, type), type);
        }
        else if (
            type.Ir.Kind == IrTypeKind.Pointer
            && kind is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression
        )
        {
            // p += n / p -= n step by whole elements, so lower through a gep like `p + n`.
            var current = _b.Load(pointer);
            result = PointerOffset(
                current,
                type,
                assign.Right,
                subtract: kind == SyntaxKind.SubtractAssignmentExpression
            );
        }
        else if (type.IsFloat)
        {
            // Float compound assignment: compute `x OP y` through the softfloat runtime, never an
            // integer op on the IEEE bit pattern.
            var current = _b.Load(pointer);
            var rhs = LowerExpression(assign.Right, type);
            SyntaxKind binKind = kind switch
            {
                SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
                SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
                _ => throw new CSharpNotSupportedException(
                    "this compound assignment is not supported on floating-point values.",
                    assign.GetLocation()
                ),
            };
            result = LowerFloatBinary(binKind, (current, type), rhs, assign).Item1;
        }
        else if (
            kind
            is SyntaxKind.LeftShiftAssignmentExpression
                or SyntaxKind.RightShiftAssignmentExpression
        )
        {
            // Shift: the count is independent and the result keeps the target's width.
            var current = _b.Load(pointer);
            var amount = Coerce(LowerExpression(assign.Right, type), type);
            result = _b.Binary(CompoundOp(kind, type.Signed), current, amount);
        }
        else
        {
            // `x OP= y` computes `x OP y` in the operands' common type (usual arithmetic conversions),
            // then narrows back to x — so /= and %= match `x = x / y` rather than truncating y first.
            var current = _b.Load(pointer);
            var (rhsVal, rhsType) = LowerExpression(assign.Right, expected: null);
            bool signMatters =
                kind
                is SyntaxKind.DivideAssignmentExpression
                    or SyntaxKind.ModuloAssignmentExpression;
            var common = CommonType(type, rhsType, signMatters, assign);
            var opResult = _b.Binary(
                CompoundOp(kind, common.Signed),
                Coerce((current, type), common),
                Coerce((rhsVal, rhsType), common)
            );
            result = Coerce((opResult, common), type);
        }

        _b.Store(result, pointer);
        return (result, type);
    }

    private (IrValue, CsType) LowerMemberAccess(
        MemberAccessExpressionSyntax member,
        CsType? expected
    )
    {
        // Struct field read.
        if (StructFieldPointer(member) is { } field)
            return (_b.Load(field.Pointer), field.Type);

        if (member.Expression is IdentifierNameSyntax subject)
        {
            // Hardware register read, e.g. Hardware.LCDC.
            if (
                IsIntrinsicSubject(subject, "Hardware", _semantics.HardwareType)
                && _hardware.IsRegister(member.Name.Identifier.Text)
            )
                return (
                    _b.Load(IrBuilder.GlobalRef(_hardware.Register(member.Name.Identifier.Text))),
                    CsType.U8
                );

            // Memory-region base pointer, e.g. Gb.Vram -> a byte* at the region's fixed address.
            if (
                IsIntrinsicSubject(subject, "Gb", _semantics.GbType)
                && _hardware.IsRegion(member.Name.Identifier.Text)
            )
                return (
                    IrBuilder.GlobalRef(_hardware.Region(member.Name.Identifier.Text)),
                    new CsType(IrType.Pointer(IrType.I8), Signed: false)
                );

            // Enum member reference, e.g. Color.Red. Symbol-first: `subject` names the enum type (used
            // here purely as a qualifier, like Hardware/Gb above), so resolution yields the enum's own
            // type symbol; CSharpSemantics.Enums maps it to the very same CsEnum instance the string
            // table holds (both populated by the same CollectEnums pass). The member's VALUE always
            // comes from that CsEnum's folded Members table (ConstEval stays authoritative) — the symbol
            // only identifies which enum is meant, never recomputes the value. Falls back to the exact
            // string match when no symbol resolves (a detached monomorphized-generic body, or no
            // compilation). Deliberately still plain Sym, not SymOrCandidate (Stage-2 P3): an enum's own
            // members can't carry an accessibility modifier in C# (they're always as visible as the enum
            // itself), and `subject` here is a bare, unqualified type-qualifier identifier, never an
            // overload — so Roslyn only ever returns a real Symbol or nothing (a nested `private` enum
            // referenced unqualified by bare name isn't merely inaccessible, it's out of scope entirely:
            // CS0103, no CandidateSymbols at all), leaving no candidate case for SymOrCandidate to add.
            CsEnum? e =
                _semantics.Sym(subject) is INamedTypeSymbol enumType
                && _semantics.Enums.TryGetValue(enumType, out var bySymbol)
                    ? bySymbol
                    : null;
            if (e is null)
                _enums.TryGetValue(subject.Identifier.Text, out e);
            if (e is not null)
            {
                var name = member.Name.Identifier.Text;
                if (!e.Members.TryGetValue(name, out long value))
                    throw new CSharpNotSupportedException(
                        BetterUnresolvedMessage(
                            member.Name,
                            $"enum '{subject.Identifier.Text}' has no member '{name}'."
                        )
                    );
                return (IrBuilder.ConstInt(e.Underlying.Ir, value), e.Underlying);
            }

            // Array length, e.g. arr.Length.
            if (member.Name.Identifier.Text == "Length")
            {
                int? length =
                    _arrays.TryGetValue(subject.Identifier.Text, out var arr) ? arr.Length
                    : _structArrays.TryGetValue(subject.Identifier.Text, out var sarr) ? sarr.Length
                    : null;
                if (length is { } n)
                {
                    var type = expected ?? (n <= 0xFF ? CsType.U8 : CsType.U16);
                    return (IrBuilder.ConstInt(type.Ir, n), type);
                }
            }
        }
        throw new CSharpNotSupportedException(
            BetterUnresolvedMessage(member, $"unsupported member access '{member}'.")
        );
    }

    private (IrValue, CsType) LowerCall(InvocationExpressionSyntax call)
    {
        // Hardware control intrinsics: Hardware.EnableInterrupts(), etc.
        if (
            call.Expression is MemberAccessExpressionSyntax hw
            && hw.Expression is IdentifierNameSyntax hwId
            && IsIntrinsicSubject(hwId, "Hardware", _semantics.HardwareType)
        )
        {
            var intrinsic = hw.Name.Identifier.Text switch
            {
                "EnableInterrupts" => "ei",
                "DisableInterrupts" => "di",
                "Halt" => "halt",
                "Nop" => "nop",
                _ => throw new CSharpNotSupportedException(
                    $"unknown Hardware method '{hw.Name.Identifier.Text}'."
                ),
            };
            return (_b.Intrinsic(intrinsic), CsType.U8);
        }

        // Arena allocator: Mem.Alloc(size) bumps the heap pointer down and returns a byte*; Mem.Reset()
        // frees everything at once by resetting the pointer to the top of the heap.
        if (
            call.Expression is MemberAccessExpressionSyntax mem
            && mem.Expression is IdentifierNameSyntax memId
            && IsIntrinsicSubject(memId, "Mem", _semantics.MemType)
        )
            return LowerMemCall(mem.Name.Identifier.Text, call);

        // BitConverter float<->bits reinterpret (a same-size bitcast, not a numeric conversion), matching
        // System.BitConverter so the managed build agrees. Lets the softfloat/Math library read and build
        // IEEE bit patterns from `float` in pure subset source.
        if (
            call.Expression is MemberAccessExpressionSyntax bc
            && bc.Expression is IdentifierNameSyntax bcId
            && IsBitConverterSubject(bcId)
        )
        {
            if (call.ArgumentList.Arguments.Count != 1)
                throw new CSharpNotSupportedException(
                    $"BitConverter.{bc.Name.Identifier.Text} takes one argument.",
                    call.GetLocation()
                );
            var argExpr = call.ArgumentList.Arguments[0].Expression;
            switch (bc.Name.Identifier.Text)
            {
                case "SingleToUInt32Bits":
                    return (LowerExpression(argExpr, CsType.F32).Item1, CsType.U32);
                case "SingleToInt32Bits":
                    return (LowerExpression(argExpr, CsType.F32).Item1, CsType.I32);
                case "UInt32BitsToSingle":
                    return (LowerExpression(argExpr, CsType.U32).Item1, CsType.F32);
                case "Int32BitsToSingle":
                    return (LowerExpression(argExpr, CsType.I32).Item1, CsType.F32);
                default:
                    throw new CSharpNotSupportedException(
                        $"unsupported BitConverter method '{bc.Name.Identifier.Text}'."
                    );
            }
        }

        // Array LINQ: a Where/Select pipeline ending in a reduction (Sum/Count/Max/Min/Any/All),
        // compiled to a loop with the lambdas inlined.
        if (TryLowerLinq(call) is { } linq)
            return linq;

        // The callee symbol, if the invocation resolves at all — via CSharpSemantics.SymOrCandidate
        // (Stage-2 P3), so a call Roslyn's own overload resolution rejects for a C#-only reason (e.g.
        // `Helper(a + b)` with `byte a, b`: the sum types as C#'s `int`, and there is no user-visible
        // `int`-to-`byte` implicit conversion, even though Koh's own usual-arithmetic-conversion rules
        // accept the call outright) still resolves to the real callee via its lone CandidateSymbols
        // entry, instead of silently falling through to the syntax-based lookup below. Still null for a
        // detached monomorphized-generic body, no compilation, or a genuine resolution failure (no symbol
        // and no acceptable candidate) — every branch below keeps its syntax fallback for exactly those
        // cases. A candidate accepted here for a call with the WRONG argument count is not a problem: the
        // arity check below (`argList.Count != callee.Params.Count`) compares against the call's own
        // syntax argument list, not the candidate's parameter count, so it still fires regardless of
        // which method the candidate identifies.
        var symCallee = _semantics.SymOrCandidate(call) as IMethodSymbol;

        // Instance method call: obj.Method(args) or this.Method(args).
        if (
            call.Expression is MemberAccessExpressionSyntax instCall
            && ClassLocalOf(instCall.Expression) is { } recv
        )
            return LowerInstanceCall(
                recv.Info,
                recv.Slot,
                instCall.Name.Identifier.Text,
                call.ArgumentList.Arguments,
                symCallee,
                instCall.Name
            );

        // Bare Method(args) inside an instance method resolves against `this`.
        if (
            call.Expression is IdentifierNameSyntax bare
            && !_methods.ContainsKey(bare.Identifier.Text)
            && _classLocals.TryGetValue("this", out var self)
            && self.Info.Methods.ContainsKey(bare.Identifier.Text)
        )
            return LowerInstanceCall(
                self.Info,
                self.Slot,
                bare.Identifier.Text,
                call.ArgumentList.Arguments,
                symCallee,
                bare
            );

        // A plain call, a sibling static-method call (bare name, resolved against the enclosing static
        // class), a qualified static-method call `Class.M(...)`, or a generic call `Foo<int>(...)`
        // routed to its monomorphized instance `Foo$int`.
        //
        // Symbol-first: a resolved method symbol identifies the callee Roslyn's own member lookup would
        // pick — which already agrees with ResolveStaticCallee's "enclosing static class wins" preference
        // below, since C#'s own scoping rules are the same (an unqualified call resolves within the
        // caller's own enclosing type before its outer scope). `OriginalDefinition` maps a constructed
        // generic call's symbol to its template; a generic template is never registered in Methods (only
        // its monomorphized instances are, and those are detached — see CSharpSemantics.RegisterMethod),
        // so a generic call always and safely falls through to the syntax-based mangled-name lookup below
        // (the symbol only ever *confirms* which template is meant; the `$`-mangled instance is still
        // selected by MangleGeneric). A BCL `System.MathF` member is special-cased: it resolves to the
        // real BCL symbol (never in Methods, since Koh's own MathF is a distinct in-tree declaration), and
        // is routed by name to the compiled library the softfloat runtime appends.
        string? calleeName = null;
        CsMethod? callee = null;
        if (symCallee is not null)
        {
            if (IsBclMathF(symCallee.ContainingType))
                calleeName = $"MathF.{symCallee.Name}";
            else if (_semantics.Methods.TryGetValue(symCallee.OriginalDefinition, out var found))
            {
                callee = found;
                calleeName = found.Fn.Name;
            }
        }
        if (callee is null)
        {
            calleeName ??= call.Expression switch
            {
                IdentifierNameSyntax idn => ResolveStaticCallee(idn.Identifier.Text),
                // Qualified generic call `Class.M<...>(...)` -> its monomorphized instance `Class.M$...`.
                MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax typeName,
                    Name: GenericNameSyntax gm
                } => $"{typeName.Identifier.Text}."
                    + CSharpFrontend.MangleGeneric(
                        gm.Identifier.Text,
                        gm.TypeArgumentList.Arguments
                    ),
                MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax typeName } qualified
                    when _methods.ContainsKey(
                        $"{typeName.Identifier.Text}.{qualified.Name.Identifier.Text}"
                    ) => $"{typeName.Identifier.Text}.{qualified.Name.Identifier.Text}",
                // A namespaced BCL-style call `System.Class.Method(...)` (e.g. `System.MathF.Round`) resolves as
                // `Class.Method` — the frontend drops the `System` namespace, reaching the compiled library.
                // Restricted to `System` so it can't hijack an instance-field chain `a.b.M()` to a static `b.M`.
                MemberAccessExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.Text: "System" },
                        Name: IdentifierNameSyntax cls
                    },
                    Name: IdentifierNameSyntax mth
                } when _methods.ContainsKey($"{cls.Identifier.Text}.{mth.Identifier.Text}") =>
                    $"{cls.Identifier.Text}.{mth.Identifier.Text}",
                // Bare generic call `M<...>(...)` -> a sibling instance of the enclosing static class
                // (`Class.M$...`) if one exists, else a top-level instance (`M$...`).
                GenericNameSyntax gn => ResolveStaticCallee(
                    CSharpFrontend.MangleGeneric(gn.Identifier.Text, gn.TypeArgumentList.Arguments)
                ),
                _ => null,
            };
            if (calleeName is not null)
                _methods.TryGetValue(calleeName, out callee);
        }
        if (callee is null)
            throw new CSharpNotSupportedException(
                BetterUnresolvedMessage(
                    call.Expression,
                    $"unsupported call target '{call.Expression}'."
                )
            );

        var args = new List<IrValue>();
        var argList = call.ArgumentList.Arguments;
        if (argList.Count != callee.Params.Count)
            throw new CSharpNotSupportedException(
                $"'{calleeName}' takes {callee.Params.Count} argument(s), but {argList.Count} were given.",
                call.GetLocation()
            );
        for (int i = 0; i < argList.Count; i++)
        {
            if (callee.ParamStructs[i] is not null)
            {
                // A struct is passed by its address; reinterpret to the parameter's exact pointer
                // type so the call is well-typed (a nested field's base is typed i8*, not [N x i8]*).
                var address = LvalueAddress(argList[i].Expression);
                args.Add(_b.Conv(IrConvOp.Bitcast, address, callee.Fn.Parameters[i].Type));
            }
            else if (callee.RefParams[i])
            {
                args.Add(LvalueAddress(argList[i].Expression)); // ref/out: pass the address
            }
            else
            {
                args.Add(
                    Coerce(
                        LowerExpression(argList[i].Expression, callee.Params[i]),
                        callee.Params[i]
                    )
                );
            }
        }

        var result = _b.Call(callee.Fn, args);
        return (result, callee.Return ?? CsType.U8);
    }

    /// <summary>The class local (or <c>this</c>) an expression denotes, if any.</summary>
    private (IrValue Slot, CsClass Info)? ClassLocalOf(ExpressionSyntax expr) =>
        expr is IdentifierNameSyntax id && _classLocals.TryGetValue(id.Identifier.Text, out var c)
            ? c
        : expr is ThisExpressionSyntax && _classLocals.TryGetValue("this", out var t) ? t
        : null;

    /// <summary>Lower an instance-method call: pass the receiver pointer as the implicit <c>this</c>,
    /// then the user arguments, and call the <c>Class.Method</c> function. Symbol-first: <paramref
    /// name="symbol"/> (the call's resolved method symbol, from <see cref="LowerCall"/>) identifies the
    /// exact method Roslyn's own overload/member resolution reaches on the receiver's real declared type,
    /// via <see cref="OriginalDefinition"/> so a call through a constructed/inherited signature still maps
    /// to the template registered in <see cref="CSharpSemantics.Methods"/>. Falls back to the qualified
    /// name Koh's own class-info resolution already established (<paramref name="cls"/>, from the
    /// locals-based receiver lookup — out of this migration's scope) when no symbol resolves. <paramref
    /// name="calleeNode"/> is the method-name syntax at the call site, consulted only if lowering fails
    /// (see <see cref="BetterUnresolvedMessage"/>).</summary>
    private (IrValue, CsType) LowerInstanceCall(
        CsClass cls,
        IrValue thisSlot,
        string methodName,
        Microsoft.CodeAnalysis.SeparatedSyntaxList<ArgumentSyntax> args,
        IMethodSymbol? symbol = null,
        SyntaxNode? calleeNode = null
    )
    {
        CsMethod? callee =
            symbol is not null
            && _semantics.Methods.TryGetValue(symbol.OriginalDefinition, out var bySymbol)
                ? bySymbol
                : null;
        var qualified = $"{cls.Name}.{methodName}";
        if (callee is null && !_methods.TryGetValue(qualified, out callee))
            throw new CSharpNotSupportedException(
                calleeNode is not null
                    ? BetterUnresolvedMessage(
                        calleeNode,
                        $"class '{cls.Name}' has no method '{methodName}'."
                    )
                    : $"class '{cls.Name}' has no method '{methodName}'."
            );
        if (args.Count != callee.Params.Count - 1)
            throw new CSharpNotSupportedException(
                $"'{qualified}' takes {callee.Params.Count - 1} argument(s), but {args.Count} were given."
            );

        var callArgs = new List<IrValue> { _b.Load(thisSlot) };
        for (int i = 0; i < args.Count; i++)
            callArgs.Add(
                Coerce(
                    LowerExpression(args[i].Expression, callee.Params[i + 1]),
                    callee.Params[i + 1]
                )
            );
        return (_b.Call(callee.Fn, callArgs), callee.Return ?? CsType.U8);
    }

    /// <summary>Lower <c>new C()</c>: bump-allocate the instance from the arena, zero its fields (heap
    /// memory is uninitialized), and return the instance pointer. Constructor arguments are not yet
    /// supported — initialize fields after construction.</summary>
    private (IrValue, CsType) LowerNew(ObjectCreationExpressionSyntax objNew)
    {
        if (
            objNew.Type is not IdentifierNameSyntax cn
            || !_classes.TryGetValue(cn.Identifier.Text, out var cls)
        )
            throw new CSharpNotSupportedException(
                $"'new {objNew.Type}' is not supported (only class instances are)."
            );
        if (objNew.ArgumentList is { Arguments.Count: > 0 })
            throw new CSharpNotSupportedException(
                $"'new {cn.Identifier.Text}(...)' with constructor arguments is not supported; set fields after."
            );

        var heap = IrBuilder.GlobalRef(_globals[CSharpFrontend.HeapPointerName].Global);
        var raw = _b.Binary(
            IrBinaryOp.Sub,
            _b.Load(heap),
            IrBuilder.ConstInt(IrType.I16, cls.Layout.Size)
        );
        _b.Store(raw, heap);
        var basePtr = _b.Conv(IrConvOp.Bitcast, raw, IrType.Pointer(IrType.I8));

        // Zero the instance in a runtime loop rather than unrolling one GEP+Store per byte, which made
        // `new` cost O(size) ROM (a 16-byte object was hundreds of bytes). The loop is O(1) code.
        // ponytail: a byte-at-a-time loop; a rt.memclear routine (ld (hl+),a) would be faster if hot.
        if (cls.Layout.Size > 0)
        {
            var fn = _method.Fn;
            var iSlot = _b.Alloca(IrType.I16);
            _b.Store(IrBuilder.ConstInt(IrType.I16, 0), iSlot);
            var head = fn.AppendBlock("new.zero.head");
            var body = fn.AppendBlock("new.zero.body");
            var done = fn.AppendBlock("new.zero.done");
            _b.Br(head);
            _b.PositionAtEnd(head);
            _b.CondBr(
                _b.Compare(
                    IrCompareOp.Ult,
                    _b.Load(iSlot),
                    IrBuilder.ConstInt(IrType.I16, cls.Layout.Size)
                ),
                body,
                done
            );
            _b.PositionAtEnd(body);
            _b.Store(IrBuilder.ConstInt(IrType.I8, 0), _b.Gep(basePtr, _b.Load(iSlot), IrType.I8));
            _b.Store(
                _b.Binary(IrBinaryOp.Add, _b.Load(iSlot), IrBuilder.ConstInt(IrType.I16, 1)),
                iSlot
            );
            _b.Br(head);
            _b.PositionAtEnd(done);
        }
        return (basePtr, new CsType(IrType.Pointer(IrType.I8), Signed: false));
    }

    /// <summary>Inline an expression lambda: bind its parameter to <paramref name="arg"/> in a temporary
    /// slot, lower its body, then unbind. Non-capturing or value-referencing bodies work; there are no
    /// heap closures.</summary>
    private (IrValue Value, CsType Type) InlineLambda(
        ExpressionSyntax lambdaExpr,
        IrValue arg,
        CsType argType,
        CsType? expected
    )
    {
        var lambda =
            lambdaExpr as LambdaExpressionSyntax
            ?? throw new CSharpNotSupportedException("a LINQ operator expects a lambda.");
        string pname = lambda switch
        {
            SimpleLambdaExpressionSyntax s => s.Parameter.Identifier.Text,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } p =>
                p.ParameterList.Parameters[0].Identifier.Text,
            _ => throw new CSharpNotSupportedException(
                "a LINQ lambda must take exactly one parameter."
            ),
        };
        if (lambda.Body is not ExpressionSyntax body)
            throw new CSharpNotSupportedException(
                "a LINQ lambda must be an expression (no statement body)."
            );

        var slot = _b.Alloca(argType.Ir);
        _b.Store(arg, slot);
        bool had = _locals.TryGetValue(pname, out var saved);
        _locals[pname] = (slot, argType);
        var result = LowerExpression(body, expected);
        if (had)
            _locals[pname] = saved;
        else
            _locals.Remove(pname);
        return result;
    }

    private static readonly HashSet<string> LinqTerminals = new(StringComparer.Ordinal)
    {
        "Sum",
        "Count",
        "Max",
        "Min",
        "Any",
        "All",
    };

    /// <summary>Lower an array LINQ chain (<c>arr.Where(..).Select(..).Sum()</c> and similar) to a loop
    /// with inlined lambdas, or return null when the call is not such a chain. Reductions only — no
    /// materializing operators — so no result buffer is needed.</summary>
    private (IrValue, CsType)? TryLowerLinq(InvocationExpressionSyntax call)
    {
        var ops = new List<(string Op, ExpressionSyntax? Arg)>();
        ExpressionSyntax cur = call;
        while (
            cur is InvocationExpressionSyntax inv
            && inv.Expression is MemberAccessExpressionSyntax ma
        )
        {
            ops.Add(
                (
                    ma.Name.Identifier.Text,
                    inv.ArgumentList.Arguments.Count > 0
                        ? inv.ArgumentList.Arguments[0].Expression
                        : null
                )
            );
            cur = ma.Expression;
        }
        if (
            cur is not IdentifierNameSyntax srcId
            || !_arrays.TryGetValue(srcId.Identifier.Text, out var src)
        )
            return null;
        ops.Reverse(); // source order: [Where|Select]* then a terminal
        if (
            ops.Count == 0
            || !LinqTerminals.Contains(ops[^1].Op)
            || ops.Take(ops.Count - 1).Any(o => o.Op is not ("Where" or "Select"))
        )
            return null;

        var (termOp, termArg) = ops[^1];
        var pipeline = ops.Take(ops.Count - 1).ToList();
        var fn = _method.Fn;
        bool isMinMax = termOp is "Max" or "Min";

        // Max/Min seed the accumulator with element 0 and iterate from 1, which bypasses any Where/Select
        // pipeline for that first element (a filtered-out or unprojected element 0 would corrupt the
        // result). Only the pipeline-free forms are correct, so reject Max/Min behind a pipeline.
        if (isMinMax && pipeline.Count > 0)
            throw new CSharpNotSupportedException(
                $"{termOp}() is only supported directly on an array, not after a Where/Select pipeline.",
                call.GetLocation()
            );

        // Sum accumulates wider than the element (C# widens Sum to int/long) so a total exceeding the
        // element width doesn't wrap; Max/Min keep the element type.
        var accType = termOp switch
        {
            "Count" => CsType.U16,
            "Any" or "All" => CsType.Bool,
            "Sum" => src.Element.Ir.SizeInBytes >= 8 ? src.Element : CsType.I32,
            _ => src.Element,
        };

        var acc = _b.Alloca(accType.Ir);
        var iSlot = _b.Alloca(IrType.I16);
        // Max/Min seed the accumulator with element 0 and start at 1 (requires a non-empty source).
        int start = isMinMax ? 1 : 0;
        _b.Store(IrBuilder.ConstInt(IrType.I16, start), iSlot);
        _b.Store(
            isMinMax ? ElementAt(src, 0) : IrBuilder.ConstInt(accType.Ir, termOp == "All" ? 1 : 0),
            acc
        );

        var head = fn.AppendBlock("linq.head");
        var body = fn.AppendBlock("linq.body");
        var cont = fn.AppendBlock("linq.cont");
        var done = fn.AppendBlock("linq.done");
        _b.Br(head);

        _b.PositionAtEnd(head);
        _b.CondBr(
            _b.Compare(IrCompareOp.Ult, _b.Load(iSlot), IrBuilder.ConstInt(IrType.I16, src.Length)),
            body,
            done
        );

        _b.PositionAtEnd(body);
        IrValue e = _b.Load(_b.Gep(src.ArrayPtr, _b.Load(iSlot), src.Element.Ir));
        var eType = src.Element;
        foreach (var (op, lambda) in pipeline)
        {
            if (op == "Where")
            {
                var keep = Coerce(InlineLambda(lambda!, e, eType, CsType.Bool), CsType.Bool);
                var take = fn.AppendBlock("linq.take");
                _b.CondBr(keep, take, cont);
                _b.PositionAtEnd(take);
            }
            else // Select
            {
                (e, eType) = InlineLambda(lambda!, e, eType, null);
            }
        }

        switch (termOp)
        {
            case "Sum":
                if (termArg is not null)
                    (e, eType) = InlineLambda(termArg, e, eType, null);
                _b.Store(_b.Add(_b.Load(acc), Coerce((e, eType), accType)), acc);
                break;
            case "Count":
                if (termArg is not null)
                {
                    var keep = Coerce(InlineLambda(termArg, e, eType, CsType.Bool), CsType.Bool);
                    var take = fn.AppendBlock("linq.take");
                    _b.CondBr(keep, take, cont);
                    _b.PositionAtEnd(take);
                }
                _b.Store(_b.Add(_b.Load(acc), IrBuilder.ConstInt(IrType.I16, 1)), acc);
                break;
            case "Max":
            case "Min":
            {
                var pred = accType.Signed
                    ? (termOp == "Max" ? IrCompareOp.Sgt : IrCompareOp.Slt)
                    : (termOp == "Max" ? IrCompareOp.Ugt : IrCompareOp.Ult);
                var replace = fn.AppendBlock("linq.rep");
                _b.CondBr(
                    _b.Compare(pred, Coerce((e, eType), accType), _b.Load(acc)),
                    replace,
                    cont
                );
                _b.PositionAtEnd(replace);
                _b.Store(Coerce((e, eType), accType), acc);
                break;
            }
            case "Any":
            case "All":
            {
                var keep = Coerce(InlineLambda(termArg!, e, eType, CsType.Bool), CsType.Bool);
                // Any: set to 1 when the predicate holds; All: set to 0 when it fails.
                var hit = fn.AppendBlock("linq.hit");
                if (termOp == "Any")
                    _b.CondBr(keep, hit, cont);
                else
                    _b.CondBr(keep, cont, hit);
                _b.PositionAtEnd(hit);
                _b.Store(IrBuilder.ConstInt(IrType.I8, termOp == "Any" ? 1 : 0), acc);
                break;
            }
        }
        _b.Br(cont);

        _b.PositionAtEnd(cont);
        _b.Store(_b.Add(_b.Load(iSlot), IrBuilder.ConstInt(IrType.I16, 1)), iSlot);
        _b.Br(head);

        _b.PositionAtEnd(done);
        return (_b.Load(acc), accType);
    }

    /// <summary>Load the element at a constant index of a data array.</summary>
    private IrValue ElementAt((IrValue ArrayPtr, CsType Element, int Length) src, int index) =>
        _b.Load(_b.Gep(src.ArrayPtr, IrBuilder.ConstInt(IrType.I16, index), src.Element.Ir));

    /// <summary>Lower an arena-allocator call. <c>Mem.Alloc(n)</c> bumps the heap pointer down by n and
    /// returns the new pointer as a <c>byte*</c>; <c>Mem.Reset()</c> restores it to the top of the heap
    /// (freeing every prior allocation at once — the arena's whole-region free).</summary>
    private (IrValue, CsType) LowerMemCall(string method, InvocationExpressionSyntax call)
    {
        var heap = IrBuilder.GlobalRef(_globals[CSharpFrontend.HeapPointerName].Global);
        var bytePtr = new CsType(IrType.Pointer(IrType.I8), Signed: false);
        switch (method)
        {
            case "Alloc":
            {
                if (call.ArgumentList.Arguments.Count != 1)
                    throw new CSharpNotSupportedException(
                        "Mem.Alloc takes one argument (a byte count)."
                    );
                var size = Coerce(
                    LowerExpression(call.ArgumentList.Arguments[0].Expression, CsType.U16),
                    CsType.U16
                );
                var updated = _b.Binary(IrBinaryOp.Sub, _b.Load(heap), size);
                _b.Store(updated, heap);
                return (_b.Conv(IrConvOp.Bitcast, updated, bytePtr.Ir), bytePtr);
            }
            case "Reset":
                _b.Store(IrBuilder.ConstInt(IrType.I16, CSharpFrontend.HeapTop), heap);
                return (IrBuilder.ConstInt(IrType.I8, 0), CsType.U8);
            default:
                throw new CSharpNotSupportedException($"unknown Mem method '{method}'.");
        }
    }

    // ---- Types & operators -------------------------------------------------

    /// <summary>Widen/narrow a value to <paramref name="target"/> using its source signedness.
    /// Pointers and integers of the address width share storage, so casts between them (e.g.
    /// <c>(byte*)someUshort</c>, <c>(byte)ptr</c>) go through a <c>bitcast</c> reinterpret — a
    /// resize as an integer first when the widths differ — which keeps the IR well-typed rather
    /// than emitting a <c>zext</c>/<c>trunc</c> onto a pointer type.</summary>
    private IrValue Coerce((IrValue Value, CsType Type) source, CsType target)
    {
        // Floating point never shares the integer/bitcast path: reinterpreting or zero/sign-extending
        // IEEE bits is meaningless. Route genuine numeric conversions (int<->float, f32<->f64) through
        // the softfloat runtime; a same float type is identical bits and needs nothing.
        if (source.Type.IsFloat || target.IsFloat)
        {
            if (source.Type.IsFloat && target.IsFloat && source.Type.Bits == target.Bits)
                return source.Value;
            return target.IsFloat ? CoerceToFloat(source, target) : FloatToInt(source, target);
        }

        var s = source.Type.Ir;
        var t = target.Ir;
        if (s.StructurallyEquals(t))
            return source.Value;

        if (s.Kind == IrTypeKind.Pointer || t.Kind == IrTypeKind.Pointer)
        {
            if (s.SizeInBytes == t.SizeInBytes)
                return _b.Conv(IrConvOp.Bitcast, source.Value, t); // pure reinterpret

            // Different widths: resize as an integer of the source's storage, then reinterpret.
            var value = source.Value;
            var asInt = s.Kind == IrTypeKind.Pointer ? IrType.Int(s.SizeInBits) : s;
            if (s.Kind == IrTypeKind.Pointer)
                value = _b.Conv(IrConvOp.Bitcast, value, asInt);
            int targetIntBits = t.SizeInBits;
            if (targetIntBits != asInt.Bits)
                value = _b.Conv(
                    targetIntBits < asInt.Bits ? IrConvOp.Trunc
                        : source.Type.Signed ? IrConvOp.SExt
                        : IrConvOp.ZExt,
                    value,
                    IrType.Int(targetIntBits)
                );
            return t.Kind == IrTypeKind.Pointer ? _b.Conv(IrConvOp.Bitcast, value, t) : value;
        }

        if (t.Bits < s.Bits)
            return _b.Conv(IrConvOp.Trunc, source.Value, t);
        return _b.Conv(source.Type.Signed ? IrConvOp.SExt : IrConvOp.ZExt, source.Value, t);
    }

    /// <summary>A bitcast that no-ops when the value already has the target type. An identity reinterpret
    /// (e.g. loading a <c>Pointer(I8)</c> class-instance slot to use as a byte*) would otherwise emit a
    /// wasted byte-for-byte copy into a fresh slot on every field access.</summary>
    private IrValue Reinterpret(IrValue value, IrType type) =>
        value.Type.StructurallyEquals(type) ? value : _b.Conv(IrConvOp.Bitcast, value, type);

    /// <summary>The pointee type of a pointer, or a diagnostic if it has none (e.g. a bare address).</summary>
    private static IrType Pointee(CsType pointer) =>
        pointer.Ir.Element
        ?? throw new CSharpNotSupportedException("pointer arithmetic requires a typed pointee.");

    private static bool IsComparison(SyntaxKind kind) =>
        kind
            is SyntaxKind.LessThanExpression
                or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.GreaterThanExpression
                or SyntaxKind.GreaterThanOrEqualExpression
                or SyntaxKind.EqualsExpression
                or SyntaxKind.NotEqualsExpression;

    private static IrCompareOp CompareOp(SyntaxKind kind, bool signed) =>
        kind switch
        {
            SyntaxKind.EqualsExpression => IrCompareOp.Eq,
            SyntaxKind.NotEqualsExpression => IrCompareOp.Ne,
            SyntaxKind.LessThanExpression => signed ? IrCompareOp.Slt : IrCompareOp.Ult,
            SyntaxKind.LessThanOrEqualExpression => signed ? IrCompareOp.Sle : IrCompareOp.Ule,
            SyntaxKind.GreaterThanExpression => signed ? IrCompareOp.Sgt : IrCompareOp.Ugt,
            SyntaxKind.GreaterThanOrEqualExpression => signed ? IrCompareOp.Sge : IrCompareOp.Uge,
            _ => throw new CSharpNotSupportedException($"unsupported comparison '{kind}'."),
        };

    private static IrBinaryOp ArithOp(SyntaxKind kind, bool signed) =>
        kind switch
        {
            SyntaxKind.AddExpression => IrBinaryOp.Add,
            SyntaxKind.SubtractExpression => IrBinaryOp.Sub,
            SyntaxKind.MultiplyExpression => IrBinaryOp.Mul,
            SyntaxKind.DivideExpression => signed ? IrBinaryOp.SDiv : IrBinaryOp.UDiv,
            SyntaxKind.ModuloExpression => signed ? IrBinaryOp.SRem : IrBinaryOp.URem,
            SyntaxKind.BitwiseAndExpression => IrBinaryOp.And,
            SyntaxKind.BitwiseOrExpression => IrBinaryOp.Or,
            SyntaxKind.ExclusiveOrExpression => IrBinaryOp.Xor,
            SyntaxKind.LeftShiftExpression => IrBinaryOp.Shl,
            SyntaxKind.RightShiftExpression => signed ? IrBinaryOp.AShr : IrBinaryOp.LShr,
            _ => throw new CSharpNotSupportedException($"unsupported operator '{kind}'."),
        };

    /// <summary>A compound assignment (<c>+=</c> etc.) uses the same operator table as its plain
    /// form; map the assignment kind to the base binary kind and reuse <see cref="ArithOp"/>.</summary>
    private static IrBinaryOp CompoundOp(SyntaxKind kind, bool signed) =>
        ArithOp(
            kind switch
            {
                SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
                SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
                SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
                SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
                SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
                SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
                SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
                SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
                _ => throw new CSharpNotSupportedException(
                    $"unsupported compound assignment '{kind}'."
                ),
            },
            signed
        );
}
