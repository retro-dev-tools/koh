using System.Reflection;
using System.Text;
using Koh.Assembler;
using Koh.Assembler.Binding;
using Koh.Assembler.Syntax;
using Koh.Objects;

namespace Koh.Objects.Tests;

/// <summary>
/// Pins the RPN expressions <see cref="RgbdsObjectWriter"/> emits for unresolved patches, decoded to
/// readable tokens (the whole file is not compared: it embeds build-time <c>__UTC_*</c> symbols).
/// </summary>
public class RgbdsExpressionTests
{
    [Test]
    public async Task Operators()
    {
        var patches = WriteAndDecode(
            """
            SECTION "Main", ROM0
            dw ext
            dw ext + 1
            dw ext - 2 * 3
            dw (ext + 4) / 2
            dw ext % 3
            dw ext & $FF
            dw ext | 1
            dw ext ^ 2
            dw ext << 2
            dw ext >> 1
            dw ext == 1
            dw ext != 1
            dw ext < 1
            dw ext > 1
            dw ext <= 1
            dw ext >= 1
            dw ext && 1
            dw ext || 1
            db -ext
            db ~ext
            db !ext
            dw +ext
            dw ((ext))
            """
        );
        await Assert
            .That(patches)
            .IsEqualTo(
                Lines(
                    "0:1:0:sym:ext",
                    "2:1:2:sym:ext lit:1 add",
                    "4:1:4:sym:ext lit:2 lit:3 mul sub",
                    "6:1:6:sym:ext lit:4 add lit:2 div",
                    "8:1:8:sym:ext lit:3 mod",
                    "10:1:10:sym:ext lit:255 and",
                    "12:1:12:sym:ext lit:1 or",
                    "14:1:14:sym:ext lit:2 xor",
                    "16:1:16:sym:ext lit:2 shl",
                    "18:1:18:sym:ext lit:1 shr",
                    "20:1:20:sym:ext lit:1 eq",
                    "22:1:22:sym:ext lit:1 ne",
                    "24:1:24:sym:ext lit:1 lt",
                    "26:1:26:sym:ext lit:1 gt",
                    "28:1:28:sym:ext lit:1 le",
                    "30:1:30:sym:ext lit:1 ge",
                    "32:1:32:sym:ext lit:1 logand",
                    "34:1:34:sym:ext lit:1 logor",
                    "36:0:36:sym:ext neg",
                    "37:0:37:sym:ext not",
                    "38:0:38:sym:ext lognot",
                    "39:1:39:sym:ext",
                    "41:1:41:sym:ext"
                )
            );
    }

    [Test]
    public async Task CurrentAddressAndLiterals()
    {
        var patches = WriteAndDecode(
            """
            SECTION "Main", ROM0
            dw ext + $
            dw ext + $10 + %101 + 7
            dw ext + @
            db 0, ext + @
            """
        );
        await Assert
            .That(patches)
            .IsEqualTo(
                Lines(
                    "0:1:0:sym:ext sym:$ add",
                    "2:1:2:sym:ext lit:16 add lit:5 add lit:7 add",
                    "4:1:4:sym:ext sym:$ add",
                    "7:0:7:sym:ext sym:$ add"
                )
            );
    }

    // Current output: HIGH/LOW/BANK are dropped (known bug, tracked separately).
    [Test]
    public async Task FunctionCalls_CurrentOutput()
    {
        var patches = WriteAndDecode(
            """
            SECTION "Main", ROM0
            db HIGH(ext)
            db LOW(ext)
            db BANK(ext)
            """
        );
        await Assert
            .That(patches)
            .IsEqualTo(Lines("0:0:0:sym:ext", "1:0:1:sym:ext", "2:0:2:sym:ext"));
    }

    // A bare-symbol operand reaches the patch as a raw identifier token, not a NameExpression.
    [Test]
    public async Task InstructionOperands()
    {
        var patches = WriteAndDecode(
            """
            SECTION "Main", ROM0
            jp ext
            call ext + 3
            jr ext
            ld a, ext
            ld hl, ext
            ld hl, ext + @
            """
        );
        await Assert
            .That(patches)
            .IsEqualTo(
                Lines(
                    "1:1:0:sym:ext",
                    "4:1:3:sym:ext lit:3 add",
                    "7:3:6:sym:ext",
                    "9:0:8:sym:ext",
                    "11:1:10:sym:ext",
                    "14:1:13:sym:ext sym:$ add"
                )
            );
    }

