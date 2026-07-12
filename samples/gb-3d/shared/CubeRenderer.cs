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
        short centerX = (short)(Surface.Width() / 2);
        short centerY = (short)(Surface.Height() / 2);
        short scale = Surface.Width() > 64 ? (short)112 : (short)(Surface.Width() * 2);

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
        FillTriangle(a, b, c, color);
        FillTriangle(a, c, d, color);
        DrawLine(a, b);
        DrawLine(b, c);
        DrawLine(c, d);
        DrawLine(d, a);
    }

    static void FillTriangle(byte ia, byte ib, byte ic, byte color)
    {
        short x0 = screenX[ia],
            y0 = screenY[ia];
        short x1 = screenX[ib],
            y1 = screenY[ib];
        short x2 = screenX[ic],
            y2 = screenY[ic];
        if (y1 < y0)
        {
            short t = x0;
            x0 = x1;
            x1 = t;
            t = y0;
            y0 = y1;
            y1 = t;
        }
        if (y2 < y0)
        {
            short t = x0;
            x0 = x2;
            x2 = t;
            t = y0;
            y0 = y2;
            y2 = t;
        }
        if (y2 < y1)
        {
            short t = x1;
            x1 = x2;
            x2 = t;
            t = y1;
            y1 = y2;
            y2 = t;
        }
        for (short y = y0; y <= y2; y++)
        {
            short xa = EdgeX(x0, y0, x2, y2, y);
            short xb = y < y1 ? EdgeX(x0, y0, x1, y1, y) : EdgeX(x1, y1, x2, y2, y);
            if (xa > xb)
            {
                short t = xa;
                xa = xb;
                xb = t;
            }
            if (y < 0 || y >= Surface.Height())
                continue;
            if (xa < 0)
                xa = 0;
            if (xb >= Surface.Width())
                xb = (short)(Surface.Width() - 1);
            for (short x = xa; x <= xb; x++)
            {
                byte shaded = ((x ^ y) & 3) == 0 && color > 1 ? (byte)(color - 1) : color;
                Surface.SetPixel((byte)x, (byte)y, shaded);
            }
        }
    }

    static short EdgeX(short x0, short y0, short x1, short y1, short y)
    {
        short dy = (short)(y1 - y0);
        if (dy == 0)
            return x0;
        return (short)(x0 + (x1 - x0) * (y - y0) / dy);
    }

    static void DrawLine(byte ia, byte ib)
    {
        short x0 = screenX[ia],
            y0 = screenY[ia],
            x1 = screenX[ib],
            y1 = screenY[ib];
        short dx = x1 > x0 ? (short)(x1 - x0) : (short)(x0 - x1);
        short sx = x0 < x1 ? (short)1 : (short)-1;
        short dy = y1 > y0 ? (short)(y0 - y1) : (short)(y1 - y0);
        short sy = y0 < y1 ? (short)1 : (short)-1;
        short error = (short)(dx + dy);
        while (true)
        {
            if (x0 >= 0 && x0 < Surface.Width() && y0 >= 0 && y0 < Surface.Height())
                Surface.SetPixel((byte)x0, (byte)y0, 3);
            if (x0 == x1 && y0 == y1)
                break;
            short twice = (short)(error * 2);
            if (twice >= dy)
            {
                error = (short)(error + dy);
                x0 = (short)(x0 + sx);
            }
            if (twice <= dx)
            {
                error = (short)(error + dx);
                y0 = (short)(y0 + sy);
            }
        }
    }
}
