using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class CharSourceTests
{
    // ─────────────────────────────────────────────────────────────
    // StringCharSource basics
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task StringCharSource_Peek_Idempotent()
    {
        var source = new StringCharSource("abc");
        var first = source.Peek();
        var second = source.Peek();
        await Assert.That(first.Value).IsEqualTo('a');
        await Assert.That(second.Value).IsEqualTo('a');
    }

    [Test]
    public async Task StringCharSource_Read_Advances()
    {
        var source = new StringCharSource("ab");
        var a = source.Read();
        var b = source.Peek();
        await Assert.That(a.Char).IsEqualTo('a');
        await Assert.That(b.Char).IsEqualTo('b');
    }

    [Test]
    public async Task StringCharSource_Eof()
    {
        var source = new StringCharSource("");
        var sc = source.Peek();
        await Assert.That(sc.IsEof).IsTrue();
        await Assert.That(sc.Value).IsEqualTo(-1);
    }

    [Test]
    public async Task StringCharSource_Origin_TracksOffset()
    {
        var source = new StringCharSource("ab", "test.asm");
        source.Read(); // consume 'a'
        var b = source.Peek();
        await Assert.That(b.Origin.Offset).IsEqualTo(1);
        await Assert.That(b.Origin.FilePath).IsEqualTo("test.asm");
        await Assert.That(b.Origin.InterpolationFrame).IsNull();
    }

    // ─────────────────────────────────────────────────────────────
    // InterpolationAwareSource
    // ─────────────────────────────────────────────────────────────

    private static InterpolationAwareSource CreateSource(string text,
        IInterpolationResolver resolver, DiagnosticBag? diags = null)
    {
        var inner = new StringCharSource(text);
        return new InterpolationAwareSource(inner, resolver, diags ?? new DiagnosticBag());
    }

    private static string ReadAll(ICharSource source)
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var sc = source.Read();
            if (sc.IsEof) break;
            sb.Append(sc.Char);
        }
        return sb.ToString();
    }

    [Test]
    public async Task Interpolation_SimpleExpansion()
    {
        var resolver = new MockResolver(new() { ["sym"] = "hello" });
        var source = CreateSource("{sym} world", resolver);
        var result = ReadAll(source);
        await Assert.That(result).IsEqualTo("hello world");
    }

    [Test]
    public async Task Interpolation_MultipleExpansions()
    {
        var resolver = new MockResolver(new() { ["a"] = "X", ["b"] = "Y" });
        var source = CreateSource("{a}+{b}", resolver);
        var result = ReadAll(source);
        await Assert.That(result).IsEqualTo("X+Y");
    }

    [Test]
    public async Task Interpolation_NestedExpansion()
    {
        // {meaning} resolves to "answer", then {answer} resolves to "42"
        var resolver = new MockResolver(new()
        {
            ["meaning"] = "answer",
            ["answer"] = "42",
        });
        var source = CreateSource("{{meaning}}", resolver);
        var result = ReadAll(source);
        await Assert.That(result).IsEqualTo("42");
    }

    [Test]
    public async Task Interpolation_FormatSpecifier()
    {
        var resolver = new FormatCapturingResolver();
        var source = CreateSource("{#05x:sym}", resolver);
        ReadAll(source);

        await Assert.That(resolver.LastName).IsEqualTo("sym");
        await Assert.That(resolver.LastFormat).IsNotNull();
        await Assert.That(resolver.LastFormat!.Exact).IsTrue();
        await Assert.That(resolver.LastFormat!.ZeroPad).IsTrue();
        await Assert.That(resolver.LastFormat!.Width).IsEqualTo(5);
        await Assert.That(resolver.LastFormat!.Type).IsEqualTo('x');
    }

    [Test]
    public async Task Interpolation_EscapedBraceInStringMode()
    {
        var resolver = new MockResolver(new() { ["x"] = "BAD" });
        var source = CreateSource("test", resolver);

        // Build a string with \{ inside string mode
        var inner = new StringCharSource("\\{x}");
        var diags = new DiagnosticBag();
        var interpolated = new InterpolationAwareSource(inner, resolver, diags);
        interpolated.EnterDoubleQuotedStringMode();
        var result = ReadAll(interpolated);

        // \{ should NOT trigger interpolation — emits literal \{x}
        await Assert.That(result).IsEqualTo("\\{x}");
    }

    [Test]
    public async Task Interpolation_BraceOutsideStringMode_AlwaysExpands()
    {
        var resolver = new MockResolver(new() { ["x"] = "42" });
        var source = CreateSource("{x}", resolver);
        var result = ReadAll(source);
        await Assert.That(result).IsEqualTo("42");
    }

    [Test]
    public async Task Interpolation_NotFound_ReportsDiagnostic()
    {
        var resolver = new MockResolver(new()); // empty — nothing resolves
        var diags = new DiagnosticBag();
        var source = CreateSource("{missing}", resolver, diags);
        ReadAll(source);

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsGreaterThan(0);
        await Assert.That(errors[0].Message).Contains("missing");
    }

    [Test]
    public async Task Interpolation_DepthLimit_ReportsDiagnostic()
    {
        // Create a resolver that always expands to another interpolation
        var resolver = new InfiniteResolver();
        var diags = new DiagnosticBag();
        var source = CreateSource("{loop}", resolver, diags);
        ReadAll(source);

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsGreaterThan(0);
        await Assert.That(errors.Any(e => e.Message.Contains("depth"))).IsTrue();
    }

    [Test]
    public async Task Interpolation_ReExpansion()
    {
        // Expanded text contains another {sym2} which should re-expand
        var resolver = new MockResolver(new()
        {
            ["sym1"] = "val={sym2}",
            ["sym2"] = "inner",
        });
        var source = CreateSource("{sym1}", resolver);
        var result = ReadAll(source);
        await Assert.That(result).IsEqualTo("val=inner");
    }

    [Test]
    public async Task Interpolation_Eof_NoExpansion()
    {
        var resolver = new MockResolver(new());
        var source = CreateSource("", resolver);
        var sc = source.Peek();
        await Assert.That(sc.IsEof).IsTrue();
    }

    [Test]
    public async Task Interpolation_Peek_Idempotent()
    {
        var resolver = new MockResolver(new() { ["x"] = "hello" });
        var source = CreateSource("{x}", resolver);
        var first = source.Peek();
        var second = source.Peek();
        await Assert.That(first.Value).IsEqualTo(second.Value);
        await Assert.That(first.Char).IsEqualTo('h');
    }

    [Test]
    public async Task Interpolation_Provenance_HasInterpolationFrame()
    {
        var resolver = new MockResolver(new() { ["sym"] = "ab" });
        var source = CreateSource("{sym}", resolver);
        var a = source.Read();
        await Assert.That(a.Char).IsEqualTo('a');
        await Assert.That(a.Origin.InterpolationFrame).IsNotNull();
        await Assert.That(a.Origin.InterpolationFrame!.ExpandedText).IsEqualTo("ab");
    }

    [Test]
    public async Task Interpolation_UnclosedBrace_ReportsDiagnostic()
    {
        var resolver = new MockResolver(new() { ["x"] = "42" });
        var diags = new DiagnosticBag();
        var source = CreateSource("{x", resolver, diags);
        ReadAll(source);

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsGreaterThan(0);
        await Assert.That(errors.Any(e => e.Message.Contains("}"))).IsTrue();
    }

    [Test]
    public async Task FormatParse_FullSpec()
    {
        var fmt = InterpolationFormat.Parse("+#-05.3q8x");
        await Assert.That(fmt).IsNotNull();
        await Assert.That(fmt!.Sign).IsEqualTo('+');
        await Assert.That(fmt.Exact).IsTrue();
        await Assert.That(fmt.LeftAlign).IsTrue();
        await Assert.That(fmt.ZeroPad).IsTrue();
        await Assert.That(fmt.Width).IsEqualTo(5);
        await Assert.That(fmt.FracDigits).IsEqualTo(3);
        await Assert.That(fmt.FixedPrec).IsEqualTo(8);
        await Assert.That(fmt.Type).IsEqualTo('x');
    }

    [Test]
    public async Task FormatParse_SimpleType()
    {
        var fmt = InterpolationFormat.Parse("d");
        await Assert.That(fmt).IsNotNull();
        await Assert.That(fmt!.Type).IsEqualTo('d');
        await Assert.That(fmt.Sign).IsNull();
        await Assert.That(fmt.Width).IsNull();
    }

    [Test]
    public async Task FormatParse_InvalidType_ReturnsNull()
    {
        var fmt = InterpolationFormat.Parse("z");
        await Assert.That(fmt).IsNull();
    }

    [Test]
    public async Task FormatParse_Empty_ReturnsNull()
    {
        var fmt = InterpolationFormat.Parse("");
        await Assert.That(fmt).IsNull();
    }

    // ─────────────────────────────────────────────────────────────
    // Test helpers
    // ─────────────────────────────────────────────────────────────

    private sealed class MockResolver : IInterpolationResolver
    {
        private readonly Dictionary<string, string> _symbols;

        public MockResolver(Dictionary<string, string> symbols)
        {
            _symbols = symbols;
        }

        public InterpolationResult Resolve(string name, InterpolationFormat? format)
        {
            if (_symbols.TryGetValue(name, out var value))
                return new InterpolationResult.Success(value);
            return new InterpolationResult.NotFound(name);
        }
    }

    private sealed class FormatCapturingResolver : IInterpolationResolver
    {
        public string? LastName { get; private set; }
        public InterpolationFormat? LastFormat { get; private set; }

        public InterpolationResult Resolve(string name, InterpolationFormat? format)
        {
            LastName = name;
            LastFormat = format;
            return new InterpolationResult.Success("formatted");
        }
    }

    private sealed class InfiniteResolver : IInterpolationResolver
    {
        public InterpolationResult Resolve(string name, InterpolationFormat? format)
        {
            // Always expand to another interpolation — should hit depth limit
            return new InterpolationResult.Success("{loop}");
        }
    }
}
