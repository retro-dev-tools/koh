using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Cooperative-coroutine lowering: `yield return` iterators become MoveNext/Current state machines.
public sealed partial class CSharpFrontend
{
    /// <summary>Transform each <c>yield return</c> iterator into a cooperative-coroutine state machine:
    /// a state class with <c>__state</c>/<c>__current</c> fields, a <c>MoveNext()</c> that advances one
    /// step per call (a switch over the state), a <c>Current()</c> accessor, and a factory that replaces
    /// the original method. The caller drives it with <c>while (it.MoveNext() != 0) use(it.Current());</c>.
    /// Minimal form: a linear body of top-level <c>yield return</c> statements.</summary>
    private static CompilationUnitSyntax TransformIterators(CompilationUnitSyntax root)
    {
        var wrapper = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == "__KohProgram");
        if (wrapper is null)
            return root;

        var iterators = wrapper.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body is { } b && b.Statements.Count > 0 && b.Statements.All(s => s is YieldStatementSyntax))
            .ToList();
        if (iterators.Count == 0)
            return root;

        var factories = new Dictionary<MethodDeclarationSyntax, MemberDeclarationSyntax>();
        var stateClasses = new List<MemberDeclarationSyntax>();
        foreach (var m in iterators)
        {
            string name = m.Identifier.Text;
            string stateName = name + "__Iter";
            // Element type from IEnumerable<T> / IEnumerator<T>; default byte.
            string elem = m.ReturnType is GenericNameSyntax { TypeArgumentList.Arguments: [var ta] } ? ta.ToString() : "byte";
            var yields = m.Body!.Statements.Cast<YieldStatementSyntax>()
                .Where(y => y.Expression is not null).Select(y => y.Expression!.ToString()).ToList();

            var sb = new System.Text.StringBuilder();
            sb.Append($"class {stateName} {{ {elem} __current; byte __state; ");
            sb.Append($"{elem} Current() {{ return __current; }} ");
            sb.Append("byte MoveNext() { switch (__state) { ");
            for (int i = 0; i < yields.Count; i++)
                sb.Append($"case {i}: __current = {yields[i]}; __state = {i + 1}; return 1; ");
            sb.Append("} return 0; } }");
            stateClasses.Add(SyntaxFactory.ParseMemberDeclaration(sb.ToString())!);
            factories[m] = SyntaxFactory.ParseMemberDeclaration($"byte* {name}() {{ return new {stateName}(); }}")!;
        }

        var newWrapper = wrapper.ReplaceNodes(iterators, (orig, _) => factories[orig]).AddMembers(stateClasses.ToArray());
        return root.ReplaceNode(wrapper, newWrapper);
    }
}
