using Koh.Compiler.Frontends.CSharp;
using Koh.Core.Diagnostics;
using Koh.Core.Text;

namespace Koh.Compiler.Tests;

/// <summary>Phase 5 of the semantic-model migration: diagnostics polish. <c>MethodLowerer</c>'s
/// "unresolved name" error sites (unknown identifier, unsupported member access, enum member not found,
/// unsupported call target, instance-method/struct-field not found — see
/// <c>MethodLowerer.BetterUnresolvedMessage</c>) now check for a whitelisted Roslyn diagnostic
/// (CS0103/CS0117/CS1061/CS0246) at the same span and, if one exists, use its clearer message text
/// instead of Koh's generic one. This never changes whether a program compiles: Roslyn diagnostics are
/// never a gate (Koh-legal code is routinely C#-illegal — mixed-width arithmetic, non-`unsafe` pointer
/// math — and must stay diagnostic-free regardless of what Roslyn itself would say about it), and the
/// enrichment only fires after Koh's own lowering has already decided to fail. These tests cover: the
/// enriched wording on a genuinely-unresolved name (a) and member (b), that ordinary Koh-legal-but-C#-
/// illegal programs — including the full <c>gb-2048-cs</c> sample — still lower with zero diagnostics
/// (c), that the enriched message still reports through the unchanged wrapper-offset span math (d), and
/// that a detached monomorphized-generic body (no symbol to consult) still gets Koh's plain original
/// message rather than throwing or leaking a Roslyn artifact (e).</summary>
public class CSharpDiagnosticsTests
{
    private static DiagnosticBag Diagnostics(string src)
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        return diagnostics;
    }

    private static Diagnostic OnlyError(string src)
    {
        var diagnostics = Diagnostics(src).ToList();
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count != 1)
            throw new InvalidOperationException(
                $"expected exactly one error diagnostic, got {errors.Count}: "
                    + string.Join(" | ", diagnostics.Select(d => d.ToString()))
            );
        return errors[0];
    }

    // ---- (a) unresolved identifier / call target: Roslyn's message replaces Koh's generic one --------

    [Test]
    public async Task UnresolvedCallTarget_UsesRoslynsClearerMessage()
    {
        var error = OnlyError("static byte Main() { return Ghost(); }");
        await Assert.That(error.Message).Contains("does not exist in the current context");
        await Assert.That(error.Message).Contains("'Ghost'");
        // Still a Koh diagnostic, not a leaked Roslyn one: lowercase sentence start, trailing period,
        // matching the surrounding codebase's diagnostic style (see MethodLowerer.KohStyle).
        await Assert.That(error.Message.StartsWith("the ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(error.Message.EndsWith('.')).IsTrue();
    }

    [Test]
    public async Task UnresolvedBareIdentifier_UsesRoslynsClearerMessage()
    {
        var error = OnlyError("static byte Main() { return Ghost; }");
        await Assert.That(error.Message).Contains("does not exist in the current context");
        await Assert.That(error.Message).Contains("'Ghost'");
    }

    // ---- (b) a missing member on an existing (user) static class ---------------------------------------

    [Test]
    public async Task MissingMemberOnExistingStaticClass_UsesRoslynsClearerMessage()
    {
        const string src =
            "static class Board { static byte X; static byte Get() { return X; } }\n"
            + "static byte Main() { return Board.Ghost; }";
        var error = OnlyError(src);
        await Assert.That(error.Message).Contains("does not contain a definition for");
        await Assert.That(error.Message).Contains("'Board'");
        await Assert.That(error.Message).Contains("'Ghost'");
    }

    // ---- (c) regression: Koh-legal-but-C#-illegal code stays completely diagnostic-free ----------------

    [Test]
    public async Task MixedWidthArithmetic_CSharpIllegal_StaysDiagnosticFree()
    {
        // `byte c = a + b;` is CS0266 in real C# (int-promoted sum needs an explicit cast back to byte);
        // Koh's own width rules keep byte + byte as byte, so this must lower cleanly regardless.
        const string src =
            "static byte Main() { byte a = 1; byte b = 2; byte c = a + b; return c; }";
        await Assert.That(Diagnostics(src)).IsEmpty();
    }

    [Test]
    public async Task PointerArithmeticOutsideUnsafe_CSharpIllegal_StaysDiagnosticFree()
    {
        // Pointer arithmetic outside an `unsafe` context is CS0214 in real C#; the Koh subset supports
        // `T*`/arithmetic without the `unsafe` keyword at all, so this must lower cleanly regardless.
        const string src = "static byte Main() { byte* p = (byte*)Gb.Vram; p = p + 1; return *p; }";
        await Assert.That(Diagnostics(src)).IsEmpty();
    }

    [Test]
    public async Task Sample2048_StaysDiagnosticFree()
    {
        // The real gb-2048-cs sample (plus the framework HAL it builds against, exactly as the SDK
        // compiles it — see Samples/Game2048Tests.cs) is the richest available regression check: structs,
        // arrays, classes, enums, LINQ reductions, and the Hardware/Gb/Mem intrinsics, all resolved
        // through the very call/member/identifier sites this phase touches. Must still compile with zero
        // diagnostics — Roslyn's own errors on this file (if any) must never leak through.
        await Assert.That(Diagnostics(ReadSample())).IsEmpty();
    }

    private static string ReadSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("could not locate the repository root (Koh.slnx).");

        var frameworkHal = Path.Combine(dir.FullName, "src", "Koh.GameBoy", "Hal");
        var sampleDir = Path.Combine(dir.FullName, "samples", "gb-2048-cs");

        var sb = new System.Text.StringBuilder();
        foreach (
            var file in Directory
                .GetFiles(frameworkHal, "*.cs")
                .OrderBy(f => f, StringComparer.Ordinal)
        )
            sb.Append(File.ReadAllText(file)).Append('\n');
        foreach (
            var file in Directory
                .GetFiles(sampleDir, "*.cs")
                .OrderBy(f => f, StringComparer.Ordinal)
        )
            sb.Append(File.ReadAllText(file)).Append('\n');
        return sb.ToString();
    }

    // ---- (d) the enriched message still maps to the right (file, line, col) through the wrapper --------

    [Test]
    public async Task EnrichedMessage_StillMapsToCorrectLineThroughWrapper()
    {
        // Three methods; the error is in the third. If the wrapper's line-offset math (Report() subtracts
        // WrapperPrefix.Length — CSharpFrontend.cs) were off, this would resolve to the wrong method (most
        // commonly the first) instead of Broken's own declaration line.
        const string src =
            "static byte Helper1() { return 1; }\n"
            + "static byte Helper2() { return 2; }\n"
            + "static byte Broken() {\n"
            + "    return Ghost();\n"
            + "}\n";
        var error = OnlyError(src);
        var text = SourceText.From(src, "game.cs");
        int lineIndex = text.GetLineIndex(error.Span.Start);
        // 0-based line index of `static byte Broken() {` (the third line).
        await Assert.That(lineIndex).IsEqualTo(2);
        int col = error.Span.Start - text.Lines[lineIndex].Start;
        await Assert.That(col).IsEqualTo(0);
        await Assert.That(error.Message).Contains("does not exist in the current context");
    }

    // ---- (e) a detached monomorphized-generic body has no symbol to consult: plain message, no throw ---

    [Test]
    public async Task GenericBody_UnresolvedCall_KeepsPlainMessage_NoRoslynLeak_NoThrow()
    {
        // Touch<T>'s specialized body is a detached synthesized tree (TypeParamRewriter) — CSharpSemantics
        // reports it as out-of-tree, so BetterUnresolvedMessage's Roslyn lookup can't run there at all and
        // must fall back to Koh's own plain message, reported as an ordinary diagnostic (not an unhandled
        // exception escaping Lower()).
        const string src =
            "static byte Main() { return Touch<byte>(5); }\n"
            + "static T Touch<T>(T x) { return (T)Ghost(x); }\n";
        var error = OnlyError(src);
        await Assert.That(error.Message).IsEqualTo("unsupported call target 'Ghost'.");
    }
}
