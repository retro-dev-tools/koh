// Local override of shared/CubeRenderer.cs (see Game.cs's header comment) — the 3D math (transform,
// face sort, backface cull) is unchanged app code; only the raster calls change, from this file's own
// SetPixel/FillSpan-based DrawLine/FillTriangle helpers (removed here) to Canvas.DrawLine/FillTriangle
// directly, which already implement the same sort-by-y/EdgeX-scanline/bounds-clip shape internally (they
// were themselves lifted from this exact file — see Canvas.cs's class remarks), so this variant no
// longer needs its own copies.
using Koh.GameBoy.Graphics;

static class CubeRenderer
{
    static short[] screenX = new short[8];
    static short[] screenY = new short[8];
    static short[] depth = new short[8];
    static byte[] faceOrder = new byte[6];
    static short[] faceDepth = new short[6];

    internal static void Render(byte phase)
    {
        Transform(phase);
        SortFaces();
        for (byte i = 0; i < 6; i++)
            DrawFace(faceOrder[i]);
    }

    static void Transform(byte phase)
    {
        short sy = FixedMath.Sin(phase);
        short cy = FixedMath.Cos(phase);
        short sx = FixedMath.Sin((byte)(phase + phase / 2));
        short cx = FixedMath.Cos((byte)(phase + phase / 2));
        short centerX = (short)(Canvas.Width / 2);
        short centerY = (short)(Canvas.Height / 2);
        // Projection scale — see shared/CubeRenderer.cs's Transform for the full worst-case-vertex
        // derivation this reuses verbatim (Canvas.Width/Height stand in for Surface.Width()/Height()).
        short scale = Canvas.Width > 64 ? (short)112 : (short)(Canvas.Width + Canvas.Width / 2);

        for (byte i = 0; i < 8; i++)
        {
            short x = (i & 1) == 0 ? (short)-34 : (short)34;
            short y = (i & 2) == 0 ? (short)-34 : (short)34;
            short z = (i & 4) == 0 ? (short)-34 : (short)34;
            short rx = (short)((x * cy + z * sy) / 128);
            short rz = (short)((z * cy - x * sy) / 128);
            short ry = (short)((y * cx - rz * sx) / 128);
            short rz2 = (short)((rz * cx + y * sx) / 128);
            short distance = (short)(rz2 + 150);
            screenX[i] = (short)(centerX + rx * scale / distance);
            screenY[i] = (short)(centerY + ry * scale / distance);
            depth[i] = rz2;
        }
    }

    static byte Vertex(byte face, byte corner)
    {
        if (face == 0)
        {
            if (corner == 0)
                return 0;
            if (corner == 1)
                return 2;
            if (corner == 2)
                return 6;
            return 4;
        }
        if (face == 1)
        {
            if (corner == 0)
                return 1;
            if (corner == 1)
                return 5;
            if (corner == 2)
                return 7;
            return 3;
        }
        if (face == 2)
        {
            if (corner == 0)
                return 0;
            if (corner == 1)
                return 4;
            if (corner == 2)
                return 5;
            return 1;
        }
        if (face == 3)
        {
            if (corner == 0)
                return 2;
            if (corner == 1)
                return 3;
            if (corner == 2)
                return 7;
            return 6;
        }
        if (face == 4)
        {
            if (corner == 0)
                return 0;
            if (corner == 1)
                return 1;
            if (corner == 2)
                return 3;
            return 2;
        }
        if (corner == 0)
            return 4;
        if (corner == 1)
            return 6;
        if (corner == 2)
            return 7;
        return 5;
    }

    static void SortFaces()
    {
        for (byte face = 0; face < 6; face++)
        {
            faceOrder[face] = face;
            faceDepth[face] = (short)(
                depth[Vertex(face, 0)]
                + depth[Vertex(face, 1)]
                + depth[Vertex(face, 2)]
                + depth[Vertex(face, 3)]
            );
        }
        for (byte i = 1; i < 6; i++)
        {
            byte f = faceOrder[i];
            short d = faceDepth[i];
            byte j = i;
            while (j > 0 && faceDepth[j - 1] > d)
            {
                faceOrder[j] = faceOrder[j - 1];
                faceDepth[j] = faceDepth[j - 1];
                j--;
            }
            faceOrder[j] = f;
            faceDepth[j] = d;
        }
    }

    static void DrawFace(byte face)
    {
        byte a = Vertex(face, 0);
        byte b = Vertex(face, 1);
        byte c = Vertex(face, 2);
        byte d = Vertex(face, 3);
        int cross =
            (screenX[b] - screenX[a]) * (screenY[c] - screenY[a])
            - (screenY[b] - screenY[a]) * (screenX[c] - screenX[a]);
        if (cross <= 0)
            return;
        byte color = (byte)(1 + face % 3);
        Canvas.FillTriangle(
            screenX[a],
            screenY[a],
            screenX[b],
            screenY[b],
            screenX[c],
            screenY[c],
            color
        );
        Canvas.FillTriangle(
            screenX[a],
            screenY[a],
            screenX[c],
            screenY[c],
            screenX[d],
            screenY[d],
            color
        );
        DrawEdge(a, b);
        DrawEdge(b, c);
        DrawEdge(c, d);
        DrawEdge(d, a);
    }

    static void DrawEdge(byte ia, byte ib) =>
        Canvas.DrawLine(screenX[ia], screenY[ia], screenX[ib], screenY[ib], 3);
}
