using Koh.Core.Binding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Emit;

/// <summary>
/// Writes an EmitModel to RGBDS RGB9 object file format (.o).
/// Compatible with rgblink for linking.
/// </summary>
public sealed class RgbdsObjectWriter
{
    private readonly Dictionary<string, int> _symbolIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sectionIndex = new(StringComparer.OrdinalIgnoreCase);

    public static void Write(Stream stream, EmitModel model)
    {
        if (!model.Success)
            throw new InvalidOperationException(
                "Cannot write an RGBDS object file for a failed compilation.");

        new RgbdsObjectWriter().WriteInternal(stream, model);
    }

    private void WriteInternal(Stream stream, EmitModel model)
    {
        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Build symbol and section index maps
        for (int i = 0; i < model.Symbols.Count; i++)
            _symbolIndex[model.Symbols[i].Name] = i;
        for (int i = 0; i < model.Sections.Count; i++)
            _sectionIndex[model.Sections[i].Name] = i;

        // Header: magic + revision + counts
        bw.Write(RgbdsObjectFormat.Magic);
        WriteInt32(bw, RgbdsObjectFormat.Revision);
        WriteInt32(bw, model.Symbols.Count);
        WriteInt32(bw, model.Sections.Count);
        WriteInt32(bw, 1); // file stack node count

        // File stack nodes (single root node)
        WriteInt32(bw, -1);     // parent ID (-1 = root)
        WriteInt32(bw, 0);      // line
        bw.Write((byte)1);      // type: NODE_FILE
        WriteString(bw, "koh"); // filename

        // Symbols
        foreach (var sym in model.Symbols)
            WriteSymbol(bw, sym);

        // Sections
        foreach (var section in model.Sections)
            WriteSection(bw, section);

        // Assertions (required trailing block — zero assertions)
        WriteInt32(bw, 0);
    }

    private void WriteSymbol(BinaryWriter bw, SymbolData sym)
    {
        WriteString(bw, sym.Name);

        byte type = sym.Visibility == SymbolVisibility.Exported
            ? RgbdsObjectFormat.SymExport
            : RgbdsObjectFormat.SymLocal;
        bw.Write(type);

        // Import symbols have no further fields in RGBDS format.
        // Currently we only emit LOCAL and EXPORT symbols.
        // NodeID, LineNo, SectionID, Value are only for non-import symbols.
        WriteInt32(bw, 0); // file stack node ID
        WriteInt32(bw, 0); // line number

        int sectionId = -1;
        if (sym.Section != null && _sectionIndex.TryGetValue(sym.Section, out var idx))
            sectionId = idx;
        WriteInt32(bw, sectionId);
        WriteInt32(bw, (int)sym.Value);
    }

    private void WriteSection(BinaryWriter bw, SectionData section)
    {
        // Field order per RGB9 rev 13: name, nodeID, lineNo, size, type, org, bank, align, alignOfs
        WriteString(bw, section.Name);
        WriteInt32(bw, 0);                          // nodeID
        WriteInt32(bw, 0);                          // lineNo
        WriteInt32(bw, section.Data.Length);         // size
        bw.Write(MapSectionType(section.Type));     // type (byte)
        WriteInt32(bw, section.FixedAddress ?? -1); // org (-1 = floating)
        WriteInt32(bw, section.Bank ?? -1);         // bank (-1 = unspecified)
        bw.Write((byte)0);                          // align (1 byte per rgblink tryGetc)
        WriteInt32(bw, 0);                          // align offset

        // Data and patches only for ROM sections
        if (IsRomType(section.Type))
        {
            bw.Write(section.Data);

            int sectionId = _sectionIndex.TryGetValue(section.Name, out var idx) ? idx : 0;
            var patches = section.Patches;
            WriteInt32(bw, patches.Count);
            foreach (var patch in patches)
                WritePatch(bw, patch, sectionId);
        }
    }

