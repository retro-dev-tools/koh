using System.Collections.Immutable;
using Koh.Common;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Objects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using LinkerType = Koh.Native.NativeLinker;

namespace Koh.Compiler.Tests.Sm83;

public class Sm83SymbolAddressTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk -----------------------------------

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (
                var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            )
            {
                try
                {
                    builder.Add(MetadataReference.CreateFromFile(path));
                }
                catch (IOException) { }
                catch (BadImageFormatException) { }
            }
        }
        return builder.ToImmutable();
    });

    private static readonly string ScratchDir = Path.Combine(
        Path.GetTempPath(),
        "koh-sm83-symbol-address-tests"
    );

    private static string CompileToAssembly(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "Sm83SymAddrAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"sm83symaddr_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    private static EmitModel Compile(string src)
    {
        var wrapped = "class Program {\n" + src + "\n}";
        var assemblyPath = CompileToAssembly(wrapped);
        var input = CompilerInput.FromAssembly(assemblyPath, []);
        var diagnostics = new DiagnosticBag();
        var module = new CilFrontend().Lower(input, diagnostics);
        return new Sm83Backend().Compile(module, diagnostics);
    }

    [Test]
    public async Task FunctionLabel_SymbolAtActualCodeAddress_NotDoubleCountingCodeBase()
    {
        // Two functions: Helper's label lands at some offset > 0 within the fixed ROM0 "CODE" section
        // (CodeBase == 0x0150), which is enough to see the bug: today Sm83Backend hands the resolver
        // `Helper`'s absolute address as `Value`, so `ResolveAddresses` computes
        // `section.PlacedAddress (CodeBase) + Value (CodeBase + offset)` == `2*CodeBase + offset`.
        var model = Compile(
            """
            static byte v;
            static void Main() { Helper(); v = 1; }
            static void Helper() { v = 2; }
            """
        );

        // Ground truth independent of Value's convention: Main's `CALL Helper` instruction bytes already
        // bake in Helper's real absolute address before linking (CLAUDE.md: "call targets are baked into
        // bytes before linking; program behavior is unaffected [by this bug]"). Decode it straight from
        // the raw CODE section bytes Sm83Backend.Compile produced, with no resolver involved. (`Main` has
        // a statement after the call so the backend can't tail-call it into a plain `JP`.)
        byte[] code = model.Sections.Single(s => s.Name == "CODE").Data;
        int callOpcodeIndex = Array.IndexOf(code, (byte)0xCD); // CALL nn
        await Assert.That(callOpcodeIndex).IsGreaterThanOrEqualTo(0);
        int calledAddress = code[callOpcodeIndex + 1] | (code[callOpcodeIndex + 2] << 8);

        var link = new LinkerType().Link([new Koh.Native.LinkerInput("game", model)]);
        await Assert.That(link.Success).IsTrue();

        // CilFrontend names a function "{DeclaringType.Name}.{Method.Name}" (CilLoweringContext), unlike
        // the deleted CSharpFrontend's bare top-level-function names.
        var helper = link.Symbols.Single(s => s.Name == "Program.Helper");
        await Assert.That(helper.AbsoluteAddress).IsEqualTo((long)calledAddress);
    }

    [Test]
    public async Task EntryFunctionLabel_SymbolMatchesHeaderEntryPoint()
    {
        // The cartridge header's entry JP target (built from the backend's own `entryAddress`) and the
        // linked `Main` symbol's resolved AbsoluteAddress describe the same address two different ways.
        // They must agree; today they don't, because only the header path is exempt from the bug (it
        // never goes through SymbolResolver).
        var model = Compile("static void Main() { }");
        var link = new LinkerType().Link([new Koh.Native.LinkerInput("game", model)]);
        await Assert.That(link.Success).IsTrue();

        byte[] rom = link.RomData!;
        int headerEntry = rom[0x0102] | (rom[0x0103] << 8); // `nop; jp <entry>` at 0x0100

        var main = link.Symbols.Single(s => s.Name == "Program.Main");
        await Assert.That(main.AbsoluteAddress).IsEqualTo((long)headerEntry);
    }

    [Test]
    public async Task WramGlobalLabel_SymbolAtWramBase()
    {
        // Globals are emitted with the same CodeSectionName/Value convention as functions (see
        // CompileCore's second `symbols.Add` loop), even though a WRAM global's real address has nothing
        // to do with the "CODE" section's ROM0 placement. Before the fix this was not just double-counted
        // like a function label but wildly wrong (CodeBase + a WRAM address, e.g. ~$C150 instead of
        // $C000) — the fix's `addr - CodeBase` / `+ CodeBase` round trip must recover the real address
        // regardless of which memory region it is actually in.
        var model = Compile("static byte counter; static void Main() { counter = 1; }");
        var link = new LinkerType().Link([new Koh.Native.LinkerInput("game", model)]);
        await Assert.That(link.Success).IsTrue();

        var counter = link.Symbols.Single(s => s.Name == "Program.counter");
        await Assert.That(counter.AbsoluteAddress).IsEqualTo((long)Sm83Backend.WramBase);
    }
}
