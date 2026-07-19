using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Diagnostics;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Mutable state shared across every <see cref="CilMethodLowerer"/> instance for one module lowering
/// pass: the function/global/layout caches, plus <see cref="EnsureLowered"/> — the on-demand lowering
/// entry point a call site (an instance <c>call</c>/<c>callvirt</c>, a delegate <c>Invoke</c>
/// resolution, or a <c>newobj</c> constructor) uses to lower a compiler-generated type's method
/// (display-class ctor, capturing-lambda body, no-capture-cache singleton method) the first time — and
/// only the first time — something actually references it. See
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>'s delegates/closures task: nothing
/// eagerly lowers a compiler-generated type's members (unlike phase 1's hand-written statics), so a
/// dead member (e.g. an iterator's boxing accessor, out of THIS task's scope) is never even attempted.
/// </summary>
internal sealed class CilLoweringContext
{
    public IrModule Module { get; }
    public DiagnosticBag Diagnostics { get; }
    public IReadOnlyDictionary<MethodDefinition, CilIntrinsicIndex.Entry> Intrinsics { get; }

    /// <summary>Every <c>[KohRuntime(key)]</c>-tagged method reachable from the module, keyed by its
    /// key string (see <see cref="CilRuntimeIndex"/>) — the float/double IL routing table
    /// (<c>CilMethodLowerer</c>'s <c>CallRuntime</c>/<c>EnsureRuntime</c>).</summary>
    public IReadOnlyDictionary<string, MethodDefinition> Runtime { get; }
    public Dictionary<MethodDefinition, IrFunction> FunctionsByMethod { get; } = new();

    /// <summary>The game's own module — everything else a call resolves into (Koh.GameBoy's Hal,
    /// <c>Mem.Copy</c>/<c>Fill</c>, …) is a REFERENCED assembly (see the on-demand-lowering task,
    /// docs/superpowers/specs/2026-07-14-cil-frontend-design.md, task 2).</summary>
    public ModuleDefinition GameModule { get; }

    private readonly Dictionary<int, IrGlobal> _registerGlobals = new();
    private readonly Dictionary<int, IrGlobal> _regionGlobals = new();
    private readonly Dictionary<TypeDefinition, CilClassLayout> _classLayouts = new();
    private readonly HashSet<MethodDefinition> _inProgress = new();
    private IrGlobal? _heapGlobal;

    // ---- Static fields (see CilMethodLowerer.Statics.cs and the statics task in
    // docs/superpowers/specs/2026-07-14-cil-frontend-design.md) ------------------------------------

    // Every static field's storage, once decided. A field NOT yet present here is still undecided —
    // EnsureStaticGlobal lazily creates an ordinary mutable WRAM holder for it the first time anything
    // references it (correct for a mutable scalar/pointer/array-reference field, since there is no
    // ordering hazard: whichever method touches it first settles it, and every later reference agrees).
    // A field that IS present was placed here by CilStaticFieldSupport.Collect — a pure-metadata
    // pre-pass over every type's static constructor that runs BEFORE any method body (including a
    // cctor's own) is lowered, so a readonly field's ROM/alias classification is always settled before
    // any consumer (including another type's cctor) can race it into the wrong (plain WRAM) shape.
    private readonly Dictionary<FieldDefinition, IrGlobal> _staticFields = new();

    // A field in this set has no separate holder cell: its own global's address (Ldsfld/Ldsflda push
    // GlobalRef directly, never Load) IS its value — used for a readonly array-literal folded to a ROM
    // blob, and for a fixed-size array field (readonly or mutable) whose 'newarr' carries no literal
    // content, which becomes a dedicated Array-typed global instead of a heap allocation (matches
    // CSharpFrontend's own static-array placement; see CilStaticFieldSupport's remarks).
    private readonly HashSet<FieldDefinition> _staticFieldIsAlias = new();

    // Element type/signedness/count for every aliased ARRAY field (not populated for a folded SCALAR
    // constant) — mirrors CilMethodLowerer.Arrays.cs's _pendingArrayInfo shape, so Ldsfld can tag its
    // pushed value the same way a 'newarr' or a static-array identifier does.
    private readonly Dictionary<
        FieldDefinition,
        (IrType ElemType, bool Signed, int Count)
    > _staticArrayInfo = new();

    // A static field of USER STRUCT type (e.g. the Framework's 'static TileAsset Tiles' /
    // 'static Timer _timer' — the ideal-game-API program's verify-first item, confirmed real by
    // CilFrameworkTests): its global is a WRAM byte blob of the struct's layout size, and — matching
    // the frontend's universal "a struct value IS the address of its bytes" representation
    // (CilMethodLowerer.Structs.cs) — Ldsfld pushes the global's ADDRESS (never a scalar Load),
    // Ldsflda pushes the same address, and Stsfld byte-copies the source struct into the blob.
    // Zero-init comes free: WRAM statics are cleared at boot, giving 'default' struct state exactly
    // like C# expects.
    private readonly Dictionary<FieldDefinition, CilClassLayout> _staticStructLayouts = new();

