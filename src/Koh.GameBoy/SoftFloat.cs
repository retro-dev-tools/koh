namespace Koh.GameBoy;

/// <summary>
/// The IEEE-754 softfloat runtime, ported from <c>Koh.Compiler.Frontends.CSharp.SoftFloatRuntime</c>
/// (a const string of Koh-subset C# that <c>CSharpFrontend</c> appends to a program using
/// <c>float</c>/<c>double</c>) into a real, compiled <c>Koh.GameBoy</c> class. Each routine operates on
/// the raw IEEE bits carried in <c>uint</c>/<c>ulong</c> — no <c>float</c>/<c>double</c> operand types
/// are used here, so this is ordinary Koh-subset code the CIL frontend can lower from IL like any other
/// referenced-assembly method. The entry points a source-level float operation resolves to are marked
/// <c>[KohRuntime("f32.add")]</c> etc.; the CIL frontend's float-op routing to these keys is a later
/// task — this class exists to be lowerable and bit-correct, nothing calls it yet.
///
/// The algorithms are a straight port (not a rewrite) of <c>SoftFloatRuntime.Source</c>: same bit
/// twiddling, same rounding, same edge cases, so the bit-identity guarantee already pinned there
/// (finite normal numbers and zero, round-to-nearest-even; infinities/NaNs as operands and subnormal
/// inputs are out of scope, same as there) carries over unchanged. The original stays untouched in
/// place — <c>CSharpFrontend</c> still needs it.
///
/// <c>MathF</c> (Abs/Truncate/Floor/Ceiling/Round/Min/Max/Sign) from the original source is NOT ported
/// here: it operates on actual <c>float</c> values (via <c>BitConverter.SingleToUInt32Bits</c>), which
/// isn't a real Koh IR type yet — wiring float itself into the CIL frontend is later work.
/// </summary>
public static class SoftFloat
{
    // ---- Koh softfloat (IEEE-754 single precision) ----------------------------

    private static uint ShrStickyF32(uint v, int n)
    {
        if (n >= 32)
        {
            if (v != 0)
                return 1;
            return 0;
        }
        uint lost = v & (((uint)1 << n) - 1);
        uint r = v >> n;
        if (lost != 0)
            r = r | 1;
        return r;
    }

    [KohRuntime("f32.neg")]
    public static uint NegF32(uint a)
    {
        return a ^ 0x80000000;
    }

    [KohRuntime("f32.add")]
    public static uint AddF32(uint a, uint b)
    {
        uint sa = a >> 31;
        uint sb = b >> 31;
        int ea = (int)((a >> 23) & 0xFF);
        int eb = (int)((b >> 23) & 0xFF);
        uint ma = a & 0x7FFFFF;
        uint mb = b & 0x7FFFFF;
        if (ea == 0)
        { // zero or subnormal (flushed to zero)
            if (eb == 0)
                return (sa & sb) << 31;
            return b;
        }
        if (eb == 0)
            return a;
        uint siga = (ma | 0x800000) << 3;
        uint sigb = (mb | 0x800000) << 3;
        int exp;
        if (ea > eb)
        {
            sigb = ShrStickyF32(sigb, ea - eb);
            exp = ea;
        }
        else if (eb > ea)
        {
            siga = ShrStickyF32(siga, eb - ea);
            exp = eb;
        }
        else
        {
            exp = ea;
        }
        uint sig;
        uint sign;
        if (sa == sb)
        {
            sig = siga + sigb;
            sign = sa;
        }
        else
        {
            if (siga >= sigb)
            {
                sig = siga - sigb;
                sign = sa;
            }
            else
            {
                sig = sigb - siga;
                sign = sb;
            }
            if (sig == 0)
                return 0;
        }
        while (sig >= ((uint)1 << 27))
        {
            sig = ShrStickyF32(sig, 1);
            exp = exp + 1;
        }
        while (sig != 0 && sig < ((uint)1 << 26))
        {
            sig = sig << 1;
            exp = exp - 1;
        }
        uint roundBits = sig & 7;
        sig = sig >> 3;
        if (roundBits > 4 || (roundBits == 4 && (sig & 1) != 0))
        {
            sig = sig + 1;
            if (sig >= ((uint)1 << 24))
            {
                sig = sig >> 1;
                exp = exp + 1;
            }
        }
        if (exp >= 255)
            return (sign << 31) | ((uint)0xFF << 23);
        if (exp <= 0)
            return sign << 31;
        return (sign << 31) | ((uint)exp << 23) | (sig & 0x7FFFFF);
    }

