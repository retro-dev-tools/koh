using Koh.Core.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Monomorphization: generic methods are specialized per concrete instantiation before lowering.
public sealed partial class CSharpFrontend
{
    /// <summary>Rewrites a generic method's syntax, replacing each type-parameter name with the concrete
    /// type it is instantiated at (monomorphization).</summary>
    private sealed class TypeParamRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<string, TypeSyntax> _map;

        public TypeParamRewriter(IReadOnlyDictionary<string, TypeSyntax> map) => _map = map;

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            _map.TryGetValue(node.Identifier.Text, out var t)
                ? t.WithTriviaFrom(node)
                : base.VisitIdentifierName(node);
    }

    /// <summary>The mangled name of a generic method instantiated at concrete type arguments, e.g.
    /// <c>Max&lt;byte&gt;</c> becomes <c>Max__g1_4_byte</c>. Both the synthesized function and its call
    /// sites use it.
    ///
    /// The name must be a legal C# identifier: a later migration phase moves monomorphized instances out
    /// of detached syntax and into a second, constructed compilation unit that Roslyn actually binds, and
    /// a real <c>MethodDeclarationSyntax</c> identifier has to parse as one — a punctuation-bearing name
    /// like the pre-migration <c>Max$1_4_byte</c> never could.
    ///
    /// Scheme: <c>name + "__g" + argCount + ("_" + enc.Length + "_" + enc)*</c>, where <c>enc</c> is
    /// <see cref="EncodeTypeArg"/> applied to each type argument's own source text
    /// (<c>TypeSyntax.ToString()</c>). This is injective:
    /// - Each encoded argument is prefixed by the *encoded* text's own length, so two distinct argument
    ///   lists can never alias by picking a different split point — one argument <c>A_B</c> vs two
    ///   arguments <c>A</c>, <c>B</c> land at different lengths/offsets and can't collide the way a plain
    ///   <c>_</c>-join would.
    /// - <see cref="EncodeTypeArg"/> doubles every literal <c>_</c> in the source text, which disambiguates
    ///   a real underscore from a hex escape: a hex escape is always a single <c>_</c> followed by exactly
    ///   two hex digits, a shape doubled underscores can never produce, so the encoded stream (and hence
    ///   the length prefix computed from it) has one unambiguous reading.
    ///
    /// Collisions with a user-written identifier are possible in principle (nothing stops a user from
    /// literally naming a method <c>Max__g1_4_byte</c>) but never silent: two functions would then share
    /// one mangled name, and Pass 1's existing duplicate-function-name diagnostic fires — the same loud
    /// failure as any other accidental name clash in the program.</summary>
    internal static string MangleGeneric(string name, IEnumerable<TypeSyntax> typeArgs) =>
        name + MangleSuffix(typeArgs);

    /// <summary>The mangled-args part of <see cref="MangleGeneric"/> — everything it appends after the
    /// bare method name — factored out so a caller can compute it standalone (e.g. to key a generic call
    /// site's own type-argument list against a synthesized instance without reconstructing the whole
    /// mangled name).</summary>
    internal static string MangleSuffix(IEnumerable<TypeSyntax> typeArgs)
    {
        var args = typeArgs.ToList();
        var sb = new System.Text.StringBuilder("__g").Append(args.Count);
        foreach (var t in args)
        {
            var enc = EncodeTypeArg(t.ToString());
            sb.Append('_').Append(enc.Length).Append('_').Append(enc);
        }
        return sb.ToString();
    }

    /// <summary>Injectively sanitizes a type argument's source text into a legal fragment of a C#
    /// identifier: <c>[A-Za-z0-9]</c> passes through unchanged, a literal <c>_</c> doubles to <c>__</c>,
    /// and any other character (<c>*</c>, <c>[</c>, <c>]</c>, <c>&lt;</c>, <c>&gt;</c>, <c>.</c>, <c>,</c>,
    /// space, ...) becomes <c>_</c> followed by its char code as lowercase 2-hex (so <c>byte*</c> becomes
    /// <c>byte_2a</c>); a char above <c>0xFF</c> (a non-Latin identifier letter) gets a distinct
    /// <c>_u</c> + 4-hex escape, since letting the 2-hex form grow to 4 digits would be ambiguous — the
    /// digits are legal pass-through characters, so <c>_1234</c> must always mean escape(0x12) then
    /// literal <c>34</c>. See <see cref="MangleGeneric"/> for why this must be injective and how the
    /// underscore-doubling makes hex escapes unambiguous.</summary>
    internal static string EncodeTypeArg(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (c == '_')
                sb.Append("__");
            else if (c <= 0xFF)
                sb.Append('_').Append(((int)c).ToString("x2"));
            else
                sb.Append("_u").Append(((int)c).ToString("x4"));
        }
        return sb.ToString();
    }

    private static IEnumerable<(
        string? Receiver,
        string Name,
        TypeArgumentListSyntax Args,
        InvocationExpressionSyntax Node
    )> FindGenericInvocations(SyntaxNode node) =>
        node.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(inv =>
                inv.Expression switch
                {
                    // A bare `M<...>(...)` call.
                    GenericNameSyntax g => (
                        (string?)null,
                        g.Identifier.Text,
                        g.TypeArgumentList,
                        inv
                    ),
                    // A qualified `Class.M<...>(...)` call.
                    MemberAccessExpressionSyntax
                    {
                        Name: GenericNameSyntax g,
                        Expression: IdentifierNameSyntax r
                    } => (r.Identifier.Text, g.Identifier.Text, g.TypeArgumentList, inv),
                    _ => default,
                }
            )
            .Where(t => t.Item2 is not null);

    /// <summary>The enclosing top-level user <c>static class</c> of a node, or null at the wrapper level.</summary>
    private static string? EnclosingStaticClass(SyntaxNode node) =>
        node.Ancestors()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(IsProgramStaticClass)
            ?.Identifier.Text;

    /// <summary>Collect every generic method template — a program-level (see <see cref="IsWrapperMember"/>)
    /// method with type parameters — keyed by (declaring class, name, type-parameter count): an invocation
    /// carries a name and a type-argument count, and that pair selects the template, so <c>Wrap&lt;T&gt;</c>
    /// and <c>Wrap&lt;T,U&gt;</c> stay distinct. Two templates sharing both (a value-arity overload like
    /// <c>Max&lt;T&gt;(T,T)</c> vs <c>Max&lt;T&gt;(T,T,T)</c>) would mangle to the same specialized name, so
    /// that is reported rather than silently mis-specialized. Also rejects a parameter/local that shadows a
    /// type parameter by name, since monomorphization substitutes type-parameter names by identifier text
    /// alone (a shadowing local would be rewritten to the concrete type along with real uses).</summary>
    private static Dictionary<
        (string? Class, string Name, int Arity),
        MethodDeclarationSyntax
    > CollectGenericTemplates(CompilationUnitSyntax root, DiagnosticBag diagnostics)
    {
        var genericMethods =
            new Dictionary<(string? Class, string Name, int Arity), MethodDeclarationSyntax>();
        foreach (
            var m in root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.TypeParameterList is { Parameters.Count: > 0 } && IsWrapperMember(m))
        )
        {
            int arity = m.TypeParameterList!.Parameters.Count;
            var key = (ProgramMemberClass(m), m.Identifier.Text, arity);
            if (!genericMethods.TryAdd(key, m))
                Report(
                    diagnostics,
                    $"generic method '{m.Identifier.Text}' with {arity} type parameter(s) is declared "
                        + "more than once; overloaded generic methods are not supported.",
                    m.Identifier.GetLocation()
                );

            var typeParams = m
                .TypeParameterList.Parameters.Select(tp => tp.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);
            var shadow = m
                .ParameterList.Parameters.Select(p => p.Identifier.Text)
                .Concat(
                    m.DescendantNodes()
                        .OfType<VariableDeclaratorSyntax>()
                        .Select(v => v.Identifier.Text)
                )
                .FirstOrDefault(typeParams.Contains);
            if (shadow is not null)
                Report(
                    diagnostics,
                    $"in generic method '{m.Identifier.Text}', '{shadow}' shadows a type parameter of the "
                        + "same name; rename the value.",
                    m.Identifier.GetLocation()
                );
        }
        return genericMethods;
    }

    /// <summary>One monomorphized generic instance produced by <see cref="SynthesizeGenericInstances"/>:
    /// the specialized <see cref="MethodDeclarationSyntax"/> (annotated with
    /// <see cref="InstanceIndexAnnotation"/>), the generic template it was specialized from (a live node in
    /// the main tree — used to recover the owning class via <see cref="ProgramMemberClass"/> when nesting
    /// it into the instances tree), and the mangled suffix its concrete type arguments produced (for a
    /// later phase's call-site routing).</summary>
    internal readonly record struct GenericInstance(
        MethodDeclarationSyntax Instance,
        MethodDeclarationSyntax Template,
        string MangledSuffix
    );

    /// <summary>Annotation kind marking a monomorphized generic instance's synthesis/worklist order.
    /// Placing a node into the constructed instances tree (<see cref="BuildInstancesTree"/>) gives it fresh
    /// (red-node) identity, so this is the only way to recover "the i'th instance" from the tree Pass 1
    /// actually lowers from — <c>module.Functions</c> order must not depend on which nesting bucket
    /// (bare vs. per-owner) an instance landed in, only on this index.</summary>
    internal const string InstanceIndexAnnotation = "Koh.InstanceIndex";

    /// <summary>Monomorphize: for every generic method invoked with concrete type arguments, synthesize
    /// a specialized copy (type parameters substituted, mangled name). A work-list handles transitive
    /// instantiation — a specialized body may name further generic instances. Instances are named per
    /// declaring class, so same-named generics in different static classes stay distinct.</summary>
    private static List<GenericInstance> SynthesizeGenericInstances(
        CompilationUnitSyntax root,
        IReadOnlyDictionary<
            (string? Class, string Name, int Arity),
            MethodDeclarationSyntax
        > generics
    )
    {
        if (generics.Count == 0)
            return []; // no generic templates — skip the invocation scan entirely (the common program)

        var done = new Dictionary<
            string,
            (MethodDeclarationSyntax Template, MethodDeclarationSyntax Instance, string Suffix)
        >(StringComparer.Ordinal);
        var work = new Queue<(MethodDeclarationSyntax Template, TypeArgumentListSyntax Args)>();
        var templates = new HashSet<MethodDeclarationSyntax>(generics.Values);

        // Resolve an invocation to its template: a qualified `Recv.M<..>` selects Recv's method; a bare
        // `M<..>` prefers the enclosing static class's method, else a top-level one.
        MethodDeclarationSyntax? Resolve(
            string? receiver,
            string? enclosing,
            string name,
            int arity
        ) =>
            receiver is not null
                ? generics.GetValueOrDefault((receiver, name, arity))
                : generics.GetValueOrDefault((enclosing, name, arity))
                    ?? generics.GetValueOrDefault((null, name, arity));

        // Seed from concrete instantiations only — invocations inside a generic template still name
        // type parameters (e.g. Id<T>), which become concrete once the template is specialized.
        foreach (var (receiver, name, args, node) in FindGenericInvocations(root))
            if (
                !node.Ancestors().OfType<MethodDeclarationSyntax>().Any(templates.Contains)
                && Resolve(receiver, EnclosingStaticClass(node), name, args.Arguments.Count)
                    is { } tmpl
            )
                work.Enqueue((tmpl, args));

        while (work.Count > 0)
        {
            var (template, args) = work.Dequeue();
            var owner = ProgramMemberClass(template);
            var suffix = MangleSuffix(args.Arguments);
            var mangled = template.Identifier.Text + suffix;
            var qualified = owner is { } c ? $"{c}.{mangled}" : mangled;
            if (done.ContainsKey(qualified))
                continue;

            var map = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
            var tps = template.TypeParameterList!.Parameters;
            for (int i = 0; i < tps.Count && i < args.Arguments.Count; i++)
                map[tps[i].Identifier.Text] = args.Arguments[i];

            var specialized = (MethodDeclarationSyntax)new TypeParamRewriter(map).Visit(template)!;
            specialized = specialized
                .WithIdentifier(SyntaxFactory.Identifier(mangled))
                .WithTypeParameterList(null)
                .WithAdditionalAnnotations(
                    new SyntaxAnnotation(InstanceIndexAnnotation, done.Count.ToString())
                );
            done[qualified] = (template, specialized, suffix);

            // The specialized body's generic invocations are now concrete; instantiate them too,
            // resolving sibling generics against this template's own class.
            foreach (var (receiver, name, a2, _) in FindGenericInvocations(specialized))
                if (Resolve(receiver, owner, name, a2.Arguments.Count) is { } tmpl2)
                    work.Enqueue((tmpl2, a2));
        }
        return done
            .Values.Select(v => new GenericInstance(v.Instance, v.Template, v.Suffix))
            .ToList();
    }

    /// <summary>Build the second, constructed tree that houses every monomorphized generic instance (see
    /// the Stage-2 plan's "instances tree" design): a <c>static partial class __KohProgram</c> matching the
    /// main tree's own (now-partial) wrapper, mirroring only the nesting shape of each instance's template —
    /// an instance whose template is a direct wrapper member goes directly under the partial wrapper; one
    /// whose template lives in a user top-level <c>static class Owner</c> goes inside a synthesized
    /// <c>static partial class Owner</c> nested in the wrapper. No other members are mirrored. Built via
    /// <see cref="SyntaxFactory"/> node construction (not a text round-trip), so the annotated
    /// <see cref="MethodDeclarationSyntax"/> instances are the exact nodes placed into the new tree — though
    /// placing them re-roots (and so re-identifies) every node; see <see cref="InstanceIndexAnnotation"/>
    /// for how a caller recovers them afterward. Roslyn merges same-named partial declarations across every
    /// tree in the compilation before any partial-completeness check, so a sibling/cross-class reference
    /// inside an instance body binds against the *real* declarations living in the main tree even though the
    /// main tree's <c>Owner</c> is not itself written <c>partial</c> (a CS0260 mismatch — never a gate, see
    /// the design's Roslyn-diagnostics policy; pinned by the spike test in
    /// <c>CSharpInstancesTreeTests.PartialMerge_NonPartialMainDecl_StillBindsMembersAcrossTrees</c>).
    /// Returns null when there are no generic instances at all (the common program), so callers can treat
    /// "no instances tree" as the normal case rather than a dummy empty tree.</summary>
    private static SyntaxTree? BuildInstancesTree(IReadOnlyList<GenericInstance> instances)
    {
        if (instances.Count == 0)
            return null;

        var staticPartial = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.StaticKeyword),
            SyntaxFactory.Token(SyntaxKind.PartialKeyword)
        );

        var bareMembers = new List<MemberDeclarationSyntax>();
        var byOwner = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
        var ownerOrder = new List<string>();

        foreach (var inst in instances)
        {
            var owner = ProgramMemberClass(inst.Template);
            if (owner is null)
            {
                bareMembers.Add(inst.Instance);
                continue;
            }
            if (!byOwner.TryGetValue(owner, out var list))
            {
                list = [];
                byOwner[owner] = list;
                ownerOrder.Add(owner);
            }
            list.Add(inst.Instance);
        }

        var wrapperMembers = new List<MemberDeclarationSyntax>(bareMembers);
        foreach (var owner in ownerOrder)
            wrapperMembers.Add(
                SyntaxFactory
                    .ClassDeclaration(owner)
                    .WithModifiers(staticPartial)
                    .WithMembers(SyntaxFactory.List(byOwner[owner]))
            );

        var wrapper = SyntaxFactory
            .ClassDeclaration(WrapperClassName)
            .WithModifiers(staticPartial)
            .WithMembers(SyntaxFactory.List(wrapperMembers));

        var compilationUnit = SyntaxFactory
            .CompilationUnit()
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(wrapper));

        return CSharpSyntaxTree.Create(compilationUnit, path: "__KohGenericInstances.cs");
    }
}