    // Per static-constructor MethodDefinition, the instructions CilStaticFieldSupport.Collect proved
    // redundant (fully accounted for by a field's ROM/alias classification) — CilMethodLowerer seeds
    // Simulate's _suppressed set from this at the top of Run(), exactly like DetectDelegateCacheIdioms'
    // own idiom collapses to nothing at runtime.
    private readonly Dictionary<MethodDefinition, HashSet<Instruction>> _elidedCctorInstructions =
        new();
    private static readonly HashSet<Instruction> EmptyElidedSet = new();

    /// <summary>Every type's lowered static-constructor <see cref="IrFunction"/>, in module-sweep
    /// order — the entry function calls each of these once, in its own prologue (mirrors
    /// CSharpFrontend's <c>staticInits</c>, emitted the same way at the top of <c>Main</c>, but as a
    /// real call rather than inlined stores: a CIL static constructor can contain arbitrary lowerable
    /// IL, not just a flat list of constant stores).</summary>
    public List<IrFunction> CctorFunctions { get; } = new();

    /// <summary>True once <paramref name="field"/>'s storage has been decided (by
    /// <see cref="RegisterFoldedStatic"/>/<see cref="RegisterFoldedStaticArray"/>, or by a prior
    /// <see cref="EnsureStaticGlobal"/> call settling it as an ordinary mutable holder).</summary>
    public bool HasStaticFieldDecision(FieldDefinition field) => _staticFields.ContainsKey(field);

    /// <summary>Fold a readonly SCALAR static field to a ROM global carrying its compile-time-constant
    /// value — <paramref name="romGlobal"/>'s own <see cref="IrGlobal.Initializer"/> already holds the
    /// bytes; Ldsfld for this field becomes an ordinary <c>Load</c> off it (no alias — the field still
    /// has real, if read-only, storage).</summary>
    public void RegisterFoldedStatic(FieldDefinition field, IrGlobal romGlobal) =>
        _staticFields[field] = romGlobal;

    /// <summary>Alias a static ARRAY field straight onto <paramref name="arrayGlobal"/> — the field has
    /// no separate holder; the global's own address IS the field's value (Ldsfld/Ldsflda push
    /// <c>GlobalRef</c> directly). Used both for a readonly array literal (ROM, content-initialized)
    /// and a fixed-size array with no literal content (ROM if readonly, else WRAM, zero-initialized by
    /// the backend's unconditional boot-only WRAM clear — see CSharpFrontend's own static-array
    /// remarks).</summary>
    public void RegisterFoldedStaticArray(
        FieldDefinition field,
        IrGlobal arrayGlobal,
        IrType elemType,
        bool elemSigned,
        int count
    )
    {
        _staticFields[field] = arrayGlobal;
        _staticFieldIsAlias.Add(field);
        _staticArrayInfo[field] = (elemType, elemSigned, count);
    }

    public bool IsStaticFieldAlias(FieldDefinition field) => _staticFieldIsAlias.Contains(field);

    /// <summary>The struct layout of a user-struct static field settled by
    /// <see cref="EnsureStaticGlobal"/> (see <c>_staticStructLayouts</c>' remarks), if it is one.</summary>
    public bool TryGetStaticStructLayout(FieldDefinition field, out CilClassLayout layout) =>
        _staticStructLayouts.TryGetValue(field, out layout!);

    public bool TryGetStaticArrayInfo(
        FieldDefinition field,
        out (IrType ElemType, bool Signed, int Count) info
    ) => _staticArrayInfo.TryGetValue(field, out info);

    /// <summary>Declaring types already run through <see cref="CilStaticFieldSupport.CollectType"/> by
    /// <see cref="EnsureStaticGlobal"/>'s on-demand fallback below — guards against re-walking the same
    /// cross-assembly type's <c>.cctor</c> once per undecided field it declares (each call is otherwise
    /// idempotent on its own — <c>CollectType</c>'s own per-field <c>HasStaticFieldDecision</c> early-out
    /// — but this keeps it O(types-referenced), not O(fields-referenced), for a type with several
    /// mutable fields).</summary>
    private readonly HashSet<TypeDefinition> _sweptTypes = new();

