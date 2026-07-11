namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// The IEEE-754 single-precision softfloat runtime, written in the Koh C# subset. The frontend appends
/// this source to a program that uses <c>float</c>/<c>double</c> (see <c>CSharpFrontend.UsesFloat</c>), so
/// float operators/comparisons/conversions — which lower to calls like <c>__f32_add</c> — resolve to
/// compiled code. Nothing here is hardcoded in the backend: it is ordinary source the compiler compiles,
/// operating on the raw IEEE bits carried in <c>uint</c>. Correctness is pinned bit-for-bit against real
/// .NET <c>float</c> by the golden tests over the normal range. (M1 covers single precision over normal
/// numbers, zero, and infinity; <c>double</c> is a later milestone. Subnormal inputs are flushed to zero
/// — a defined behavior, not garbage — rather than handled exactly; full subnormal support is a later
/// refinement.)
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
        if (shift >= 8) return 0xFFFFFFFF; // saturate
        return sig << shift;
    }
    int rs = -shift;
    if (rs >= 32) return 0;
    return sig >> rs; // truncate toward zero
}

static int __f32_to_i32(uint a) {
    uint mag = a & 0x7FFFFFFF;
    uint u = __f32_to_u32(mag);
    if ((a >> 31) != 0) return -(int)u;
    return (int)u;
}
";
}
