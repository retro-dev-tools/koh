using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// A synthetic stub source declaring the intrinsic surface (<c>Hardware</c>, <c>Gb</c>, <c>Mem</c>,
/// <c>InterruptAttribute</c>) so ordinary Roslyn binding resolves it for the semantic model
/// (<see cref="CSharpSemantics"/>). Generated from <see cref="HardwareRegisters"/>'s tables so the two
/// can never drift apart. Declared in the global namespace, not <c>namespace Koh.GameBoy</c>: user/HAL
/// source is wrapped by <see cref="CSharpFrontend.BlankNamespacing"/>, which erases its usings and
/// namespace headers, so it refers to these names as global-namespace simple names — the real
/// <c>Koh.GameBoy</c> assembly's types would not bind. This tree is added to the compilation as a pure
/// binding oracle; it is never lowered.
/// </summary>
internal static class IntrinsicsStub
{
    private static readonly Lazy<SyntaxTree> _tree = new(() =>
        CSharpSyntaxTree.ParseText(Generate(), path: "__KohIntrinsics.cs")
    );

    /// <summary>The parsed stub tree (generated and parsed once, then cached for the process lifetime).</summary>
    internal static SyntaxTree Tree => _tree.Value;

    /// <summary>Build the stub source text. Internal (not private) so a test can inspect it directly
    /// without going through the semantic model.</summary>
    internal static string Generate()
    {
        var sb = new StringBuilder();

        // A global using so bare BCL names the wrapped source refers to (Int128, MathF, BitConverter,
        // ...) resolve without a `using System;` line — BlankNamespacing erases the source's own usings,
        // and (unlike CSharpCompilationOptions.Usings, which only applies to Script-kind trees) a `global
        // using` directive in any tree of a compilation applies to every tree in it.
        sb.AppendLine("global using System;");
        sb.AppendLine();

        // Hardware: one auto-property per memory-mapped register (read AND written by user code), plus
        // the handful of control intrinsics MethodLowerer recognizes by name.
        sb.AppendLine("public static class Hardware");
        sb.AppendLine("{");
        foreach (var name in HardwareRegisters.RegisterNames)
            sb.AppendLine($"    public static byte {name} {{ get; set; }}");
        sb.AppendLine("    public static void EnableInterrupts() { }");
        sb.AppendLine("    public static void DisableInterrupts() { }");
        sb.AppendLine("    public static void Halt() { }");
        sb.AppendLine("    public static void Nop() { }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Gb: one byte* per memory region, a constant base pointer used in pointer arithmetic (read
        // only — never assigned, like the real Koh.GameBoy.Gb properties).
        sb.AppendLine("public static unsafe class Gb");
        sb.AppendLine("{");
        foreach (var name in HardwareRegisters.RegionNames)
            sb.AppendLine($"    public static byte* {name} => null;");
        sb.AppendLine("}");
        sb.AppendLine();

        // Mem: the arena allocator intrinsics, plus the Copy/Fill byte-block intrinsics (routed to the
        // MemRuntime.cs runtime the same way Alloc/Reset route to the bump allocator).
        sb.AppendLine("public static class Mem");
        sb.AppendLine("{");
        sb.AppendLine("    public static unsafe byte* Alloc(int size) => null;");
        sb.AppendLine("    public static void Reset() { }");
        sb.AppendLine(
            "    public static unsafe void Copy(byte* destination, byte* source, ushort count) { }"
        );
        sb.AppendLine(
            "    public static unsafe void Fill(byte* destination, byte value, ushort count) { }"
        );
        sb.AppendLine("}");
        sb.AppendLine();

        // [Interrupt("VBlank")] etc.
        sb.AppendLine("public sealed class InterruptAttribute : System.Attribute");
        sb.AppendLine("{");
        sb.AppendLine("    public InterruptAttribute(string kind) { }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