    [KohRuntime("f32.sub")]
    public static uint SubF32(uint a, uint b)
    {
        return AddF32(a, b ^ 0x80000000);
    }

    [KohRuntime("f32.mul")]
    public static uint MulF32(uint a, uint b)
    {
        uint sign = (a ^ b) & 0x80000000;
        int ea = (int)((a >> 23) & 0xFF);
        int eb = (int)((b >> 23) & 0xFF);
        uint ma = a & 0x7FFFFF;
        uint mb = b & 0x7FFFFF;
        if (ea == 0 || eb == 0)
            return sign; // 0 * x = 0 (subnormals flushed to zero)
        uint siga = ma | 0x800000;
        uint sigb = mb | 0x800000;
        ulong prod = (ulong)siga * (ulong)sigb; // up to 48 bits
        int exp = ea + eb - 127;
        if (prod >= ((ulong)1 << 47))
        {
            exp = exp + 1;
        }
        else
        {
            prod = prod << 1;
        } // leading 1 now at bit 47
        uint sig = (uint)(prod >> 24); // 24-bit significand (bit 23 = leading 1)
        uint rem = (uint)(prod & 0xFFFFFF); // low 24 bits -> rounding
        uint half = 0x800000;
        if (rem > half || (rem == half && (sig & 1) != 0))
        {
            sig = sig + 1;
            if (sig >= ((uint)1 << 24))
            {
                sig = sig >> 1;
                exp = exp + 1;
            }
        }
        if (exp >= 255)
            return sign | ((uint)0xFF << 23);
        if (exp <= 0)
            return sign;
        return sign | ((uint)exp << 23) | (sig & 0x7FFFFF);
    }

    [KohRuntime("f32.div")]
    public static uint DivF32(uint a, uint b)
    {
        uint sign = (a ^ b) & 0x80000000;
        int ea = (int)((a >> 23) & 0xFF);
        int eb = (int)((b >> 23) & 0xFF);
        uint ma = a & 0x7FFFFF;
        uint mb = b & 0x7FFFFF;
        if (ea == 0)
            return sign; // 0 / x = 0 (subnormals flushed)
        if (eb == 0)
            return sign | ((uint)0xFF << 23); // x / 0 = inf
        uint siga = ma | 0x800000;
        uint sigb = mb | 0x800000;
        int exp = ea - eb + 127;
        ulong num = (ulong)siga << 25;
        uint q = (uint)(num / (ulong)sigb);
        uint r = (uint)(num % (ulong)sigb);
        if (q >= ((uint)1 << 25)) { } // leading already at bit 25 (no-op)
        else
        {
            q = q << 1;
            exp = exp - 1;
        } // normalize leading to bit 25
        uint sig = q >> 2; // 24-bit significand
        uint low = q & 3; // guard + round
        uint sticky = (r != 0) ? (uint)1 : (uint)0;
        bool up;
        if ((low & 2) == 0)
            up = false;
        else if (low == 2 && sticky == 0)
            up = (sig & 1) != 0;
        else
            up = true;
        if (up)
        {
            sig = sig + 1;
            if (sig >= ((uint)1 << 24))
            {
                sig = sig >> 1;
                exp = exp + 1;
            }
        }
        if (exp >= 255)
            return sign | ((uint)0xFF << 23);
        if (exp <= 0)
            return sign;
        return sign | ((uint)exp << 23) | (sig & 0x7FFFFF);
    }

    // ---- Comparisons (return bool) --------------------------------------------

    [KohRuntime("f32.isNan")]
    public static byte IsNanF32(uint a)
    {
        if (((a >> 23) & 0xFF) == 0xFF && (a & 0x7FFFFF) != 0)
            return 1;
        return 0;
    }

