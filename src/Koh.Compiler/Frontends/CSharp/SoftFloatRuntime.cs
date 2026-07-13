namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// The IEEE-754 single-precision softfloat runtime, written in the Koh C# subset. The frontend appends
/// this source to a program that uses <c>float</c>/<c>double</c> (see <c>CSharpFrontend.UsesFloat</c>), so
/// float operators/comparisons/conversions — which lower to calls like <c>__f32_add</c> — resolve to
/// compiled code. Nothing here is hardcoded in the backend: it is ordinary source the compiler compiles,
/// operating on the raw IEEE bits carried in <c>uint</c>. Correctness is pinned bit-for-bit against real
/// .NET <c>float</c> by the golden tests over finite normal numbers and zero, with round-to-nearest-even.
/// The <c>__f64_*</c> family is the same, widened to <c>double</c> (64-bit) over <c>ulong</c>/<c>UInt128</c>.
///
/// Note: the softfloat/MathF functions form a call graph that the backend gives one disjoint WRAM frame
/// per function (frames are not yet shared). A complex float expression therefore relies on the optimizer
/// inlining the small helpers to keep the live-function count down; CompilerDriver always optimizes, so
/// this holds for every shipped ROM. Bit-level ops (Round/Truncate/Abs) are kept self-contained to stay
/// cheap. Sharing frames across non-interfering functions is the proper backend fix (later work).
///
/// Scope / not-yet-handled (documented, later refinements): infinity and NaN as operands are not special-
/// cased in arithmetic (e.g. <c>inf - inf</c> does not yield NaN), so results for non-finite operands may
/// differ from .NET; division by a finite zero does yield infinity as a courtesy. Subnormal inputs are
/// flushed to zero (a defined behavior, not garbage). The bit-identity guarantee therefore covers finite
/// normal numbers and zero — exactly what the golden tests exercise.
/// </summary>
internal static class SoftFloatRuntime
{
    public const string Source =
        @"
// ---- Koh softfloat (IEEE-754 single precision) ----------------------------

static uint __f32_shr_sticky(uint v, int n) {
    if (n >= 32) { if (v != 0) return 1; return 0; }
    uint lost = v & (((uint)1 << n) - 1);
    uint r = v >> n;
    if (lost != 0) r = r | 1;
    return r;
}

static uint __f32_neg(uint a) { return a ^ 0x80000000; }

static uint __f32_add(uint a, uint b) {
    uint sa = a >> 31;
    uint sb = b >> 31;
    int ea = (int)((a >> 23) & 0xFF);
    int eb = (int)((b >> 23) & 0xFF);
    uint ma = a & 0x7FFFFF;
    uint mb = b & 0x7FFFFF;
    if (ea == 0) { // zero or subnormal (flushed to zero)
        if (eb == 0) return (sa & sb) << 31;
        return b;
    }
    if (eb == 0) return a;
    uint siga = (ma | 0x800000) << 3;
    uint sigb = (mb | 0x800000) << 3;
    int exp;
    if (ea > eb) { sigb = __f32_shr_sticky(sigb, ea - eb); exp = ea; }
    else if (eb > ea) { siga = __f32_shr_sticky(siga, eb - ea); exp = eb; }
    else { exp = ea; }
    uint sig;
    uint sign;
    if (sa == sb) { sig = siga + sigb; sign = sa; }
    else {
        if (siga >= sigb) { sig = siga - sigb; sign = sa; }
        else { sig = sigb - siga; sign = sb; }
        if (sig == 0) return 0;
    }
    while (sig >= ((uint)1 << 27)) { sig = __f32_shr_sticky(sig, 1); exp = exp + 1; }
    while (sig != 0 && sig < ((uint)1 << 26)) { sig = sig << 1; exp = exp - 1; }
    uint roundBits = sig & 7;
    sig = sig >> 3;
    if (roundBits > 4 || (roundBits == 4 && (sig & 1) != 0)) {
        sig = sig + 1;
        if (sig >= ((uint)1 << 24)) { sig = sig >> 1; exp = exp + 1; }
    }
    if (exp >= 255) return (sign << 31) | ((uint)0xFF << 23);
    if (exp <= 0) return sign << 31;
    return (sign << 31) | ((uint)exp << 23) | (sig & 0x7FFFFF);
}

static uint __f32_sub(uint a, uint b) { return __f32_add(a, b ^ 0x80000000); }

static uint __f32_mul(uint a, uint b) {
    uint sign = (a ^ b) & 0x80000000;
    int ea = (int)((a >> 23) & 0xFF);
    int eb = (int)((b >> 23) & 0xFF);
    uint ma = a & 0x7FFFFF;
    uint mb = b & 0x7FFFFF;
    if (ea == 0 || eb == 0) return sign; // 0 * x = 0 (subnormals flushed to zero)
    uint siga = ma | 0x800000;
    uint sigb = mb | 0x800000;
    ulong prod = (ulong)siga * (ulong)sigb; // up to 48 bits
    int exp = ea + eb - 127;
    if (prod >= ((ulong)1 << 47)) { exp = exp + 1; }
    else { prod = prod << 1; } // leading 1 now at bit 47
    uint sig = (uint)(prod >> 24);          // 24-bit significand (bit 23 = leading 1)
    uint rem = (uint)(prod & 0xFFFFFF);     // low 24 bits -> rounding
    uint half = 0x800000;
    if (rem > half || (rem == half && (sig & 1) != 0)) {
        sig = sig + 1;
        if (sig >= ((uint)1 << 24)) { sig = sig >> 1; exp = exp + 1; }
    }
    if (exp >= 255) return sign | ((uint)0xFF << 23);
    if (exp <= 0) return sign;
    return sign | ((uint)exp << 23) | (sig & 0x7FFFFF);
}

static uint __f32_div(uint a, uint b) {
    uint sign = (a ^ b) & 0x80000000;
    int ea = (int)((a >> 23) & 0xFF);
    int eb = (int)((b >> 23) & 0xFF);
    uint ma = a & 0x7FFFFF;
    uint mb = b & 0x7FFFFF;
    if (ea == 0) return sign;                         // 0 / x = 0 (subnormals flushed)
    if (eb == 0) return sign | ((uint)0xFF << 23);    // x / 0 = inf
    uint siga = ma | 0x800000;
    uint sigb = mb | 0x800000;
    int exp = ea - eb + 127;
    ulong num = (ulong)siga << 25;
    uint q = (uint)(num / (ulong)sigb);
    uint r = (uint)(num % (ulong)sigb);
    if (q >= ((uint)1 << 25)) { exp = exp; }   // leading already at bit 25
    else { q = q << 1; exp = exp - 1; }         // normalize leading to bit 25
    uint sig = q >> 2;          // 24-bit significand
    uint low = q & 3;           // guard + round
    uint sticky = (r != 0) ? (uint)1 : (uint)0;
    bool up;
    if ((low & 2) == 0) up = false;
    else if (low == 2 && sticky == 0) up = (sig & 1) != 0;
    else up = true;
    if (up) {
        sig = sig + 1;
        if (sig >= ((uint)1 << 24)) { sig = sig >> 1; exp = exp + 1; }
    }
    if (exp >= 255) return sign | ((uint)0xFF << 23);
    if (exp <= 0) return sign;
    return sign | ((uint)exp << 23) | (sig & 0x7FFFFF);
}

// ---- Comparisons (return bool) --------------------------------------------

static byte __f32_isnan(uint a) {
    if (((a >> 23) & 0xFF) == 0xFF && (a & 0x7FFFFF) != 0) return 1;
    return 0;
}

// 0 = equal, 1 = a<b, 2 = a>b, 3 = unordered (NaN)
static byte __f32_cmp(uint a, uint b) {
    if (__f32_isnan(a) != 0 || __f32_isnan(b) != 0) return 3;
    if (((a | b) << 1) == 0) return 0; // +0 == -0
    uint sa = a >> 31;
    uint sb = b >> 31;
    if (sa != sb) { if (sa != 0) return 1; return 2; }
    uint ma = a & 0x7FFFFFFF;
    uint mb = b & 0x7FFFFFFF;
    if (ma == mb) return 0;
    if (sa == 0) { if (ma < mb) return 1; return 2; }
    if (ma > mb) return 1;
    return 2;
}

static bool __f32_lt(uint a, uint b) { return __f32_cmp(a, b) == 1; }
static bool __f32_le(uint a, uint b) { byte c = __f32_cmp(a, b); return c == 1 || c == 0; }
static bool __f32_gt(uint a, uint b) { return __f32_cmp(a, b) == 2; }
static bool __f32_ge(uint a, uint b) { byte c = __f32_cmp(a, b); return c == 2 || c == 0; }
static bool __f32_eq(uint a, uint b) { return __f32_cmp(a, b) == 0; }
static bool __f32_ne(uint a, uint b) { return __f32_cmp(a, b) != 0; }

// ---- Integer <-> float conversions ----------------------------------------

static uint __u32_to_f32(uint mag) {
    if (mag == 0) return 0;
    int e = 31;
    while ((mag & 0x80000000) == 0) { mag = mag << 1; e = e - 1; }
    uint sig = mag >> 8;        // 24-bit significand, bit 23 leading
    uint rem = mag & 0xFF;      // low 8 bits -> rounding
    int exp = e + 127;
    uint half = 0x80;
    if (rem > half || (rem == half && (sig & 1) != 0)) {
        sig = sig + 1;
        if (sig >= ((uint)1 << 24)) { sig = sig >> 1; exp = exp + 1; }
    }
    return ((uint)exp << 23) | (sig & 0x7FFFFF);
}

static uint __i32_to_f32(int a) {
    if (a == 0) return 0;
    if (a < 0) return 0x80000000 | __u32_to_f32((uint)(-a));
    return __u32_to_f32((uint)a);
}

static uint __f32_to_u32(uint a) {
    if ((a >> 31) != 0) return 0; // negative -> 0
    int e = (int)((a >> 23) & 0xFF);
    if (e == 0) return 0;
    int unbiased = e - 127;
    if (unbiased < 0) return 0;
    uint sig = (a & 0x7FFFFF) | 0x800000; // 24-bit, value = sig * 2^(unbiased-23)
    int shift = unbiased - 23;
    if (shift >= 0) {
        if (shift >= 9) return 0xFFFFFFFF; // >= 2^32: saturate (shift 8 still fits: sig<<8 <= ~2^32)
        return sig << shift;
    }
    int rs = -shift;
    if (rs >= 32) return 0;
    return sig >> rs; // truncate toward zero
}

static int __f32_to_i32(uint a) {
    uint mag = a & 0x7FFFFFFF;
    uint u = __f32_to_u32(mag);
    uint signBit = 0x80000000;
    if ((a >> 31) != 0) {
        // Same saturation shape as __f64_to_i64 (see its comment): a magnitude beyond int.MinValue must
        // saturate explicitly, or -(int)u wraps back to a wrong positive result instead of saturating like
        // a host `(int)` cast does.
        if (u > signBit) return (int)signBit; // saturate to int.MinValue
        return -(int)u;
    }
    if (u >= signBit) return (int)(signBit - 1); // saturate to int.MaxValue
    return (int)u;
}

// ---- Koh softfloat (IEEE-754 double precision) ----------------------------
// Same algorithms as the single-precision family, widened to 64-bit (11-bit exponent, bias 1023, 52-bit
// mantissa) over ulong, using UInt128 for the 53x53 product and the division numerator.

static ulong __f64_shr_sticky(ulong v, int n) {
    if (n >= 64) { if (v != 0) return 1; return 0; }
    ulong lost = v & (((ulong)1 << n) - 1);
    ulong r = v >> n;
    if (lost != 0) r = r | 1;
    return r;
}

static ulong __f64_neg(ulong a) { return a ^ 0x8000000000000000; }

static ulong __f64_add(ulong a, ulong b) {
    ulong sa = a >> 63;
    ulong sb = b >> 63;
    int ea = (int)((a >> 52) & 0x7FF);
    int eb = (int)((b >> 52) & 0x7FF);
    ulong ma = a & 0xFFFFFFFFFFFFF;
    ulong mb = b & 0xFFFFFFFFFFFFF;
    if (ea == 0) { if (eb == 0) return (sa & sb) << 63; return b; }
    if (eb == 0) return a;
    ulong siga = (ma | 0x10000000000000) << 3;
    ulong sigb = (mb | 0x10000000000000) << 3;
    int exp;
    if (ea > eb) { sigb = __f64_shr_sticky(sigb, ea - eb); exp = ea; }
    else if (eb > ea) { siga = __f64_shr_sticky(siga, eb - ea); exp = eb; }
    else { exp = ea; }
    ulong sig;
    ulong sign;
    if (sa == sb) { sig = siga + sigb; sign = sa; }
    else {
        if (siga >= sigb) { sig = siga - sigb; sign = sa; }
        else { sig = sigb - siga; sign = sb; }
        if (sig == 0) return 0;
    }
    while (sig >= ((ulong)1 << 56)) { sig = __f64_shr_sticky(sig, 1); exp = exp + 1; }
    while (sig != 0 && sig < ((ulong)1 << 55)) { sig = sig << 1; exp = exp - 1; }
    ulong roundBits = sig & 7;
    sig = sig >> 3;
    if (roundBits > 4 || (roundBits == 4 && (sig & 1) != 0)) {
        sig = sig + 1;
        if (sig >= ((ulong)1 << 53)) { sig = sig >> 1; exp = exp + 1; }
    }
    if (exp >= 2047) return (sign << 63) | ((ulong)0x7FF << 52);
    if (exp <= 0) return sign << 63;
    return (sign << 63) | ((ulong)exp << 52) | (sig & 0xFFFFFFFFFFFFF);
}

static ulong __f64_sub(ulong a, ulong b) { return __f64_add(a, b ^ 0x8000000000000000); }

static ulong __f64_mul(ulong a, ulong b) {
    ulong sign = (a ^ b) & 0x8000000000000000;
    int ea = (int)((a >> 52) & 0x7FF);
    int eb = (int)((b >> 52) & 0x7FF);
    ulong ma = a & 0xFFFFFFFFFFFFF;
    ulong mb = b & 0xFFFFFFFFFFFFF;
    if (ea == 0 || eb == 0) return sign;
    ulong siga = ma | 0x10000000000000;
    ulong sigb = mb | 0x10000000000000;
    UInt128 prod = (UInt128)siga * (UInt128)sigb; // up to 106 bits
    int exp = ea + eb - 1023;
    if (prod >= ((UInt128)1 << 105)) { exp = exp + 1; }
    else { prod = prod << 1; } // leading 1 at bit 105
    ulong sig = (ulong)(prod >> 53);                    // 53-bit significand
    ulong rem = (ulong)(prod & (((UInt128)1 << 53) - 1));
    ulong half = (ulong)1 << 52;
    if (rem > half || (rem == half && (sig & 1) != 0)) {
        sig = sig + 1;
        if (sig >= ((ulong)1 << 53)) { sig = sig >> 1; exp = exp + 1; }
    }
    if (exp >= 2047) return sign | ((ulong)0x7FF << 52);
    if (exp <= 0) return sign;
    return sign | ((ulong)exp << 52) | (sig & 0xFFFFFFFFFFFFF);
}

static ulong __f64_div(ulong a, ulong b) {
    ulong sign = (a ^ b) & 0x8000000000000000;
    int ea = (int)((a >> 52) & 0x7FF);
    int eb = (int)((b >> 52) & 0x7FF);
    ulong ma = a & 0xFFFFFFFFFFFFF;
    ulong mb = b & 0xFFFFFFFFFFFFF;
    if (ea == 0) return sign;
    if (eb == 0) return sign | ((ulong)0x7FF << 52);
    ulong siga = ma | 0x10000000000000;
    ulong sigb = mb | 0x10000000000000;
    int exp = ea - eb + 1023;
    UInt128 num = (UInt128)siga << 54;
    ulong q = (ulong)(num / (UInt128)sigb);
    ulong r = (ulong)(num % (UInt128)sigb);
    if (q >= ((ulong)1 << 54)) { exp = exp; }
    else { q = q << 1; exp = exp - 1; }
    ulong sig = q >> 2;
    ulong low = q & 3;
    ulong sticky = (r != 0) ? (ulong)1 : (ulong)0;
    bool up;
    if ((low & 2) == 0) up = false;
    else if (low == 2 && sticky == 0) up = (sig & 1) != 0;
    else up = true;
    if (up) {
        sig = sig + 1;
        if (sig >= ((ulong)1 << 53)) { sig = sig >> 1; exp = exp + 1; }
    }
    if (exp >= 2047) return sign | ((ulong)0x7FF << 52);
    if (exp <= 0) return sign;
    return sign | ((ulong)exp << 52) | (sig & 0xFFFFFFFFFFFFF);
}

static byte __f64_isnan(ulong a) {
    if (((a >> 52) & 0x7FF) == 0x7FF && (a & 0xFFFFFFFFFFFFF) != 0) return 1;
    return 0;
}

static byte __f64_cmp(ulong a, ulong b) {
    if (__f64_isnan(a) != 0 || __f64_isnan(b) != 0) return 3;
    if (((a | b) << 1) == 0) return 0;
    ulong sa = a >> 63;
    ulong sb = b >> 63;
    if (sa != sb) { if (sa != 0) return 1; return 2; }
    ulong ma = a & 0x7FFFFFFFFFFFFFFF;
    ulong mb = b & 0x7FFFFFFFFFFFFFFF;
    if (ma == mb) return 0;
    if (sa == 0) { if (ma < mb) return 1; return 2; }
    if (ma > mb) return 1;
    return 2;
}

static bool __f64_lt(ulong a, ulong b) { return __f64_cmp(a, b) == 1; }
static bool __f64_le(ulong a, ulong b) { byte c = __f64_cmp(a, b); return c == 1 || c == 0; }
static bool __f64_gt(ulong a, ulong b) { return __f64_cmp(a, b) == 2; }
static bool __f64_ge(ulong a, ulong b) { byte c = __f64_cmp(a, b); return c == 2 || c == 0; }
static bool __f64_eq(ulong a, ulong b) { return __f64_cmp(a, b) == 0; }
static bool __f64_ne(ulong a, ulong b) { return __f64_cmp(a, b) != 0; }

static ulong __u64_to_f64(ulong mag) {
    if (mag == 0) return 0;
    int e = 63;
    while ((mag & 0x8000000000000000) == 0) { mag = mag << 1; e = e - 1; }
    ulong sig = mag >> 11;      // 53-bit significand
    ulong rem = mag & 0x7FF;    // low 11 bits -> rounding
    int exp = e + 1023;
    ulong half = 0x400;
    if (rem > half || (rem == half && (sig & 1) != 0)) {
        sig = sig + 1;
        if (sig >= ((ulong)1 << 53)) { sig = sig >> 1; exp = exp + 1; }
    }
    return ((ulong)exp << 52) | (sig & 0xFFFFFFFFFFFFF);
}

static ulong __i64_to_f64(long a) {
    if (a == 0) return 0;
    if (a < 0) return 0x8000000000000000 | __u64_to_f64((ulong)(-a));
    return __u64_to_f64((ulong)a);
}

static ulong __f64_to_u64(ulong a) {
    if ((a >> 63) != 0) return 0;
    int e = (int)((a >> 52) & 0x7FF);
    if (e == 0) return 0;
    int unbiased = e - 1023;
    if (unbiased < 0) return 0;
    ulong sig = (a & 0xFFFFFFFFFFFFF) | 0x10000000000000;
    int shift = unbiased - 52;
    if (shift >= 0) {
        if (shift >= 12) return 0xFFFFFFFFFFFFFFFF; // >= 2^64: saturate
        return sig << shift;
    }
    int rs = -shift;
    if (rs >= 64) return 0;
    return sig >> rs;
}

static long __f64_to_i64(ulong a) {
    ulong mag = a & 0x7FFFFFFFFFFFFFFF;
    ulong u = __f64_to_u64(mag);
    ulong signBit = 0x8000000000000000;
    if ((a >> 63) != 0) {
        // Negative: magnitudes up to 2^63 (exactly long.MinValue) negate cleanly via wraparound; a
        // magnitude beyond that must saturate explicitly, or -(long)u wraps back to a positive result
        // (e.g. u = 0xFFFFFFFFFFFFFFFF -> -(long)u = 1, not long.MinValue) instead of saturating like a
        // host `(long)` cast does.
        if (u > signBit) return (long)signBit; // saturate to long.MinValue
        return -(long)u;
    }
    if (u >= signBit) return (long)(signBit - 1); // saturate to long.MaxValue
    return (long)u;
}

static ulong __f32_to_f64(uint a) {
    ulong sign64 = (ulong)(a & 0x80000000) << 32;
    int e = (int)((a >> 23) & 0xFF);
    ulong mant = (ulong)(a & 0x7FFFFF) << 29; // 23-bit mantissa -> top of 52
    if (e == 0) return sign64;                 // zero (subnormal flushed)
    if (e == 0xFF) return sign64 | ((ulong)0x7FF << 52) | mant; // inf/nan
    int exp = e - 127 + 1023;
    return sign64 | ((ulong)exp << 52) | mant;
}

static uint __f64_to_f32(ulong a) {
    uint sign = (uint)(a >> 32) & 0x80000000;
    int e = (int)((a >> 52) & 0x7FF);
    ulong m = a & 0xFFFFFFFFFFFFF;
    if (e == 0) return sign; // zero (subnormal flushed)
    if (e == 0x7FF) {
        uint nan = (uint)(m >> 29);
        if (m != 0 && nan == 0) nan = 1;
        return sign | ((uint)0xFF << 23) | nan;
    }
    int exp = e - 1023 + 127;
    uint sig = (uint)(m >> 29);   // 23-bit
    ulong rem = m & 0x1FFFFFFF;   // low 29 bits -> rounding
    ulong half = 0x10000000;
    if (rem > half || (rem == half && (sig & 1) != 0)) {
        sig = sig + 1;
        if (sig >= 0x800000) { sig = 0; exp = exp + 1; }
    }
    if (exp >= 255) return sign | ((uint)0xFF << 23);
    if (exp <= 0) return sign;
    return sign | ((uint)exp << 23) | (sig & 0x7FFFFF);
}

// ---- MathF (single-precision Math), as ordinary compiled library source -----
// Resolved by name (MathF.Round / System.MathF.Round), operating on float via the softfloat ops above and
// BitConverter reinterprets. Matches real .NET MathF so the managed build agrees. (Sqrt is not yet
// provided — a correctly-rounded software sqrt is a later refinement.)

static class MathF {
    static float Abs(float x) {
        return BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(x) & 0x7FFFFFFF);
    }

