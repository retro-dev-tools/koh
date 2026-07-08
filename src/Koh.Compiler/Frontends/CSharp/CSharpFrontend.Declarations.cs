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
    private static Dictionary<string, CsEnum> CollectEnums(CompilationUnitSyntax root, DiagnosticBag diagnostics)
    {
        var enums = new Dictionary<string, CsEnum>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            var underlying = decl.BaseList is { Types.Count: > 0 } bases
                ? ResolveType(bases.Types[0].Type, enums)
                : CsType.U8; // Koh C# defaults enums to byte (int has no place on an 8-bit CPU)

            var members = new Dictionary<string, long>(StringComparer.Ordinal);
            long next = 0;
            foreach (var member in decl.Members)
            {
                long value = member.EqualsValue is { } eq
                    ? ConstEval(eq.Value, name => members.TryGetValue(name, out var v) ? v : null, enums)
                    : next;
                members[member.Identifier.Text] = value;
                next = value + 1;
            }
            var name = decl.Identifier.Text;
            if (!enums.TryAdd(name, new CsEnum(underlying, members)))
                Report(diagnostics, $"duplicate enum '{name}' (only the first definition is used).",
                    decl.Identifier.GetLocation());
        }
        return enums;
    }

    private static (Dictionary<string, (IrGlobal Global, CsType Type)> Globals,
                    Dictionary<string, (CsType Type, long Value)> Consts,
                    List<(IrGlobal Global, long Value, CsType Type)> Inits,
                    Dictionary<string, (IrGlobal Global, CsType Element, int Length)> Arrays)
        CollectStatics(CompilationUnitSyntax root, IReadOnlyDictionary<string, CsEnum> enums, IrModule module,
                       DiagnosticBag diagnostics)
    {
        var globals = new Dictionary<string, (IrGlobal, CsType)>(StringComparer.Ordinal);
        var consts = new Dictionary<string, (CsType, long)>(StringComparer.Ordinal);
        var inits = new List<(IrGlobal, long, CsType)>();
        var arrays = new Dictionary<string, (IrGlobal, CsType, int)>(StringComparer.Ordinal);

        long? ConstLookup(string n) => consts.TryGetValue(n, out var c) ? c.Item2 : null;

        // Statics (consts, scalar globals, data arrays) share one field namespace; a duplicate name
        // would collide across these dictionaries and emit two globals with the same name. Keep the
        // first definition and report the rest.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        bool Redeclared(string n, Location loc)
        {
            if (declared.Add(n))
                return false;
            Report(diagnostics, $"duplicate static field '{n}' (only the first definition is used).", loc);
            return true;
        }

        // Only class-level fields are statics; fields inside a struct are its members.
        // Only fields at the program (wrapper) level are statics; a user class's fields are its
        // per-instance members, laid out per instance rather than as global storage.
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                     .Where(IsWrapperMember))
        {
            bool isConst = field.Modifiers.Any(m => m.ValueText == "const");
            bool isReadonly = field.Modifiers.Any(m => m.ValueText == "readonly");

            // A static array field is a data table: `static readonly T[] x = { ... }` lives in ROM;
            // `static T[] x = new T[n]` is a zero-initialized WRAM buffer.
            if (field.Declaration.Type is ArrayTypeSyntax arrayType)
            {
                var element = ResolveType(arrayType.ElementType, enums);
                foreach (var v in field.Declaration.Variables)
                {
                    if (Redeclared(v.Identifier.Text, v.Identifier.GetLocation()))
                        continue;
                    CollectStaticArray(v, element, isReadonly, ConstLookup, enums, module, arrays);
                }
                continue;
            }

            var type = ResolveType(field.Declaration.Type, enums);
            int size = type.Ir.SizeInBytes;

            foreach (var v in field.Declaration.Variables)
            {
                var name = v.Identifier.Text;
                if (Redeclared(name, v.Identifier.GetLocation()))
                    continue;
                if (isConst)
                {
                    if (v.Initializer is null)
                        throw new CSharpNotSupportedException($"const '{name}' needs an initializer.",
                            v.Identifier.GetLocation());
                    long cv = ConstEval(v.Initializer.Value, ConstLookup, enums, !type.Signed);
                    if (!FitsInBytes(cv, size))
                        throw new CSharpNotSupportedException(
                            $"const '{name}' value {cv} does not fit in {size} byte(s).", v.Identifier.GetLocation());
                    consts[name] = (type, cv);
                }
                else if (isReadonly && v.Initializer is { } roInit)
                {
                    var g = new IrGlobal(name, type.Ir, AddressSpace.Rom,
                        initializer: ToLittleEndian(ConstEval(roInit.Value, ConstLookup, enums, !type.Signed), size));
                    module.Globals.Add(g);
                    globals[name] = (g, type);
                }
                else
                {
                    var g = new IrGlobal(name, type.Ir, AddressSpace.Wram);
                    module.Globals.Add(g);
                    globals[name] = (g, type);
                    if (v.Initializer is { } init)
                        inits.Add((g, ConstEval(init.Value, ConstLookup, enums, !type.Signed), type));
                }
            }
        }
        return (globals, consts, inits, arrays);
    }

    /// <summary>Lower one static array field into a ROM (readonly, initialized) or WRAM (mutable,
    /// <c>new T[n]</c>) global.</summary>
    private static void CollectStaticArray(
        VariableDeclaratorSyntax v, CsType element, bool isReadonly, Func<string, long?> constLookup,
        IReadOnlyDictionary<string, CsEnum> enums,
        IrModule module, Dictionary<string, (IrGlobal, CsType, int)> arrays)
    {
        var name = v.Identifier.Text;
        int elemSize = element.Ir.SizeInBytes;

        // A string literal is ROM character data: `static readonly byte[] Msg = "SCORE";`.
        if (v.Initializer?.Value is LiteralExpressionSyntax { Token.Value: string text })
        {
            if (!isReadonly)
                throw new CSharpNotSupportedException(
                    $"string-initialized static array '{name}' must be 'static readonly' (it lives in ROM).");
            var strBytes = new List<byte>(text.Length * elemSize);
            foreach (var ch in text)
                strBytes.AddRange(ToLittleEndian(ch, elemSize));
            var strRom = new IrGlobal(name, IrType.Array(element.Ir, text.Length), AddressSpace.Rom, initializer: strBytes.ToArray());
            module.Globals.Add(strRom);
            arrays[name] = (strRom, element, text.Length);
            return;
        }

        List<ExpressionSyntax>? elements = v.Initializer?.Value switch
        {
            InitializerExpressionSyntax bare => bare.Expressions.ToList(),                 // = { ... }
            ArrayCreationExpressionSyntax { Initializer: { } init } => init.Expressions.ToList(), // = new T[] { ... }
            _ => null,
        };

        if (elements is not null)
        {
            if (!isReadonly)
                throw new CSharpNotSupportedException(
                    $"initialized static array '{name}' must be 'static readonly' (it lives in ROM); "
                    + "use 'static T[] x = new T[n]' for a mutable buffer.");
            var bytes = new List<byte>(elements.Count * elemSize);
            foreach (var e in elements)
                bytes.AddRange(ToLittleEndian(ConstEval(e, constLookup, enums, !element.Signed), elemSize));
            var rom = new IrGlobal(name, IrType.Array(element.Ir, elements.Count), AddressSpace.Rom, initializer: bytes.ToArray());
            module.Globals.Add(rom);
            arrays[name] = (rom, element, elements.Count);
            return;
        }

        // No element list: `new T[n]` (or a bare size) -> a zero buffer. Mutable in WRAM; a readonly
        // one is placed in ROM (constant zeros).
        long length64 = v.Initializer?.Value switch
        {
            ArrayCreationExpressionSyntax create when create.Type.RankSpecifiers[0].Sizes[0] is { } size
                => ConstEval(size, constLookup, enums),
            _ => throw new CSharpNotSupportedException(
                $"static array '{name}' needs an initializer or a size.", v.Identifier.GetLocation()),
        };
        // Range-check before the int cast so an out-of-range size is a diagnostic, not a silent
        // truncation (e.g. 0x100000001 casting to 1).
        if (length64 < 0 || length64 > 0xFFFF)
            throw new CSharpNotSupportedException(
                $"static array '{name}' length {length64} is out of range (0..65535).", v.Identifier.GetLocation());
        int length = (int)length64;
        var g = new IrGlobal(name, IrType.Array(element.Ir, length), isReadonly ? AddressSpace.Rom : AddressSpace.Wram);
        module.Globals.Add(g);
        arrays[name] = (g, element, length);
    }

    private static Dictionary<string, CsStruct> CollectStructs(
        CompilationUnitSyntax root, IReadOnlyDictionary<string, CsEnum> enums, DiagnosticBag diagnostics)
    {
        var decls = new Dictionary<string, StructDeclarationSyntax>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<StructDeclarationSyntax>())
            if (!decls.TryAdd(decl.Identifier.Text, decl))
                Report(diagnostics, $"duplicate struct '{decl.Identifier.Text}' (only the first definition is used).",
                    decl.Identifier.GetLocation());

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

            var specs = new List<(string Name, CsType Type, int Size, int Align, CsStruct? Nested)>();
            foreach (var member in decls[name].Members.OfType<FieldDeclarationSyntax>())
            {
                var typeSyntax = member.Declaration.Type;
                bool isStruct = typeSyntax is IdentifierNameSyntax sn && decls.ContainsKey(sn.Identifier.Text);
                CsStruct? nested = isStruct ? Layout(((IdentifierNameSyntax)typeSyntax).Identifier.Text) : null;
                var type = isStruct ? CsType.U8 : ResolveType(typeSyntax, enums);
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
        return structs;
    }

    /// <summary>Collect user classes (reference types) nested in the program wrapper: lay out their
    /// non-static scalar fields like a struct and record their instance methods.</summary>
    private static Dictionary<string, CsClass> CollectClasses(
        CompilationUnitSyntax root, IReadOnlyDictionary<string, CsEnum> enums, DiagnosticBag diagnostics)
    {
        var classes = new Dictionary<string, CsClass>(StringComparer.Ordinal);
        // All class names up front, so a field whose type is a (possibly later-declared, or self-)
        // reference class resolves to a heap pointer rather than an unsupported-type error.
        var classNames = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(c => c.Identifier.Text)
            .Where(n => n != WrapperClassName && n != "Mem")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (decl.Identifier.Text == WrapperClassName)
                continue; // the synthesized program wrapper, not a user class

            // `Mem` is the reserved arena-allocator intrinsic (Mem.Alloc/Mem.Reset). A user class of
            // that name would have its member calls hijacked by the allocator lowering, so reject it
            // rather than mis-compile silently.
            if (decl.Identifier.Text == "Mem")
            {
                Report(diagnostics, "'Mem' is reserved for the arena-allocator intrinsic and cannot name a class.",
                    decl.Identifier.GetLocation());
                continue;
            }

            var specs = new List<(string Name, CsType Type, int Size, int Align, CsStruct? Nested)>();
            foreach (var member in decl.Members.OfType<FieldDeclarationSyntax>())
            {
                if (member.Modifiers.Any(m => m.ValueText == "static"))
                {
                    // Neither CollectStatics (program-wrapper fields only) nor this pass stores a class-level
                    // static, so it would silently vanish. Reject it rather than emit a misleading later error.
                    Report(diagnostics, $"static field in class '{decl.Identifier.Text}' is not supported; "
                        + "declare it at the top level (program scope) instead.", member.GetLocation());
                    continue;
                }
                var type = ResolveTypeAllowingClass(member.Declaration.Type, enums, classNames);
                int fsize = type.Ir.SizeInBytes;
                foreach (var v in member.Declaration.Variables)
                    specs.Add((v.Identifier.Text, type, fsize, fsize, null));
            }
            var layout = LayoutFields(specs);

            var methods = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
            foreach (var m in decl.Members.OfType<MethodDeclarationSyntax>())
                if (!methods.TryAdd(m.Identifier.Text, m))
                    Report(diagnostics, $"duplicate method '{decl.Identifier.Text}.{m.Identifier.Text}' "
                        + "(overloaded instance methods are not supported).", m.Identifier.GetLocation());

            // A class with no instance fields lays out to size 0, so every `new` would return the same
            // heap address (all instances alias). Reject it rather than silently mis-allocate.
            if (layout.Fields.Count == 0)
                Report(diagnostics, $"class '{decl.Identifier.Text}' has no instance fields; a reference "
                    + "type needs at least one so distinct instances get distinct addresses.",
                    decl.Identifier.GetLocation());

            var cls = new CsClass(decl.Identifier.Text, layout, methods);
            if (!classes.TryAdd(decl.Identifier.Text, cls))
                Report(diagnostics, $"duplicate class '{decl.Identifier.Text}' (only the first definition is used).",
                    decl.Identifier.GetLocation());
        }
        return classes;
    }

    private static int RoundUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>Pack a sequence of fields into a struct/class layout: each field is aligned to its own
    /// size (nested aggregates pack byte-aligned), and the total is rounded up to the max field
    /// alignment. Shared by struct and class layout so their packing rules cannot drift.</summary>
    private static CsStruct LayoutFields(
        IReadOnlyList<(string Name, CsType Type, int Size, int Align, CsStruct? Nested)> members)
    {
        var fields = new List<CsField>(members.Count);
        int offset = 0, align = 1;
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