    // 0 = equal, 1 = a<b, 2 = a>b, 3 = unordered (NaN)
    [KohRuntime("f32.cmp")]
    public static byte CmpF32(uint a, uint b)
    {
        if (IsNanF32(a) != 0 || IsNanF32(b) != 0)
            return 3;
        if (((a | b) << 1) == 0)
            return 0; // +0 == -0
        uint sa = a >> 31;
        uint sb = b >> 31;
        if (sa != sb)
        {
            if (sa != 0)
                return 1;
            return 2;
        }
        uint ma = a & 0x7FFFFFFF;
        uint mb = b & 0x7FFFFFFF;
        if (ma == mb)
            return 0;
        if (sa == 0)
        {
            if (ma < mb)
                return 1;
            return 2;
        }
        if (ma > mb)
            return 1;
        return 2;
    }

    [KohRuntime("f32.lt")]
    public static bool LtF32(uint a, uint b)
    {
        return CmpF32(a, b) == 1;
    }

    [KohRuntime("f32.le")]
    public static bool LeF32(uint a, uint b)
    {
        byte c = CmpF32(a, b);
        return c == 1 || c == 0;
    }

    [KohRuntime("f32.gt")]
    public static bool GtF32(uint a, uint b)
    {
        return CmpF32(a, b) == 2;
    }

    [KohRuntime("f32.ge")]
    public static bool GeF32(uint a, uint b)
    {
        byte c = CmpF32(a, b);
        return c == 2 || c == 0;
    }

    [KohRuntime("f32.eq")]
    public static bool EqF32(uint a, uint b)
    {
        return CmpF32(a, b) == 0;
    }

    [KohRuntime("f32.ne")]
    public static bool NeF32(uint a, uint b)
    {
        return CmpF32(a, b) != 0;
    }

    // ---- Integer <-> float conversions ----------------------------------------

    [KohRuntime("u32.toF32")]
    public static uint FromU32(uint mag)
    {
        if (mag == 0)
            return 0;
        int e = 31;
        while ((mag & 0x80000000) == 0)
        {
            mag = mag << 1;
            e = e - 1;
        }
        uint sig = mag >> 8; // 24-bit significand, bit 23 leading
        uint rem = mag & 0xFF; // low 8 bits -> rounding
        int exp = e + 127;
        uint half = 0x80;
        if (rem > half || (rem == half && (sig & 1) != 0))
        {
            sig = sig + 1;
            if (sig >= ((uint)1 << 24))
            {
                sig = sig >> 1;
                exp = exp + 1;
            }
        }
        return ((uint)exp << 23) | (sig & 0x7FFFFF);
    }

    [KohRuntime("i32.toF32")]
    public static uint FromI32(int a)
    {
        if (a == 0)
            return 0;
        if (a < 0)
            return 0x80000000 | FromU32((uint)(-a));
        return FromU32((uint)a);
    }

    [KohRuntime("f32.toU32")]
    public static uint ToU32(uint a)
    {
        if ((a >> 31) != 0)
            return 0; // negative -> 0
        int e = (int)((a >> 23) & 0xFF);
        if (e == 0)
            return 0;
        int unbiased = e - 127;
        if (unbiased < 0)
            return 0;
        uint sig = (a & 0x7FFFFF) | 0x800000; // 24-bit, value = sig * 2^(unbiased-23)
        int shift = unbiased - 23;
        if (shift >= 0)
        {
            if (shift >= 9)
                return 0xFFFFFFFF; // >= 2^32: saturate (shift 8 still fits: sig<<8 <= ~2^32)
            return sig << shift;
        }
        int rs = -shift;
        if (rs >= 32)
            return 0;
        return sig >> rs; // truncate toward zero
    }

    [KohRuntime("f32.toI32")]
    public static int ToI32(uint a)
    {
        uint mag = a & 0x7FFFFFFF;
        uint u = ToU32(mag);
        uint signBit = 0x80000000;
        if ((a >> 31) != 0)
        {
            // Same saturation shape as ToI64 (see its comment): a magnitude beyond int.MinValue must
            // saturate explicitly, or -(int)u wraps back to a wrong positive result instead of saturating
            // like a host `(int)` cast does.
            if (u > signBit)
                return (int)signBit; // saturate to int.MinValue
            return -(int)u;
        }
        if (u >= signBit)
            return (int)(signBit - 1); // saturate to int.MaxValue
        return (int)u;
    }

    // ---- Koh softfloat (IEEE-754 double precision) ----------------------------
    // Same algorithms as the single-precision family, widened to 64-bit (11-bit exponent, bias 1023,
    // 52-bit mantissa) over ulong, using UInt128 for the 53x53 product and the division numerator.