    static float Truncate(float x) {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        int unbiased = (int)((bits >> 23) & 0xFF) - 127;
        if (unbiased >= 23) return x;                                          // integral, or inf/NaN
        if (unbiased < 0) return BitConverter.UInt32BitsToSingle(bits & 0x80000000); // |x| < 1 -> +/-0
        uint mask = ~(((uint)1 << (23 - unbiased)) - 1);
        return BitConverter.UInt32BitsToSingle(bits & mask);
    }

    static float Floor(float x) {
        float t = Truncate(x);
        if (x < 0f && t != x) return t - 1f;
        return t;
    }

    static float Ceiling(float x) {
        float t = Truncate(x);
        if (x > 0f && t != x) return t + 1f;
        return t;
    }

    // Round to nearest integer, ties to even — done purely on the bits (no calls to other float ops), so
    // it stays one self-contained function.
    static float Round(float x) {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        uint sign = bits & 0x80000000;
        int e = (int)((bits >> 23) & 0xFF) - 127;
        if (e >= 23) return x;                                       // no fractional bits (integral/inf/nan)
        if (e < -1) return BitConverter.UInt32BitsToSingle(sign);    // |x| < 0.5 -> +/-0
        if (e == -1) {                                               // 0.5 <= |x| < 1
            if ((bits & 0x7FFFFF) == 0) return BitConverter.UInt32BitsToSingle(sign);   // exactly 0.5 -> 0
            return BitConverter.UInt32BitsToSingle(sign | 0x3F800000);                   // -> 1.0
        }
        int fracBits = 23 - e;
        uint fracMask = ((uint)1 << fracBits) - 1;
        uint frac = bits & fracMask;
        uint kept = bits & ~fracMask;
        uint half = (uint)1 << (fracBits - 1);
        // Round up on > half, or on an exact tie when the kept integer's low bit is 1 (round to even).
        if (frac > half || (frac == half && (bits & ((uint)1 << fracBits)) != 0))
            kept = kept + ((uint)1 << fracBits);   // a carry into the exponent yields the correct next value
        return BitConverter.UInt32BitsToSingle(kept);
    }

    static float Min(float a, float b) {
        if (a != a) return a;                   // NaN propagates
        if (b != b) return b;
        if (a < b) return a;
        if (b < a) return b;
        if ((BitConverter.SingleToUInt32Bits(a) >> 31) != 0) return a; // signed zero: -0 is the min
        return b;
    }

    static float Max(float a, float b) {
        if (a != a) return a;
        if (b != b) return b;
        if (a > b) return a;
        if (b > a) return b;
        if ((BitConverter.SingleToUInt32Bits(a) >> 31) == 0) return a; // signed zero: +0 is the max
        return b;
    }

    static int Sign(float x) {
        if (x > 0f) return 1;
        if (x < 0f) return -1;
        return 0;
    }
}
";
}