    /// <summary>The global backing <paramref name="field"/>, creating an ordinary mutable WRAM holder
    /// the first time anything references an undecided field. For a field declared in the GAME's own
    /// module, every readonly/fold-eligible field is already decided by the time any method body lowers
    /// (<see cref="CilStaticFieldSupport.Collect"/>'s eager Pass 0 pre-pass), so this is reached only for
    /// an ordinary mutable field — matching the original "no pre-pass needed" reasoning. For a field
    /// declared in a REFERENCED assembly (e.g. Koh.GameBoy's own <c>[KohAligned(n)] static byte[]</c>
    /// library statics — <c>Collect</c> deliberately does not sweep referenced assemblies eagerly, see
    /// its own remarks), no pre-pass ever ran over its declaring type at all — so this classifies that
    /// type ON DEMAND, the first time reachable code references any of its fields, via the same
    /// <see cref="CilStaticFieldSupport.CollectType"/> Pass 0 itself calls, before falling back to the
    /// ordinary holder. This mirrors <see cref="EnsureLowered"/>'s own on-demand lowering of a
    /// cross-assembly METHOD body — classify/lower the first time something reachable actually needs
    /// it, never eagerly for an assembly's entire type set (which would place every matched field's
    /// global permanently, unconditionally, regardless of reachability — see <c>CollectType</c>'s
    /// remarks on why an eager whole-reference-graph sweep was tried and reverted).</summary>
    public IrGlobal EnsureStaticGlobal(FieldDefinition field)
    {
        if (_staticFields.TryGetValue(field, out var g))
            return g;
        if (_sweptTypes.Add(field.DeclaringType))
            CilStaticFieldSupport.CollectType(field.DeclaringType, this);
        if (_staticFields.TryGetValue(field, out g))
            return g;
        // A user-struct field gets a WRAM byte blob sized by its layout (see _staticStructLayouts'
        // remarks) — checked before CilTypeMapper.Map, which has no struct branch and would throw.
        if (CilStructSupport.ResolveStruct(field.FieldType) is { } structDef)
        {
            var layout = GetLayout(structDef);
            g = new IrGlobal(
                $"{field.DeclaringType.FullName}.{field.Name}",
                IrType.Array(IrType.I8, Math.Max(layout.Size, 1)),
                AddressSpace.Wram,
                alignment: CilStaticFieldSupport.ReadAlignment(field)
            );
            Module.Globals.Add(g);
            _staticFields[field] = g;
            _staticStructLayouts[field] = layout;
            return g;
        }
        var (irType, _) = CilTypeMapper.Map(field.FieldType);
        g = new IrGlobal(
            $"{field.DeclaringType.FullName}.{field.Name}",
            irType,
            AddressSpace.Wram,
            alignment: CilStaticFieldSupport.ReadAlignment(field)
        );
        Module.Globals.Add(g);
        _staticFields[field] = g;
        return g;
    }

    // A compiler-generated '<PrivateImplementationDetails>' RVA blob field (Roslyn's own storage for
    // the raw bytes behind BOTH a local array literal's RuntimeHelpers.InitializeArray idiom and a
    // `"..."u8` ReadOnlySpan<byte> literal — see CilMethodLowerer.Arrays.cs's DetectArrayLiteralIdioms
    // and CilMethodLowerer.Delegates.cs's TryLowerSpanCall) -> the ROM global carrying its bytes.
    // Roslyn deduplicates identical literal content onto ONE blob field across the whole module, so
    // keying this cache by the field (rather than by call site) means two `{1,2,3}` literals anywhere
    // in the program share one ROM global for free, exactly like a hand-written 'static readonly'
    // array field already would.
    private readonly Dictionary<FieldDefinition, IrGlobal> _rvaBlobGlobals = new();

    /// <summary>The ROM global backing <paramref name="blobField"/>'s raw bytes (a compiler-generated
    /// RVA-initialized field — <see cref="FieldDefinition.InitialValue"/> is already exactly the bytes
    /// the CLR loader would have mapped from the PE image), creating it the first time anything
    /// references this particular blob field. Note this is a wholly different field shape from an
    /// ordinary user 'static readonly' field (<see cref="EnsureStaticGlobal"/>/<see cref="RegisterFoldedStatic"/>
    /// above): a blob field is never scanned by <c>CilStaticFieldSupport.Collect</c> (it has no
    /// '.cctor' store at all — its content is baked directly into the assembly, not assigned at
    /// runtime), so it would otherwise fall through <c>EnsureStaticGlobal</c>'s generic path and get a
    /// zero-initialized WRAM holder with none of its real content.</summary>
    public IrGlobal EnsureRvaBlobGlobal(FieldDefinition blobField)
    {
        if (_rvaBlobGlobals.TryGetValue(blobField, out var g))
            return g;
        var bytes = blobField.InitialValue;
        g = new IrGlobal(
            $"__rvablob.{blobField.FullName}",
            IrType.Array(IrType.I8, bytes.Length),
            AddressSpace.Rom,
            initializer: bytes
        );
        Module.Globals.Add(g);
        _rvaBlobGlobals[blobField] = g;
        return g;
    }

    // A string literal (`ldstr`, straight from the #US metadata heap — there is no backing
    // FieldDefinition to key by the way EnsureRvaBlobGlobal's blob fields have) -> the ROM global
    // carrying its LENGTH-PREFIXED ASCII bytes (see EnsureStringLiteralGlobal). Keyed by the literal's
    // own content (ordinal) so two occurrences of the same literal text anywhere in the module share one
    // ROM global, matching EnsureRvaBlobGlobal's own dedup rationale.
    private readonly Dictionary<string, IrGlobal> _stringLiteralGlobals = new(
        StringComparer.Ordinal
    );