    private static ulong ShrStickyF64(ulong v, int n)
    {
        if (n >= 64)
        {
            if (v != 0)
                return 1;
            return 0;
        }
        ulong lost = v & (((ulong)1 << n) - 1);
        ulong r = v >> n;
        if (lost != 0)
            r = r | 1;
        return r;
    }

    [KohRuntime("f64.neg")]
    public static ulong NegF64(ulong a)
    {
        return a ^ 0x8000000000000000;
    }

    [KohRuntime("f64.add")]
    public static ulong AddF64(ulong a, ulong b)
    {
        ulong sa = a >> 63;
        ulong sb = b >> 63;
        int ea = (int)((a >> 52) & 0x7FF);
        int eb = (int)((b >> 52) & 0x7FF);
        ulong ma = a & 0xFFFFFFFFFFFFF;
        ulong mb = b & 0xFFFFFFFFFFFFF;
        if (ea == 0)
        {
            if (eb == 0)
                return (sa & sb) << 63;
            return b;
        }
        if (eb == 0)
            return a;
        ulong siga = (ma | 0x10000000000000) << 3;
        ulong sigb = (mb | 0x10000000000000) << 3;
        int exp;
        if (ea > eb)
        {
            sigb = ShrStickyF64(sigb, ea - eb);
            exp = ea;
        }
        else if (eb > ea)
        {
            siga = ShrStickyF64(siga, eb - ea);
            exp = eb;
        }
        else
        {
            exp = ea;
        }
        ulong sig;
        ulong sign;
        if (sa == sb)
        {
            sig = siga + sigb;
            sign = sa;
        }
        else
        {
            if (siga >= sigb)
            {
                sig = siga - sigb;
                sign = sa;
            }
            else
            {
                sig = sigb - siga;
                sign = sb;
            }
            if (sig == 0)
                return 0;
        }
        while (sig >= ((ulong)1 << 56))
        {
            sig = ShrStickyF64(sig, 1);
            exp = exp + 1;
        }
        while (sig != 0 && sig < ((ulong)1 << 55))
        {
            sig = sig << 1;
            exp = exp - 1;
        }
        ulong roundBits = sig & 7;
        sig = sig >> 3;
        if (roundBits > 4 || (roundBits == 4 && (sig & 1) != 0))
        {
            sig = sig + 1;
            if (sig >= ((ulong)1 << 53))
            {
                sig = sig >> 1;
                exp = exp + 1;
            }
        }
        if (exp >= 2047)
            return (sign << 63) | ((ulong)0x7FF << 52);
        if (exp <= 0)
            return sign << 63;
        return (sign << 63) | ((ulong)exp << 52) | (sig & 0xFFFFFFFFFFFFF);
    }

    [KohRuntime("f64.sub")]
    public static ulong SubF64(ulong a, ulong b)
    {
        return AddF64(a, b ^ 0x8000000000000000);
    }

    [KohRuntime("f64.mul")]
    public static ulong MulF64(ulong a, ulong b)
    {
        ulong sign = (a ^ b) & 0x8000000000000000;
        int ea = (int)((a >> 52) & 0x7FF);
        int eb = (int)((b >> 52) & 0x7FF);
        ulong ma = a & 0xFFFFFFFFFFFFF;
        ulong mb = b & 0xFFFFFFFFFFFFF;
        if (ea == 0 || eb == 0)
            return sign;
        ulong siga = ma | 0x10000000000000;
        ulong sigb = mb | 0x10000000000000;
        UInt128 prod = (UInt128)siga * (UInt128)sigb; // up to 106 bits
        int exp = ea + eb - 1023;
        if (prod >= ((UInt128)1 << 105))
        {
            exp = exp + 1;
        }
        else
        {
            prod = prod << 1;
        } // leading 1 at bit 105
        ulong sig = (ulong)(prod >> 53); // 53-bit significand
        ulong rem = (ulong)(prod & (((UInt128)1 << 53) - 1));
        ulong half = (ulong)1 << 52;
        if (rem > half || (rem == half && (sig & 1) != 0))
        {
            sig = sig + 1;
            if (sig >= ((ulong)1 << 53))
            {
                sig = sig >> 1;
                exp = exp + 1;
            }
        }
        if (exp >= 2047)
            return sign | ((ulong)0x7FF << 52);
        if (exp <= 0)
            return sign;
        return sign | ((ulong)exp << 52) | (sig & 0xFFFFFFFFFFFFF);
    }

