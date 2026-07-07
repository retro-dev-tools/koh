using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Cooperative-coroutine lowering: `yield return` iterators become MoveNext/Current state machines.
public sealed partial class CSharpFrontend
{
    /// <summary>Transform each <c>yield return</c> iterator into a cooperative-coroutine state machine:
    /// a state class with <c>__state</c>/<c>__current</c> fields (plus a field per parameter and per loop
    /// variable), a <c>MoveNext()</c> that advances one step per call and suspends between yields, a
    /// <c>Current()</c> accessor, and a factory that replaces the original method — capturing the
    /// arguments into the state object. The caller drives it with
    /// <c>while (it.MoveNext() != 0) use(it.Current());</c>.
    ///
    /// Supported bodies: a linear sequence of top-level <c>yield return</c> statements, or a single
    /// counted <c>for</c>/<c>while</c> loop whose body is one <c>yield return</c>. An iterator whose body
    /// is not one of these shapes is left untransformed (and later reported as an unsupported
    /// <c>yield</c>).</summary>
    private static CompilationUnitSyntax TransformIterators(CompilationUnitSyntax root)
    {
        var wrapper = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == "__KohProgram");
        if (wrapper is null)
            return root;

        // An iterator is any method whose body contains a `yield`.
        var iterators = wrapper.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body is { } b && b.DescendantNodes().OfType<YieldStatementSyntax>().Any())
            .ToList();
        if (iterators.Count == 0)
            return root;

        var factories = new Dictionary<MethodDeclarationSyntax, MemberDeclarationSyntax>();
        var stateClasses = new List<MemberDeclarationSyntax>();
        foreach (var m in iterators)
        {
            var built = BuildIteratorStateMachine(m);
            if (built is null)
                continue; // unsupported shape — leave the method untransformed so `yield` is diagnosed
            var (stateClass, factory) = built.Value;
            stateClasses.Add(stateClass);
            factories[m] = factory;
        }
        if (factories.Count == 0)
            return root;

        var newWrapper = wrapper.ReplaceNodes(factories.Keys, (orig, _) => factories[orig])
            .AddMembers(stateClasses.ToArray());
        return root.ReplaceNode(wrapper, newWrapper);
    }

    /// <summary>Build the state class and replacement factory for one iterator, or null if its body is
    /// not a supported shape.</summary>
    private static (MemberDeclarationSyntax StateClass, MemberDeclarationSyntax Factory)?
        BuildIteratorStateMachine(MethodDeclarationSyntax m)
    {
        string name = m.Identifier.Text;
        string stateName = name + "__Iter";
        // Element type from IEnumerable<T> / IEnumerator<T>; default byte.
        string elem = m.ReturnType is GenericNameSyntax { TypeArgumentList.Arguments: [var ta] } ? ta.ToString() : "byte";
        var statements = m.Body!.Statements;

        // A parameter named like a synthesized field would alias it (the factory's capture
        // `__it.__state = __state;` would overwrite the state counter). Leave such an iterator
        // untransformed rather than silently corrupt it.
        if (m.ParameterList.Parameters.Any(p => IsReservedIteratorName(p.Identifier.Text)))
            return null;

        // Fields shared by every shape: the yielded value, the state counter, and one field per
        // parameter (so the body can reference its arguments after the factory captures them).
        var fields = new StringBuilder($"{elem} __current; byte __state; ");
        foreach (var p in m.ParameterList.Parameters)
            fields.Append($"{p.Type} {p.Identifier.Text}; ");

        string? moveNext =
            BuildFlatMoveNext(statements)
            ?? BuildLoopMoveNext(statements, fields);
        if (moveNext is null)
            return null;

        var cls = new StringBuilder();
        cls.Append($"class {stateName} {{ {fields}");
        cls.Append($"{elem} Current() {{ return __current; }} ");
        cls.Append(moveNext);
        cls.Append('}');

        // The factory allocates the state object, copies each argument into its captured field, and
        // returns it (a class instance is a heap pointer, exposed to the caller as byte*).
        var factory = new StringBuilder($"byte* {name}{m.ParameterList} {{ {stateName} __it = new {stateName}(); ");
        foreach (var p in m.ParameterList.Parameters)
            factory.Append($"__it.{p.Identifier.Text} = {p.Identifier.Text}; ");
        factory.Append("return __it; }");

        // If the synthesized source doesn't parse to a member (e.g. a parameter type that stringifies to
        // something unexpected), leave the iterator untransformed rather than propagate a null — the
        // remaining `yield` is then reported as unsupported.
        if (SyntaxFactory.ParseMemberDeclaration(cls.ToString()) is not { } stateClass
            || SyntaxFactory.ParseMemberDeclaration(factory.ToString()) is not { } factoryDecl)
            return null;
        return (stateClass, factoryDecl);
    }

    /// <summary>A linear sequence of top-level <c>yield return</c> statements → a switch that returns one
    /// per call. Null if the body is not exactly that shape.</summary>
    private static string? BuildFlatMoveNext(SyntaxList<StatementSyntax> statements)
    {
        if (statements.Count == 0 || !statements.All(s => s is YieldStatementSyntax { Expression: not null }))
            return null;
        var yields = statements.Cast<YieldStatementSyntax>().Select(y => y.Expression!.ToString()).ToList();

        var sb = new StringBuilder("byte MoveNext() { switch (__state) { ");
        for (int i = 0; i < yields.Count; i++)
            sb.Append($"case {i}: __current = {yields[i]}; __state = {i + 1}; return 1; ");
        sb.Append("} return 0; }");
        return sb.ToString();
    }

    /// <summary>A single counted <c>for</c> loop whose body is one <c>yield return</c> → a resumable
    /// loop: state 0 runs the initializer, later re-entries run the increment, and each call advances one
    /// iteration. Null if the body is not that shape. The loop variables declared in the initializer are
    /// appended to <paramref name="fields"/>.
    ///
    /// A <c>while</c> loop is deliberately NOT supported: a single-<c>yield</c> body cannot mutate the
    /// state the condition reads, so a counted <c>while</c> would compile to an infinite stream — it is
    /// left untransformed (and reported as an unsupported <c>yield</c>) instead of miscompiled.</summary>
    private static string? BuildLoopMoveNext(SyntaxList<StatementSyntax> statements, StringBuilder fields)
    {
        if (statements is not [ForStatementSyntax f]
            || f.Declaration is null || f.Condition is null)
            return null;

        var inits = new StringBuilder();
        foreach (var v in f.Declaration.Variables)
        {
            if (IsReservedIteratorName(v.Identifier.Text) || v.Initializer is null)
                return null;
            fields.Append($"{f.Declaration.Type} {v.Identifier.Text}; ");
            inits.Append($"{v.Identifier.Text} = {v.Initializer.Value}; ");
        }
        string increments = string.Concat(f.Incrementors.Select(e => $"{e}; "));
        string cond = f.Condition.ToString();
        string? yield = SingleYield(f.Statement);
        if (yield is null)
            return null;

        // __state: 0 = before first iteration, 1 = iterating, 2 = exhausted.
        var sb = new StringBuilder("byte MoveNext() { if (__state == 2) { return 0; } ");
        sb.Append($"if (__state == 0) {{ {inits}__state = 1; }} else {{ {increments}}} ");
        sb.Append($"if ({cond}) {{ __current = {yield}; return 1; }} __state = 2; return 0; }}");
        return sb.ToString();
    }

    /// <summary>Names the synthesized state machine reserves for its own fields/local — a parameter or
    /// loop variable using one of these would alias a synthesized field and corrupt iteration.</summary>
    private static bool IsReservedIteratorName(string name) =>
        name is "__current" or "__state" or "__it";

    /// <summary>The expression of a loop body that is exactly one <c>yield return E;</c> (bare or a block
    /// wrapping a single yield), or null.</summary>
    private static string? SingleYield(StatementSyntax body)
    {
        var y = body switch
        {
            YieldStatementSyntax ys => ys,
            BlockSyntax { Statements: [YieldStatementSyntax ys] } => ys,
            _ => null,
        };
        return y is { Expression: not null } ? y.Expression!.ToString() : null;
    }
}
