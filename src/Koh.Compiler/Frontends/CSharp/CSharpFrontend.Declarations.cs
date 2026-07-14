using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Passes that collect top-level declarations (enums, static globals/consts/arrays, structs,
// classes) into the tables the lowerer consults.
public sealed partial class CSharpFrontend
{
    /// <summary>Collect every enum declaration. <c>enums</c> below is purely this pass's own local
    /// bookkeeping (duplicate detection, plus the safe self-/forward-reference fallback <see
    /// cref="ConstEval"/>/<see cref="TryEnumMember"/> use while it grows in file order — see
    /// CSharpFrontend.Types.cs's header remarks and <see cref="TryEnumMember"/> for why this pass must
    /// NOT read <see cref="CSharpSemantics.Enums"/> for that): nothing outside this method needs the text
    /// form anymore (Stage-2 P6), so it isn't returned — every other consumer of enum types resolves
    /// symbol-first through <see cref="CSharpSemantics.Enums"/>, materialized only after every
    /// <see cref="CSharpSemantics.RegisterEnum"/> call below has fired.</summary>
    private static void CollectEnums(
        CompilationUnitSyntax root,
        DiagnosticBag diagnostics,
        CSharpSemantics semantics
    )
    {
        var enums = new Dictionary<string, CsEnum>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            // `semantics: null`: an enum's base-list type is never itself a user enum in valid C#, so this
            // never needs symbol resolution — and consulting CSharpSemantics.Enums here (before this
            // enum, or any later one, is registered) would be unsafe (see the class remarks).
            var underlying = decl.BaseList is { Types.Count: > 0 } bases
                ? ResolveType(bases.Types[0].Type, semantics: null)
                : CsType.U8; // Koh C# defaults enums to byte (int has no place on an 8-bit CPU)

            var members = new Dictionary<string, long>(StringComparer.Ordinal);
            long next = 0;
            foreach (var member in decl.Members)
            {
                long value = member.EqualsValue is { } eq
                    ? ConstEval(
                        eq.Value,
                        name => members.TryGetValue(name, out var v) ? v : null,
                        enums
                    )
                    : next;
                members[member.Identifier.Text] = value;
                next = value + 1;
            }
            var name = decl.Identifier.Text;
            var csEnum = new CsEnum(underlying, members);
            if (!enums.TryAdd(name, csEnum))
                Report(
                    diagnostics,
                    $"duplicate enum '{name}' (only the first definition is used).",
                    decl.Identifier.GetLocation()
                );
            else
                semantics.RegisterEnum(decl, csEnum);
        }
    }

    private static (
        Dictionary<string, (IrGlobal Global, CsType Type)> Globals,
        Dictionary<string, (CsType Type, long Value)> Consts,
        List<(IrGlobal Global, long Value, CsType Type)> Inits,
        Dictionary<string, (IrGlobal Global, CsType Element, int Length)> Arrays
    ) CollectStatics(
        CompilationUnitSyntax root,
        IrModule module,
        DiagnosticBag diagnostics,
        CSharpSemantics semantics
    )
    {
        var globals = new Dictionary<string, (IrGlobal, CsType)>(StringComparer.Ordinal);
        var consts = new Dictionary<string, (CsType, long)>(StringComparer.Ordinal);
        var inits = new List<(IrGlobal, long, CsType)>();
        var arrays = new Dictionary<string, (IrGlobal, CsType, int)>(StringComparer.Ordinal);

        // Resolve a const referenced from within `owner`'s scope: a static class sees its own consts
        // by simple name (they are stored under the qualified key `Class.name`), falling back to a
        // top-level const of that name.
        long? ConstIn(string? owner, string n) =>
            owner is { } c && consts.TryGetValue($"{c}.{n}", out var q) ? q.Item2
            : consts.TryGetValue(n, out var b) ? b.Item2
            : null;

        // Statics (consts, scalar globals, data arrays) share one field namespace; a duplicate name
        // would collide across these dictionaries and emit two globals with the same name. Keep the
        // first definition and report the rest.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        bool Redeclared(string n, Location loc)
        {
            if (declared.Add(n))
                return false;
            Report(
                diagnostics,
                $"duplicate static field '{n}' (only the first definition is used).",
                loc
            );
            return true;
        }

        // Only class-level fields are statics; fields inside a struct are its members.
        // Only fields at the program (wrapper) level are statics; a user class's fields are its
        // per-instance members, laid out per instance rather than as global storage.
        foreach (
            var field in root.DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .Where(IsWrapperMember)
        )
        {
            bool isConst = field.Modifiers.Any(m => m.ValueText == "const");
            bool isReadonly = field.Modifiers.Any(m => m.ValueText == "readonly");

            // Fold this field's initializer against its own class scope, so a sibling const referenced
            // by simple name (e.g. `new byte[Size]`) resolves to the qualified `Class.Size`.
            var owner = ProgramMemberClass(field);
            long? ConstLookup(string n) => ConstIn(owner, n);

            // A static array field is a data table: `static readonly T[] x = { ... }` lives in ROM;
            // `static T[] x = new T[n]` is a zero-initialized WRAM buffer.
            if (field.Declaration.Type is ArrayTypeSyntax arrayType)
            {
                var element = ResolveType(arrayType.ElementType, semantics);
                foreach (var v in field.Declaration.Variables)
                {
                    // Qualify a static class's field by class (Board.cells) so two classes can each
                    // declare a same-named static field without colliding.
                    var key = ProgramMemberName(field, v.Identifier.Text);
                    if (Redeclared(key, v.Identifier.GetLocation()))
                        continue;
                    CollectStaticArray(
                        v,
                        key,
                        element,
                        isReadonly,
                        ConstLookup,
                        semantics,
                        module,
                        arrays
                    );
                }
                continue;
            }

            var type = ResolveType(field.Declaration.Type, semantics);
            int size = type.Ir.SizeInBytes;

            foreach (var v in field.Declaration.Variables)
            {
                var name = ProgramMemberName(field, v.Identifier.Text);
                if (Redeclared(name, v.Identifier.GetLocation()))
                    continue;
                if (isConst)
                {
                    if (v.Initializer is null)
                        throw new CSharpNotSupportedException(
                            $"const '{name}' needs an initializer.",
                            v.Identifier.GetLocation()
                        );
                    long cv = ConstEval(
                        v.Initializer.Value,
                        ConstLookup,
                        unsigned: !type.Signed,
                        semantics: semantics
                    );
                    if (!FitsInBytes(cv, size))
                        throw new CSharpNotSupportedException(
                            $"const '{name}' value {cv} does not fit in {size} byte(s).",
                            v.Identifier.GetLocation()
                        );
                    consts[name] = (type, cv);
                    semantics.RegisterConst(v, type, cv);
                }
                else if (isReadonly && v.Initializer is { } roInit)
                {
                    var g = new IrGlobal(
                        name,
                        type.Ir,
                        AddressSpace.Rom,
                        initializer: ToLittleEndian(
                            ConstEval(
                                roInit.Value,
                                ConstLookup,
                                unsigned: !type.Signed,
                                semantics: semantics
                            ),
                            size
                        )
                    );
                    module.Globals.Add(g);
                    globals[name] = (g, type);
                    semantics.RegisterGlobal(v, g, type);
                }
                else
                {
                    var g = new IrGlobal(name, type.Ir, AddressSpace.Wram);
                    module.Globals.Add(g);
                    globals[name] = (g, type);
                    semantics.RegisterGlobal(v, g, type);
                    // No-initializer fields default to zero via the backend's unconditional, boot-only
                    // WRAM-globals clear (Sm83Backend's pre-FunctionLabel boot stub), not a per-field store
                    // here: a store in the entry function's own IR body re-runs on every recursive
                    // re-entry (Main calling Main), which would reset the field on every call instead of
                    // only at true boot. Only an explicit initializer needs a store, since only it carries
                    // a value the backend-level zero-clear doesn't already produce.
                    if (v.Initializer is { } init)
                        inits.Add(
                            (
                                g,
                                ConstEval(
                                    init.Value,
                                    ConstLookup,
                                    unsigned: !type.Signed,
                                    semantics: semantics
                                ),
                                type
                            )
                        );
                }
            }
        }
        return (globals, consts, inits, arrays);
    }

    /// <summary>Lower one static array field into a ROM (readonly, initialized) or WRAM (mutable,
    /// <c>new T[n]</c>) global.</summary>
    private static void CollectStaticArray(
        VariableDeclaratorSyntax v,
        string name,
        CsType element,
        bool isReadonly,
        Func<string, long?> constLookup,
        CSharpSemantics semantics,
        IrModule module,
        Dictionary<string, (IrGlobal, CsType, int)> arrays
    )
    {
        int elemSize = element.Ir.SizeInBytes;

        // A string literal is ROM character data: `static readonly byte[] Msg = "SCORE";`.
        if (v.Initializer?.Value is LiteralExpressionSyntax { Token.Value: string text })
        {
            if (!isReadonly)
                throw new CSharpNotSupportedException(
                    $"string-initialized static array '{name}' must be 'static readonly' (it lives in ROM)."
                );
            var strBytes = new List<byte>(text.Length * elemSize);
            foreach (var ch in text)
                strBytes.AddRange(ToLittleEndian(ch, elemSize));
            var strRom = new IrGlobal(
                name,
                IrType.Array(element.Ir, text.Length),
                AddressSpace.Rom,
                initializer: strBytes.ToArray()
            );
            module.Globals.Add(strRom);
            arrays[name] = (strRom, element, text.Length);
            return;
        }

        List<ExpressionSyntax>? elements = v.Initializer?.Value switch
        {
            InitializerExpressionSyntax bare => bare.Expressions.ToList(), // = { ... }
            ArrayCreationExpressionSyntax { Initializer: { } init } => init.Expressions.ToList(), // = new T[] { ... }
            _ => null,
        };

        if (elements is not null)
        {
            if (!isReadonly)
                throw new CSharpNotSupportedException(
                    $"initialized static array '{name}' must be 'static readonly' (it lives in ROM); "
                        + "use 'static T[] x = new T[n]' for a mutable buffer."
                );
            var bytes = new List<byte>(elements.Count * elemSize);
            foreach (var e in elements)
                bytes.AddRange(
                    ToLittleEndian(
                        ConstEval(e, constLookup, unsigned: !element.Signed, semantics: semantics),
                        elemSize
                    )
                );
            var rom = new IrGlobal(
                name,
                IrType.Array(element.Ir, elements.Count),
                AddressSpace.Rom,
                initializer: bytes.ToArray()
            );
            module.Globals.Add(rom);
            arrays[name] = (rom, element, elements.Count);
            return;
        }

        // No element list: `new T[n]` (or a bare size) -> a zero buffer. Mutable in WRAM; a readonly
        // one is placed in ROM (constant zeros).
        long length64 = v.Initializer?.Value switch
        {
            ArrayCreationExpressionSyntax create
                when create.Type.RankSpecifiers[0].Sizes[0] is { } size => ConstEval(
                size,
                constLookup,
                semantics: semantics
            ),
            _ => throw new CSharpNotSupportedException(
                $"static array '{name}' needs an initializer or a size.",
                v.Identifier.GetLocation()
            ),
        };
        // Range-check before the int cast so an out-of-range size is a diagnostic, not a silent
        // truncation (e.g. 0x100000001 casting to 1).
        if (length64 < 0 || length64 > 0xFFFF)
            throw new CSharpNotSupportedException(
                $"static array '{name}' length {length64} is out of range (0..65535).",
                v.Identifier.GetLocation()
            );
        int length = (int)length64;
        var g = new IrGlobal(
            name,
            IrType.Array(element.Ir, length),
            isReadonly ? AddressSpace.Rom : AddressSpace.Wram
        );
        module.Globals.Add(g);
        arrays[name] = (g, element, length);
    }

    private static Dictionary<string, CsStruct> CollectStructs(
        CompilationUnitSyntax root,
        DiagnosticBag diagnostics,
        CSharpSemantics semantics
    )
    {
        var decls = new Dictionary<string, StructDeclarationSyntax>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<StructDeclarationSyntax>())
            if (!decls.TryAdd(decl.Identifier.Text, decl))
                Report(
                    diagnostics,
                    $"duplicate struct '{decl.Identifier.Text}' (only the first definition is used).",
                    decl.Identifier.GetLocation()
                );

        var structs = new Dictionary<string, CsStruct>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);

        // Lay a struct out, resolving struct-typed fields on demand (nested structs) with cycle
        // detection. Scalar fields align to their own size; a nested struct field is packed (SM83 is
        // byte-addressable, so misalignment costs nothing).
        CsStruct Layout(string name)
        {
            if (structs.TryGetValue(name, out var done))
                return done;
            if (!inProgress.Add(name))
                throw new CSharpNotSupportedException($"struct '{name}' contains itself.");

            var specs =
                new List<(string Name, CsType Type, int Size, int Align, CsStruct? Nested)>();
            foreach (var member in decls[name].Members.OfType<FieldDeclarationSyntax>())
            {
                var typeSyntax = member.Declaration.Type;
                bool isStruct =
                    typeSyntax is IdentifierNameSyntax sn && decls.ContainsKey(sn.Identifier.Text);
                CsStruct? nested = isStruct
                    ? Layout(((IdentifierNameSyntax)typeSyntax).Identifier.Text)
                    : null;
                var type = isStruct ? CsType.U8 : ResolveType(typeSyntax, semantics);
                int size = nested?.Size ?? type.Ir.SizeInBytes;
                int fieldAlign = nested is not null ? 1 : size; // nested aggregates pack byte-aligned
                foreach (var v in member.Declaration.Variables)
                    specs.Add((v.Identifier.Text, type, size, fieldAlign, nested));
            }
            var result = LayoutFields(specs);
            structs[name] = result;
            inProgress.Remove(name);
            return result;
        }

        foreach (var name in decls.Keys)
            Layout(name);
        // Registered after every struct is laid out (rather than inline in Layout): Layout recurses into
        // nested struct fields and memoizes, so a single pass here over `decls` is simpler than tracking
        // registration through the recursion, and every name in `decls` is guaranteed laid out by now.
        foreach (var (name, decl) in decls)
            semantics.RegisterStruct(decl, structs[name]);
        return structs;
    }

    /// <summary>Collect user classes (reference types) nested in the program wrapper: lay out their
    /// non-static scalar fields like a struct and record their instance methods.</summary>
    private static Dictionary<string, CsClass> CollectClasses(
        CompilationUnitSyntax root,
        DiagnosticBag diagnostics,
        CSharpSemantics semantics
    )
    {
        var classes = new Dictionary<string, CsClass>(StringComparer.Ordinal);
        // All class names up front, so a field whose type is a (possibly later-declared, or self-)
        // reference class resolves to a heap pointer rather than an unsupported-type error.
        var classNames = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(c => !c.Modifiers.Any(m => m.ValueText == "static"))
            .Select(c => c.Identifier.Text)
            .Where(n => n != WrapperClassName && n != "Mem")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (decl.Identifier.Text == WrapperClassName)
                continue; // the synthesized program wrapper, not a user class

            // Reserved intrinsic surfaces: a user class named `Mem` (arena allocator), `Hardware`
            // (registers), `Gb` (memory regions), or `BitConverter` (float<->bits reinterpret) would have
            // its member access silently hijacked by the corresponding lowering. Reject rather than
            // mis-compile — checked before the static skip below, so a `static class Gb` is caught too.
            if (decl.Identifier.Text is "Mem" or "Hardware" or "Gb" or "BitConverter")
            {
                Report(
                    diagnostics,
                    $"'{decl.Identifier.Text}' is reserved for a built-in intrinsic surface and cannot name a class.",
                    decl.Identifier.GetLocation()
                );
                continue;
            }

            // A top-level `static class` is a namespace of program-level static methods/fields
            // (collected via IsWrapperMember/CollectStatics), not a heap reference type.
            if (decl.Modifiers.Any(m => m.ValueText == "static"))
                continue;

            var specs =
                new List<(string Name, CsType Type, int Size, int Align, CsStruct? Nested)>();
            foreach (var member in decl.Members.OfType<FieldDeclarationSyntax>())
            {
                if (member.Modifiers.Any(m => m.ValueText == "static"))
                {
                    // Neither CollectStatics (program-wrapper fields only) nor this pass stores a class-level
                    // static, so it would silently vanish. Reject it rather than emit a misleading later error.
                    Report(
                        diagnostics,
                        $"static field in class '{decl.Identifier.Text}' is not supported; "
                            + "declare it at the top level (program scope) instead.",
                        member.GetLocation()
                    );
                    continue;
                }
                // classIndexSafe: false — this pass's own field-layout loop registers classes as it goes
                // (RegisterClass, below), so a field naming a not-yet-registered class (self- or later-
                // declared, e.g. a linked-list node) must NOT consult CSharpSemantics.Classes yet (see
                // CSharpFrontend.Types.cs's header remarks and ResolveTypeAllowingClass's own remarks);
                // `classNames` (gathered up front, above) is the safe check here instead.
                var type = ResolveTypeAllowingClass(
                    member.Declaration.Type,
                    semantics,
                    classNames,
                    classIndexSafe: false
                );
                int fsize = type.Ir.SizeInBytes;
                foreach (var v in member.Declaration.Variables)
                    specs.Add((v.Identifier.Text, type, fsize, fsize, null));
            }
            var layout = LayoutFields(specs);

            var methods = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
            foreach (var m in decl.Members.OfType<MethodDeclarationSyntax>())
                if (!methods.TryAdd(m.Identifier.Text, m))
                    Report(
                        diagnostics,
                        $"duplicate method '{decl.Identifier.Text}.{m.Identifier.Text}' "
                            + "(overloaded instance methods are not supported).",
                        m.Identifier.GetLocation()
                    );

            // A class with no instance fields lays out to size 0, so every `new` would return the same
            // heap address (all instances alias). Reject it rather than silently mis-allocate.
            if (layout.Fields.Count == 0)
                Report(
                    diagnostics,
                    $"class '{decl.Identifier.Text}' has no instance fields; a reference "
                        + "type needs at least one so distinct instances get distinct addresses.",
                    decl.Identifier.GetLocation()
                );

            var cls = new CsClass(decl.Identifier.Text, layout, methods);
            if (!classes.TryAdd(decl.Identifier.Text, cls))
                Report(
                    diagnostics,
                    $"duplicate class '{decl.Identifier.Text}' (only the first definition is used).",
                    decl.Identifier.GetLocation()
                );
            else
                semantics.RegisterClass(decl, cls);
        }
        return classes;
    }

    private static int RoundUp(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;

    /// <summary>Pack a sequence of fields into a struct/class layout: each field is aligned to its own
    /// size (nested aggregates pack byte-aligned), and the total is rounded up to the max field
    /// alignment. Shared by struct and class layout so their packing rules cannot drift.</summary>
    private static CsStruct LayoutFields(
        IReadOnlyList<(string Name, CsType Type, int Size, int Align, CsStruct? Nested)> members
    )
    {
        var fields = new List<CsField>(members.Count);
        int offset = 0,
            align = 1;
        foreach (var (name, type, size, fieldAlign, nested) in members)
        {
            offset = RoundUp(offset, fieldAlign);
            fields.Add(new CsField(name, type, offset, nested));
            offset += size;
            align = Math.Max(align, fieldAlign);
        }
        return new CsStruct(fields, RoundUp(offset, align));
    }
}