    [KohRuntime("f64.div")]
    public static ulong DivF64(ulong a, ulong b)
    {
        ulong sign = (a ^ b) & 0x8000000000000000;
        int ea = (int)((a >> 52) & 0x7FF);
        int eb = (int)((b >> 52) & 0x7FF);
        ulong ma = a & 0xFFFFFFFFFFFFF;
        ulong mb = b & 0xFFFFFFFFFFFFF;
        if (ea == 0)
            return sign;
        if (eb == 0)
            return sign | ((ulong)0x7FF << 52);
        ulong siga = ma | 0x10000000000000;
        ulong sigb = mb | 0x10000000000000;
        int exp = ea - eb + 1023;
        UInt128 num = (UInt128)siga << 54;
        ulong q = (ulong)(num / (UInt128)sigb);
        ulong r = (ulong)(num % (UInt128)sigb);
        if (q >= ((ulong)1 << 54)) { } // leading already at bit 54 (no-op)
        else
        {
            q = q << 1;
            exp = exp - 1;
        }
        ulong sig = q >> 2;
        ulong low = q & 3;
        ulong sticky = (r != 0) ? (ulong)1 : (ulong)0;
        bool up;
        if ((low & 2) == 0)
            up = false;
        else if (low == 2 && sticky == 0)
            up = (sig & 1) != 0;
        else
            up = true;
        if (up)
        {
            sig = sig + 1;
            if (sig >= ((ulong)1 << 53))
            {
                sig = sig >> 1;
                exp = exp + 1;
            }
        }
        if (exp >= 2047)
            return sign | ((ulong)0x7FF << 52);
        if (exp <= 0)
            return sign;
        return sign | ((ulong)exp << 52) | (sig & 0xFFFFFFFFFFFFF);
    }

    [KohRuntime("f64.isNan")]
    public static byte IsNanF64(ulong a)
    {
        if (((a >> 52) & 0x7FF) == 0x7FF && (a & 0xFFFFFFFFFFFFF) != 0)
            return 1;
        return 0;
    }

    [KohRuntime("f64.cmp")]
    public static byte CmpF64(ulong a, ulong b)
    {
        if (IsNanF64(a) != 0 || IsNanF64(b) != 0)
            return 3;
        if (((a | b) << 1) == 0)
            return 0;
        ulong sa = a >> 63;
        ulong sb = b >> 63;
        if (sa != sb)
        {
            if (sa != 0)
                return 1;
            return 2;
        }
        ulong ma = a & 0x7FFFFFFFFFFFFFFF;
        ulong mb = b & 0x7FFFFFFFFFFFFFFF;
        if (ma == mb)
            return 0;
        if (sa == 0)
        {
            if (ma < mb)
                return 1;
            return 2;
        }
        if (ma > mb)
            return 1;
        return 2;
    }

    [KohRuntime("f64.lt")]
    public static bool LtF64(ulong a, ulong b)
    {
        return CmpF64(a, b) == 1;
    }

    [KohRuntime("f64.le")]
    public static bool LeF64(ulong a, ulong b)
    {
        byte c = CmpF64(a, b);
        return c == 1 || c == 0;
    }

    [KohRuntime("f64.gt")]
    public static bool GtF64(ulong a, ulong b)
    {
        return CmpF64(a, b) == 2;
    }

    [KohRuntime("f64.ge")]
    public static bool GeF64(ulong a, ulong b)
    {
        byte c = CmpF64(a, b);
        return c == 2 || c == 0;
    }

    [KohRuntime("f64.eq")]
    public static bool EqF64(ulong a, ulong b)
    {
        return CmpF64(a, b) == 0;
    }

    [KohRuntime("f64.ne")]
    public static bool NeF64(ulong a, ulong b)
    {
        return CmpF64(a, b) != 0;
    }