    private void WritePatch(BinaryWriter bw, PatchEntry patch, int pcSectionId)
    {
        // Field order per RGB9 rev 13: nodeID, lineNo, offset, pcSectionID, pcOffset, type, rpnSize, rpn
        WriteInt32(bw, 0);                            // nodeID
        WriteInt32(bw, 0);                            // lineNo
        WriteInt32(bw, patch.Offset);                 // offset within section
        WriteInt32(bw, pcSectionId);                  // pcSectionID
        WriteInt32(bw, patch.PCAfterInstruction);     // pcOffset
        bw.Write(MapPatchKind(patch.Kind));           // type (byte)

        // RPN expression
        var rpn = new List<byte>();
        if (patch.Expression != null)
            FlattenToRpn(patch.Expression, rpn);
        WriteInt32(bw, rpn.Count);
        bw.Write(rpn.ToArray());
    }

    private void FlattenToRpn(GreenNodeBase node, List<byte> rpn)
    {
        if (node is GreenNode greenNode)
        {
            switch (greenNode.Kind)
            {
                case SyntaxKind.LiteralExpression:
                {
                    var child = greenNode.GetChild(0);
                    if (child is GreenToken token)
                    {
                        if (token.Kind == SyntaxKind.NumberLiteral)
                        {
                            var val = ExpressionEvaluator.ParseNumber(token.Text);
                            rpn.Add(RgbdsObjectFormat.RpnLiteral);
                            WriteRpnInt32(rpn, (int)(val ?? 0));
                        }
                        else if (token.Kind == SyntaxKind.CurrentAddressToken)
                        {
                            // $ = current PC — encoded as RPN_SYM with ID 0xFFFFFFFF
                            rpn.Add(RgbdsObjectFormat.RpnSymbol);
                            WriteRpnInt32(rpn, unchecked((int)0xFFFFFFFF));
                        }
                    }
                    break;
                }

                case SyntaxKind.NameExpression:
                {
                    var child = greenNode.GetChild(0);
                    if (child is GreenToken token)
                    {
                        if (_symbolIndex.TryGetValue(token.Text, out var idx))
                        {
                            rpn.Add(RgbdsObjectFormat.RpnSymbol);
                            WriteRpnInt32(rpn, idx);
                        }
                        else
                        {
                            // Unknown symbol — emit as literal 0 (should be an import in a full implementation)
                            rpn.Add(RgbdsObjectFormat.RpnLiteral);
                            WriteRpnInt32(rpn, 0);
                        }
                    }
                    break;
                }

                case SyntaxKind.BinaryExpression:
                {
                    var left = greenNode.GetChild(0);
                    var op = greenNode.GetChild(1) as GreenToken;
                    var right = greenNode.GetChild(2);
                    if (left != null) FlattenToRpn(left, rpn);
                    if (right != null) FlattenToRpn(right, rpn);
                    if (op != null) rpn.Add(MapBinaryOp(op.Kind));
                    break;
                }

                case SyntaxKind.UnaryExpression:
                {
                    var op = greenNode.GetChild(0) as GreenToken;
                    var operand = greenNode.GetChild(1);
                    if (operand != null) FlattenToRpn(operand, rpn);
                    // Unary + is identity — no RPN opcode needed
                    if (op != null && op.Kind != SyntaxKind.PlusToken)
                        rpn.Add(MapUnaryOp(op.Kind));
                    break;
                }

                case SyntaxKind.ParenthesizedExpression:
                {
                    var inner = greenNode.GetChild(1);
                    if (inner != null) FlattenToRpn(inner, rpn);
                    break;
                }

                default:
                    for (int i = 0; i < greenNode.ChildCount; i++)
                    {
                        var child = greenNode.GetChild(i);
                        if (child != null) FlattenToRpn(child, rpn);
                    }
                    break;
            }
        }
    }