    /// <summary>The ROM global carrying <paramref name="value"/>'s runtime representation: a
    /// LENGTH-PREFIXED ASCII blob — <c>[u16 length (little-endian, matching <see cref="DataLayout.Sm83"/>)]
    /// [one byte per char]</c> — so a <c>string</c> value is just a pointer to this global that flows
    /// through locals/params/returns/fields like any other pointer, and <c>.Length</c>/the indexer can
    /// be read straight out of memory at the receiving end (a runtime load, not compile-time
    /// provenance-tracing) — see <c>CilMethodLowerer.Strings.cs</c>'s class remarks for why this
    /// representation is what unblocks a string PARAMETER (e.g. the graphics library's
    /// <c>Text.Draw(col, row, string text)</c>), and for why the bytes are ASCII, not UTF-16, and the
    /// non-ASCII-character diagnostic. Creates the global the first time this exact literal text is seen
    /// anywhere in the module. <paramref name="asciiBytes"/> is supplied by the caller (already
    /// validated/converted) rather than recomputed here, since only <c>CilMethodLowerer.LowerLdstr</c>
    /// has the method context needed to report a non-ASCII character with a useful diagnostic.</summary>
    public IrGlobal EnsureStringLiteralGlobal(string value, byte[] asciiBytes)
    {
        if (_stringLiteralGlobals.TryGetValue(value, out var g))
            return g;
        var blob = new byte[2 + asciiBytes.Length];
        blob[0] = (byte)(asciiBytes.Length & 0xFF);
        blob[1] = (byte)((asciiBytes.Length >> 8) & 0xFF);
        System.Array.Copy(asciiBytes, 0, blob, 2, asciiBytes.Length);
        g = new IrGlobal(
            $"__strlit.{_stringLiteralGlobals.Count}",
            IrType.Array(IrType.I8, blob.Length),
            AddressSpace.Rom,
            initializer: blob
        );
        Module.Globals.Add(g);
        _stringLiteralGlobals[value] = g;
        return g;
    }

    public void RegisterElidedCctorInstructions(
        MethodDefinition cctor,
        HashSet<Instruction> elided
    ) => _elidedCctorInstructions[cctor] = elided;

    public IReadOnlySet<Instruction> ElidedInstructionsFor(MethodDefinition method) =>
        _elidedCctorInstructions.TryGetValue(method, out var set) ? set : EmptyElidedSet;

    // One monomorphized generic-method instance per (open-generic template, mangled type-argument
    // suffix) — see CilMethodLowerer.Generics.cs. Keyed by suffix rather than by the concrete
    // TypeReference list itself: two instantiations at the "same" type can be distinct TypeReference
    // objects (imported from different call sites), so the mangled string (already the injective,
    // call-site-independent key CSharpFrontend's own routing relies on) is the only stable key. A
    // SEPARATE table from FunctionsByMethod is required, not an accident: FunctionsByMethod is keyed by
    // MethodDefinition alone, and one open-generic MethodDefinition maps to MANY IrFunctions here (one
    // per distinct instantiation) — reusing that table would alias every instantiation onto whichever
    // one happened to be lowered first.
    private readonly Dictionary<
        (MethodDefinition Template, string Suffix),
        IrFunction
    > _genericInstances = new();
    private readonly HashSet<(MethodDefinition Template, string Suffix)> _genericInProgress = new();

    /// <summary>Bump-pointer heap top, WRAM address — identical convention to
    /// <c>Koh.Compiler.Frontends.CSharp.CSharpFrontend.HeapTop</c> (not shared code: the two frontends
    /// compile disjoint modules, so only the convention, not the constant's storage, needs to agree).</summary>
    internal const int HeapTop = 0xDE00;
    internal const string HeapPointerName = "__heap";

    public CilLoweringContext(
        IrModule module,
        DiagnosticBag diagnostics,
        IReadOnlyDictionary<MethodDefinition, CilIntrinsicIndex.Entry> intrinsics,
        IReadOnlyDictionary<string, MethodDefinition> runtime,
        ModuleDefinition gameModule
    )
    {
        Module = module;
        Diagnostics = diagnostics;
        Intrinsics = intrinsics;
        Runtime = runtime;
        GameModule = gameModule;
    }

    /// <summary>Resolve and lower (on demand, exactly like <see cref="EnsureLowered"/> — the same
    /// referenced-assembly path <c>Koh.GameBoy.SoftFloat</c>'s routines travel) the <c>[KohRuntime(key)]</c>
    /// routine for <paramref name="key"/> — a float/double IL operation's (add/sub/mul/div/neg/compare/
    /// convert) implementation. A missing key is a diagnostic naming the exact key expected, never a
    /// silent miscompile (see CLAUDE.md's "never hardcode a routine name" rule for this frontend — the
    /// vocabulary lives entirely in <c>[KohRuntime]</c> metadata, so a gap here means the metadata
    /// doesn't cover this operation yet, not that the frontend guessed a name wrong).</summary>
    public IrFunction EnsureRuntime(string key)
    {
        if (!Runtime.TryGetValue(key, out var def))
            throw new CilNotSupportedException(
                $"no [KohRuntime(\"{key}\")] routine is registered (the CIL frontend routes float/"
                    + "double IL operations through Koh.GameBoy.SoftFloat; add a "
                    + $"[KohRuntime(\"{key}\")]-tagged method to supply this operation)."
            );
        return EnsureLowered(def)
            ?? throw new CilNotSupportedException(
                $"cannot lower runtime routine '{def.FullName}' for [KohRuntime(\"{key}\")]."
            );
    }