    [KohRuntime("u64.toF64")]
    public static ulong FromU64(ulong mag)
    {
        if (mag == 0)
            return 0;
        int e = 63;
        while ((mag & 0x8000000000000000) == 0)
        {
            mag = mag << 1;
            e = e - 1;
        }
        ulong sig = mag >> 11; // 53-bit significand
        ulong rem = mag & 0x7FF; // low 11 bits -> rounding
        int exp = e + 1023;
        ulong half = 0x400;
        if (rem > half || (rem == half && (sig & 1) != 0))
        {
            sig = sig + 1;
            if (sig >= ((ulong)1 << 53))
            {
                sig = sig >> 1;
                exp = exp + 1;
            }
        }
        return ((ulong)exp << 52) | (sig & 0xFFFFFFFFFFFFF);
    }

    [KohRuntime("i64.toF64")]
    public static ulong FromI64(long a)
    {
        if (a == 0)
            return 0;
        if (a < 0)
            return 0x8000000000000000 | FromU64((ulong)(-a));
        return FromU64((ulong)a);
    }

    [KohRuntime("f64.toU64")]
    public static ulong ToU64(ulong a)
    {
        if ((a >> 63) != 0)
            return 0;
        int e = (int)((a >> 52) & 0x7FF);
        if (e == 0)
            return 0;
        int unbiased = e - 1023;
        if (unbiased < 0)
            return 0;
        ulong sig = (a & 0xFFFFFFFFFFFFF) | 0x10000000000000;
        int shift = unbiased - 52;
        if (shift >= 0)
        {
            if (shift >= 12)
                return 0xFFFFFFFFFFFFFFFF; // >= 2^64: saturate
            return sig << shift;
        }
        int rs = -shift;
        if (rs >= 64)
            return 0;
        return sig >> rs;
    }

    [KohRuntime("f64.toI64")]
    public static long ToI64(ulong a)
    {
        ulong mag = a & 0x7FFFFFFFFFFFFFFF;
        ulong u = ToU64(mag);
        ulong signBit = 0x8000000000000000;
        if ((a >> 63) != 0)
        {
            // Negative: magnitudes up to 2^63 (exactly long.MinValue) negate cleanly via wraparound; a
            // magnitude beyond that must saturate explicitly, or -(long)u wraps back to a positive result
            // (e.g. u = 0xFFFFFFFFFFFFFFFF -> -(long)u = 1, not long.MinValue) instead of saturating like a
            // host `(long)` cast does.
            if (u > signBit)
                return (long)signBit; // saturate to long.MinValue
            return -(long)u;
        }
        if (u >= signBit)
            return (long)(signBit - 1); // saturate to long.MaxValue
        return (long)u;
    }

    [KohRuntime("f32.toF64")]
    public static ulong ToF64(uint a)
    {
        ulong sign64 = (ulong)(a & 0x80000000) << 32;
        int e = (int)((a >> 23) & 0xFF);
        ulong mant = (ulong)(a & 0x7FFFFF) << 29; // 23-bit mantissa -> top of 52
        if (e == 0)
            return sign64; // zero (subnormal flushed)
        if (e == 0xFF)
            return sign64 | ((ulong)0x7FF << 52) | mant; // inf/nan
        int exp = e - 127 + 1023;
        return sign64 | ((ulong)exp << 52) | mant;
    }

    [KohRuntime("f64.toF32")]
    public static uint ToF32(ulong a)
    {
        uint sign = (uint)(a >> 32) & 0x80000000;
        int e = (int)((a >> 52) & 0x7FF);
        ulong m = a & 0xFFFFFFFFFFFFF;
        if (e == 0)
            return sign; // zero (subnormal flushed)
        if (e == 0x7FF)
        {
            uint nan = (uint)(m >> 29);
            if (m != 0 && nan == 0)
                nan = 1;
            return sign | ((uint)0xFF << 23) | nan;
        }
        int exp = e - 1023 + 127;
        uint sig = (uint)(m >> 29); // 23-bit
        ulong rem = m & 0x1FFFFFFF; // low 29 bits -> rounding
        ulong half = 0x10000000;
        if (rem > half || (rem == half && (sig & 1) != 0))
        {
            sig = sig + 1;
            if (sig >= 0x800000)
            {
                sig = 0;
                exp = exp + 1;
            }
        }
        if (exp >= 255)
            return sign | ((uint)0xFF << 23);
        if (exp <= 0)
            return sign;
        return sign | ((uint)exp << 23) | (sig & 0x7FFFFF);
    }
}