    private static byte MapBinaryOp(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken => RgbdsObjectFormat.RpnAdd,
        SyntaxKind.MinusToken => RgbdsObjectFormat.RpnSub,
        SyntaxKind.StarToken => RgbdsObjectFormat.RpnMul,
        SyntaxKind.SlashToken => RgbdsObjectFormat.RpnDiv,
        SyntaxKind.PercentToken => RgbdsObjectFormat.RpnMod,
        SyntaxKind.AmpersandToken => RgbdsObjectFormat.RpnAnd,
        SyntaxKind.PipeToken => RgbdsObjectFormat.RpnOr,
        SyntaxKind.CaretToken => RgbdsObjectFormat.RpnXor,
        SyntaxKind.LessThanLessThanToken => RgbdsObjectFormat.RpnShl,
        SyntaxKind.GreaterThanGreaterThanToken => RgbdsObjectFormat.RpnShr,
        SyntaxKind.EqualsEqualsToken => RgbdsObjectFormat.RpnEq,
        SyntaxKind.BangEqualsToken => RgbdsObjectFormat.RpnNe,
        SyntaxKind.LessThanToken => RgbdsObjectFormat.RpnLt,
        SyntaxKind.GreaterThanToken => RgbdsObjectFormat.RpnGt,
        SyntaxKind.LessThanEqualsToken => RgbdsObjectFormat.RpnLe,
        SyntaxKind.GreaterThanEqualsToken => RgbdsObjectFormat.RpnGe,
        SyntaxKind.AmpersandAmpersandToken => RgbdsObjectFormat.RpnLogAnd,
        SyntaxKind.PipePipeToken => RgbdsObjectFormat.RpnLogOr,
        _ => RgbdsObjectFormat.RpnAdd,
    };

    private static byte MapUnaryOp(SyntaxKind kind) => kind switch
    {
        SyntaxKind.MinusToken => RgbdsObjectFormat.RpnNeg,
        SyntaxKind.TildeToken => RgbdsObjectFormat.RpnNot,
        SyntaxKind.BangToken => RgbdsObjectFormat.RpnLogNot,
        _ => RgbdsObjectFormat.RpnNeg,
    };

    private static byte MapSectionType(SectionType type) => type switch
    {
        SectionType.Rom0 => RgbdsObjectFormat.SectRom0,
        SectionType.RomX => RgbdsObjectFormat.SectRomx,
        SectionType.Wram0 => RgbdsObjectFormat.SectWram0,
        SectionType.WramX => RgbdsObjectFormat.SectWramx,
        SectionType.Vram => RgbdsObjectFormat.SectVram,
        SectionType.Hram => RgbdsObjectFormat.SectHram,
        SectionType.Sram => RgbdsObjectFormat.SectSram,
        SectionType.Oam => RgbdsObjectFormat.SectOam,
        _ => RgbdsObjectFormat.SectRom0,
    };

    private static byte MapPatchKind(PatchKind kind) => kind switch
    {
        PatchKind.Absolute8 => RgbdsObjectFormat.PatchByte,
        PatchKind.Absolute16 => RgbdsObjectFormat.PatchLe16,
        PatchKind.Relative8 => RgbdsObjectFormat.PatchJr,
        _ => RgbdsObjectFormat.PatchByte,
    };

    private static bool IsRomType(SectionType type) =>
        type is SectionType.Rom0 or SectionType.RomX;

    private static void WriteInt32(BinaryWriter bw, int value) => bw.Write(value);

    private static void WriteString(BinaryWriter bw, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        bw.Write(bytes);
        bw.Write((byte)0);
    }

    private static void WriteRpnInt32(List<byte> rpn, int value)
    {
        rpn.Add((byte)(value & 0xFF));
        rpn.Add((byte)((value >> 8) & 0xFF));
        rpn.Add((byte)((value >> 16) & 0xFF));
        rpn.Add((byte)((value >> 24) & 0xFF));
    }
}