    public IrGlobal RegisterGlobal(int address)
    {
        if (_registerGlobals.TryGetValue(address, out var g))
            return g;
        g = new IrGlobal(
            $"Hardware.0x{address:X4}",
            IrType.I8,
            AddressSpace.Default,
            fixedAddress: address
        );
        Module.Globals.Add(g);
        _registerGlobals[address] = g;
        return g;
    }

    public IrGlobal RegionGlobal(int address)
    {
        if (_regionGlobals.TryGetValue(address, out var g))
            return g;
        g = new IrGlobal(
            $"Gb.0x{address:X4}",
            IrType.I8,
            AddressSpace.Default,
            fixedAddress: address
        );
        Module.Globals.Add(g);
        _regionGlobals[address] = g;
        return g;
    }

    public CilClassLayout GetLayout(TypeDefinition type)
    {
        if (_classLayouts.TryGetValue(type, out var layout))
            return layout;
        layout = CilClassLayout.Compute(type, GetLayout);
        _classLayouts[type] = layout;
        return layout;
    }

    /// <summary>The shared heap-pointer global, created the first time it's needed (see
    /// <c>CilModuleLowerer.NeedsHeap</c>'s pre-scan) — its <c>HeapTop</c> initializer is emitted once,
    /// in the entry function's prologue, by <see cref="CilMethodLowerer"/> when <c>isEntry</c> and this
    /// property is non-null.</summary>
    public IrGlobal EnsureHeapGlobal()
    {
        if (_heapGlobal is { } existing)
            return existing;
        var heap = new IrGlobal(HeapPointerName, IrType.I16, AddressSpace.Wram);
        Module.Globals.Add(heap);
        _heapGlobal = heap;
        return heap;
    }

    public IrGlobal? HeapGlobal => _heapGlobal;

    // ---- OAM DMA intrinsic (see CilMethodLowerer.LowerIntrinsicCall's "oamdma" case and the graphics
    // library design doc's build-plan slice 2) --------------------------------------------------------

    private IrGlobal? _oamDmaSourceGlobal;

    /// <summary>The one-byte WRAM scratch cell staging <c>Hardware.RunOamDma(sourcePage)</c>'s argument
    /// for the backend's boot-installed HRAM trampoline to read (see <see cref="Sm83Backend"/>'s
    /// "oamdma" gating — it looks this global up BY NAME, the same convention <see cref="HeapGlobal"/>'s
    /// own backend-side lookup uses). Created lazily the first time any call lowers to this intrinsic —
    /// unlike the heap pointer, it needs no boot-time seed value, so (unlike <see cref="EnsureHeapGlobal"/>)
    /// there is no eager pre-scan requirement: whichever call site references it first settles its
    /// placement, and the backend only ever reads its assigned address, never its (nonexistent) initial
    /// value.</summary>
    public IrGlobal EnsureOamDmaSourceGlobal()
    {
        if (_oamDmaSourceGlobal is { } existing)
            return existing;
        var g = new IrGlobal(OamDmaSourceGlobalName, IrType.I8, AddressSpace.Wram);
        Module.Globals.Add(g);
        _oamDmaSourceGlobal = g;
        return g;
    }

    /// <summary>The fixed name <see cref="Sm83Backend"/> looks this global up by (see
    /// <see cref="EnsureOamDmaSourceGlobal"/>'s remarks) — same convention as <see cref="HeapPointerName"/>.</summary>
    internal const string OamDmaSourceGlobalName = "__oamdma_src";