    private static string Lines(params string[] lines) => string.Join('\n', lines);

    private static readonly Dictionary<byte, string> RpnNames = typeof(RgbdsObjectFormat)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(byte) && f.Name.StartsWith("Rpn"))
        .ToDictionary(
            f => (byte)f.GetRawConstantValue()!,
            f => f.Name["Rpn".Length..].ToLowerInvariant()
        );

    private static string WriteAndDecode(string source)
    {
        var options = new BinderOptions { AllowUndefinedSymbols = true };
        var model = Compilation.Create(options, SyntaxTree.Parse(source)).Emit();
        if (!model.Success)
            throw new InvalidOperationException(string.Join("; ", model.Diagnostics));

        using var ms = new MemoryStream();
        RgbdsObjectWriter.Write(ms, model);
        return string.Join('\n', DecodePatches(ms.ToArray()));
    }

    /// <summary>Reads an RGB9 object front to back and renders each patch as offset:kind:pcOffset:rpn.</summary>
    private static List<string> DecodePatches(byte[] bytes)
    {
        using var br = new BinaryReader(new MemoryStream(bytes));
        string CString()
        {
            var sb = new StringBuilder();
            for (byte c; (c = br.ReadByte()) != 0; )
                sb.Append((char)c);
            return sb.ToString();
        }

        br.ReadBytes(4); // magic
        br.ReadInt32(); // revision
        int symbolCount = br.ReadInt32();
        int sectionCount = br.ReadInt32();
        int nodeCount = br.ReadInt32();
        for (int i = 0; i < nodeCount; i++)
        {
            br.ReadInt32();
            br.ReadInt32();
            br.ReadByte();
            CString();
        }

        var symbols = new List<string>();
        for (int i = 0; i < symbolCount; i++)
        {
            symbols.Add(CString());
            if (br.ReadByte() != RgbdsObjectFormat.SymImport)
                br.ReadBytes(16);
        }

        var patches = new List<string>();
        for (int s = 0; s < sectionCount; s++)
        {
            CString();
            br.ReadInt32();
            br.ReadInt32();
            int size = br.ReadInt32();
            byte type = br.ReadByte();
            br.ReadBytes(4 + 4 + 1 + 4);
            if (type is not (RgbdsObjectFormat.SectRom0 or RgbdsObjectFormat.SectRomx))
                continue;
            br.ReadBytes(size);
            int patchCount = br.ReadInt32();
            for (int p = 0; p < patchCount; p++)
            {
                br.ReadInt32();
                br.ReadInt32();
                int offset = br.ReadInt32();
                br.ReadInt32();
                int pcOffset = br.ReadInt32();
                byte kind = br.ReadByte();
                var rpn = new BinaryReader(new MemoryStream(br.ReadBytes(br.ReadInt32())));
                var tokens = new List<string>();
                while (rpn.BaseStream.Position < rpn.BaseStream.Length)
                {
                    byte op = rpn.ReadByte();
                    if (op == RgbdsObjectFormat.RpnLiteral)
                        tokens.Add($"lit:{rpn.ReadInt32()}");
                    else if (op == RgbdsObjectFormat.RpnSymbol)
                    {
                        uint id = rpn.ReadUInt32();
                        tokens.Add(id == uint.MaxValue ? "sym:$" : $"sym:{symbols[(int)id]}");
                    }
                    else
                        tokens.Add(RpnNames[op]);
                }
                patches.Add($"{offset}:{kind}:{pcOffset}:{string.Join(' ', tokens)}");
            }
        }

        if (br.ReadInt32() != 0 || br.BaseStream.Position != bytes.Length)
            throw new InvalidDataException(
                "RGB9 object did not end at the trailing assertion count."
            );
        return patches;
    }
}