    /// <summary>Signature only (Pass 1 of the eager, hand-written-static-method sweep) — adds
    /// <paramref name="method"/> to <see cref="FunctionsByMethod"/> so later calls resolve regardless
    /// of declaration order. A per-method failure reports a diagnostic and leaves the method out (its
    /// body pass is then skipped).</summary>
    public IrFunction? EnsureSignature(MethodDefinition method)
    {
        if (FunctionsByMethod.TryGetValue(method, out var existing))
            return existing;
        try
        {
            // Struct-by-value RETURN is a diagnostic, not a lowering: parity with CSharpFrontend, whose
            // own return-type resolver has no struct path at all (see CilMethodLowerer.Structs.cs's
            // class remarks) — the backend's calling convention has no proven aggregate-by-value return
            // shape, unlike a byval struct PARAMETER (an ordinary address plus a call-site copy).
            if (CilStructSupport.ResolveStruct(method.ReturnType) is not null)
                throw new CilNotSupportedException(
                    $"'{method.FullName}' returns a struct by value, which the CIL frontend does not "
                        + "support (return it via an out/ref parameter instead)."
                );
            var parameters = new List<IrParameter>();
            if (method.HasThis)
            {
                // MapParam, not the plain Map: 'this' on a struct instance method is a byref to the
                // struct's own bytes (Pointer(I8), via CilStructSupport.ResolveStruct), exactly like any
                // other struct parameter (CilTypeMapper.MapParam's remarks) — Map alone has no struct
                // branch and throws "unsupported CIL type" for a value-type declaring type. For a class
                // 'this' MapParam falls through to Map's own reference-type shortcut, so this is a
                // strict superset of the previous behavior.
                var thisType = CilTypeMapper.MapParam(method.DeclaringType).IrType;
                parameters.Add(new IrParameter("this", thisType));
            }
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                var p = method.Parameters[i];
                var shape = CilTypeMapper.MapParam(p.ParameterType);
                parameters.Add(new IrParameter(p.Name ?? $"arg{i}", shape.IrType));
            }
            var (returnType, _) = CilTypeMapper.Map(method.ReturnType);
            var fn = new IrFunction(
                UniqueFunctionName($"{method.DeclaringType.Name}.{method.Name}"),
                returnType,
                parameters
            )
            {
                // Only a static method (mirrors CSharpFrontend, which only ever reads [Interrupt] off a
                // top-level/local function — never an instance method) can be an interrupt handler; an
                // instance method's implicit 'this' parameter has no receiver at interrupt time anyway.
                InterruptVector = method.HasThis ? null : InterruptVectorOf(method),
            };
            Module.Functions.Add(fn);
            FunctionsByMethod[method] = fn;
            return fn;
        }
        catch (CilNotSupportedException ex)
        {
            Diagnostics.Report(default, ex.Message, DiagnosticSeverity.Error, Module.Name);
            return null;
        }
    }

    // Every IR function name handed out so far. C# METHOD OVERLOADS share a simple name
    // ('Rng.Next()' and 'Rng.Next(byte)' are distinct MethodDefinitions but both map to "Rng.Next"),
    // and the linker treats function names as exported symbols — two same-named functions are a
    // duplicate-symbol link error (found the first time a program called two overloads of one
    // Framework method; latent for any two Text.Draw overloads too). Names are per-MethodDefinition
    // identity everywhere (FunctionsByMethod), so the NAME is display/symbol-only: uniquify on
    // collision, keeping the first occurrence's pretty name so existing single-overload programs
    // (and tests that look functions up by name) are unaffected.
    private readonly HashSet<string> _functionNames = new(StringComparer.Ordinal);

    private string UniqueFunctionName(string baseName)
    {
        if (_functionNames.Add(baseName))
            return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName}#{i}";
            if (_functionNames.Add(candidate))
                return candidate;
        }
    }

    /// <summary>Maps an <c>[Interrupt]</c> kind name (e.g. "VBlank") to its vector address. The sole
    /// consumer is <see cref="InterruptVectorOf"/>; kept as a small static table here now that the CIL
    /// frontend is the only frontend (formerly shared with the deleted <c>CSharpFrontend</c> via
    /// <c>HardwareRegisters.InterruptVector</c>).</summary>
    internal static int? InterruptVector(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "vblank" => 0x40,
            "stat" or "lcdstat" or "lcd" => 0x48,
            "timer" => 0x50,
            "serial" => 0x58,
            "joypad" => 0x60,
            _ => null,
        };

    /// <summary>Reads <c>[Interrupt(kind)]</c> (<c>Koh.GameBoy.InterruptAttribute</c>) off
    /// <paramref name="method"/>, matched by the attribute type's SIMPLE NAME ("InterruptAttribute") —
    /// same reasoning as <see cref="CilIntrinsicIndex"/>'s <c>[KohIntrinsic]</c> match: <c>Koh.Compiler</c>
    /// must never reference <c>Koh.GameBoy</c>. Maps the kind string through <see cref="InterruptVector"/>.
    /// A present-but-unrecognized kind (typo, wrong string) is a diagnostic rather than silently leaving
    /// the method an ordinary, never-invoked function.</summary>
    private int? InterruptVectorOf(MethodDefinition method)
    {
        foreach (var attr in method.CustomAttributes)
        {
            if (attr.AttributeType.Name != "InterruptAttribute")
                continue;
            var kind =
                attr.ConstructorArguments.Count > 0
                    ? attr.ConstructorArguments[0].Value as string
                    : null;
            var vector = InterruptVector(kind);
            if (vector is null)
                Diagnostics.Report(
                    default,
                    $"unknown interrupt kind '{kind ?? "?"}' on '{method.FullName}' "
                        + "(expected VBlank, Stat/LcdStat/Lcd, Timer, Serial, or Joypad).",
                    DiagnosticSeverity.Error,
                    Module.Name
                );
            return vector;
        }
        return null;
    }

    /// <summary>Monomorphize (or reuse an already-monomorphized) specialization of the open generic
    /// method <paramref name="template"/> at concrete type arguments <paramref name="concreteArgs"/>
    /// (already substituted through any ENCLOSING instantiation's own map — see
    /// <c>CilMethodLowerer.Generics.cs</c>'s <c>LowerGenericCall</c> for how transitive instantiation
    /// falls out of that), <paramref name="suffix"/> being the caller's own precomputed mangled name for
    /// it (<see cref="CilGenericSubst.MangledSuffix"/>). On-demand, exactly like <see cref="EnsureLowered"/>
    /// — a template is never swept eagerly (<c>CilModuleLowerer.Lower</c> skips every
    /// <c>HasGenericParameters</c> method), only specialized the first time some call site actually
    /// instantiates it. A per-instantiation failure (signature or body) reports a diagnostic and returns
    /// null/the signature-only stub respectively — mirrors <see cref="EnsureSignature"/>/<see cref="LowerBody"/>'s
    /// own split so a body failure doesn't retroactively invalidate an already-registered signature other
    /// call sites may share.</summary>
    public IrFunction? EnsureGenericInstance(
        MethodDefinition template,
        IReadOnlyList<TypeReference> concreteArgs,
        string suffix
    )
    {
        var key = (template, suffix);
        if (_genericInstances.TryGetValue(key, out var existing))
            return existing;

        IrFunction fn;
        try
        {
            fn = BuildGenericSignature(template, concreteArgs, suffix);
        }
        catch (CilNotSupportedException ex)
        {
            Diagnostics.Report(default, ex.Message, DiagnosticSeverity.Error, Module.Name);
            return null;
        }
        _genericInstances[key] = fn;

        if (!_genericInProgress.Add(key))
        {
            Diagnostics.Report(
                default,
                $"'{template.FullName}{suffix}' cannot be lowered (recursive generic instantiation).",
                DiagnosticSeverity.Error,
                Module.Name
            );
            return fn;
        }
        try
        {
            new CilMethodLowerer(template, fn, this, isEntry: false, concreteArgs).Run();
        }
        catch (CilNotSupportedException ex)
        {
            Diagnostics.Report(default, ex.Message, DiagnosticSeverity.Error, Module.Name);
        }
        finally
        {
            _genericInProgress.Remove(key);
        }
        return fn;
    }

    /// <summary>Build one generic instantiation's IR signature — the generic-aware sibling of
    /// <see cref="EnsureSignature"/>: every parameter/return type is run through
    /// <see cref="CilGenericSubst.Substitute"/> against <paramref name="concreteArgs"/> before mapping,
    /// since the template's own <see cref="MethodDefinition.Parameters"/>/<see cref="MethodDefinition.ReturnType"/>
    /// still name the OPEN type parameter (<c>!!0</c>) directly off Cecil metadata. Scope: a static
    /// method only (an instance generic method would need virtual/interface dispatch devirtualization
    /// layered on top of monomorphization — out of scope, matching <c>CSharpFrontend</c>, which has no
    /// generic-class/generic-instance-method support either) and scalar/pointer parameters/return only
    /// (a struct-typed one is a diagnostic here rather than a silent wrong-copy — byval struct
    /// semantics need PrepareArg's fresh-copy path, which <c>LowerGenericCall</c>'s own arg-prep does
    /// not thread through, unlike the ordinary call path's <c>PrepareArg</c>).</summary>
    private IrFunction BuildGenericSignature(
        MethodDefinition template,
        IReadOnlyList<TypeReference> concreteArgs,
        string suffix
    )
    {
        if (template.HasThis)
            throw new CilNotSupportedException(
                $"'{template.FullName}' is a generic INSTANCE method; the CIL frontend monomorphizes "
                    + "static generic methods only."
            );
        var substReturn = CilGenericSubst.Substitute(template.ReturnType, concreteArgs);
        if (CilStructSupport.ResolveStruct(substReturn) is not null)
            throw new CilNotSupportedException(
                $"'{template.FullName}{suffix}' returns a struct by value, which the CIL frontend does "
                    + "not support (return it via an out/ref parameter instead)."
            );
        var parameters = new List<IrParameter>();
        for (var i = 0; i < template.Parameters.Count; i++)
        {
            var p = template.Parameters[i];
            var substituted = CilGenericSubst.Substitute(p.ParameterType, concreteArgs);
            if (
                substituted is not ByReferenceType
                && CilStructSupport.ResolveStruct(substituted) is not null
            )
                throw new CilNotSupportedException(
                    $"'{template.FullName}{suffix}' has a struct-typed parameter, which generic "
                        + "monomorphization does not support yet."
                );
            var shape = CilTypeMapper.MapParam(substituted);
            parameters.Add(new IrParameter(p.Name ?? $"arg{i}", shape.IrType));
        }
        var (returnType, _) = CilTypeMapper.Map(substReturn);
        var fn = new IrFunction(
            UniqueFunctionName($"{template.DeclaringType.Name}.{template.Name}{suffix}"),
            returnType,
            parameters
        );
        Module.Functions.Add(fn);
        return fn;
    }

    /// <summary>Lower <paramref name="method"/> now if it hasn't been already: build its signature
    /// (see <see cref="EnsureSignature"/>) and its body. Used both for the eager Pass-2 sweep (hand-
    /// written statics) and, recursively, for on-demand resolution of a compiler-generated type's
    /// member the first time some call site actually references it (a delegate target, a display-
    /// class ctor, an instance method reached through a devirtualized <c>callvirt</c>). A cycle (a
    /// method whose own body's lowering re-enters this for itself, transitively) reports a diagnostic
    /// and returns the signature-only function rather than recursing forever — not reachable from the
    /// current subset (no recursive closures), but guarded rather than assumed away.</summary>
    public IrFunction? EnsureLowered(MethodDefinition method)
    {
        // Defense-in-depth for the same invariant CilMethodLowerer.LowerCall's referenced-assembly
        // branch already enforces with its own, call-site-specific message (see its remarks): the BCL
        // is never lowered, from ANY on-demand path (instance call, delegate target, constructor) — not
        // just the static-call path the referenced-assembly task opens up. Checked BEFORE EnsureSignature
        // so a BCL method never gets a dangling signature-only stub in FunctionsByMethod either.
        if (CilModuleLowerer.IsBclMethod(method))
        {
            Diagnostics.Report(
                default,
                $"'{method.FullName}' is a Base Class Library method; the CIL frontend cannot lower "
                    + "BCL IL (only [KohIntrinsic] members and code from a referenced non-BCL assembly "
                    + "are supported).",
                DiagnosticSeverity.Error,
                Module.Name
            );
            return null;
        }
        var fn = EnsureSignature(method);
        if (fn is null)
            return null;
        if (_lowered.Contains(method))
            return fn;
        if (!_inProgress.Add(method))
        {
            Diagnostics.Report(
                default,
                $"'{method.FullName}' cannot be lowered (recursive on-demand resolution).",
                DiagnosticSeverity.Error,
                Module.Name
            );
            return fn;
        }
        try
        {
            LowerBody(method, fn, isEntry: false);
        }
        finally
        {
            _inProgress.Remove(method);
        }
        return fn;
    }

    private readonly HashSet<MethodDefinition> _lowered = new();

    // A method's ret-site(s) concrete-type provenance (see CilMethodLowerer's Code.Ret case) — null
    // means "no known single concrete type" (either genuinely unknown, or two ret sites disagreed).
    // Used by iterator-kickoff/GetEnumerator-shaped devirtualization (see
    // CilMethodLowerer.Iterators.cs): a game-module method's declared return may be an interface, but
    // every ret it actually executes might still trace to the SAME concrete allocation.
    private readonly Dictionary<MethodDefinition, TypeDefinition?> _concreteReturnType = new();

    /// <summary>Record one ret site's concrete-type provenance for <paramref name="method"/> (called
    /// once per <c>ret</c> encountered while lowering its body). The first sighting sets it; a later
    /// sighting that disagrees (a different type, or no provenance at all) downgrades it to "unknown"
    /// — sound, not merely optimistic: every path must agree for the method's result to be trusted as
    /// exactly one concrete type.</summary>
    public void RecordConcreteReturn(MethodDefinition method, TypeDefinition? type)
    {
        if (_concreteReturnType.TryGetValue(method, out var existing))
        {
            if (!ReferenceEquals(existing, type))
                _concreteReturnType[method] = null;
        }
        else
        {
            _concreteReturnType[method] = type;
        }
    }

    /// <summary>The single concrete type every <c>ret</c> in <paramref name="method"/> traces to, or
    /// null if unknown/disagreeing — forces <paramref name="method"/> to be lowered first (idempotent
    /// via <see cref="EnsureLowered"/>) if it hasn't been yet, so this works regardless of whether the
    /// caller or the callee happens to lower first in the eager sweep.</summary>
    public TypeDefinition? GetConcreteReturnType(MethodDefinition method)
    {
        EnsureLowered(method);
        return _concreteReturnType.TryGetValue(method, out var type) ? type : null;
    }

    /// <summary>Lower one method body, reporting a diagnostic (not throwing) on an unsupported
    /// construct so one bad method doesn't sink the whole compile — same containment as
    /// <c>CSharpFrontend</c>'s per-method <c>CSharpNotSupportedException</c> handling.</summary>
    public void LowerBody(MethodDefinition method, IrFunction fn, bool isEntry)
    {
        if (!_lowered.Add(method))
            return;
        try
        {
            new CilMethodLowerer(method, fn, this, isEntry).Run();
        }
        catch (CilNotSupportedException ex)
        {
            Diagnostics.Report(default, ex.Message, DiagnosticSeverity.Error, Module.Name);
        }
    }
}
