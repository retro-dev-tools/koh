using Koh.GameBoy;

namespace Koh.Compiler.Tests;

/// <summary>
/// Managed differential test for the <see cref="SoftFloat"/> port (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>, §3): runs the ported routines
/// directly as ordinary .NET code (no compiler, no ROM, no emulator — <c>SoftFloat</c> is plain C#) and
/// compares every result bit-for-bit against real .NET <c>float</c>/<c>double</c> arithmetic. This is
/// the actual correctness pin for the transcription itself; the CIL end-to-end test
/// (<c>CilKohRuntimeTests</c>) only proves ONE routine (<c>AddF32</c>) lowers cleanly through the CIL
/// frontend on ONE input — it is not a substitute for exercising all ~40 ported functions.
///
/// The value domain mirrors <c>CSharpEndToEndTests</c>' existing float golden tests (same documented
/// scope: finite normal numbers and zero, round-to-nearest-even; subnormal inputs flush to zero and
/// infinities/NaNs as operands are not special-cased — not exercised here, same as there).
/// </summary>
public class SoftFloatDifferentialTests
{
    private static uint Bits(float x) => BitConverter.SingleToUInt32Bits(x);

    private static ulong Bits(double x) => BitConverter.DoubleToUInt64Bits(x);

    // ---- f32 arithmetic ---------------------------------------------------------------------

    [Test]
    [Arguments(1.5f, 2.0f)]
    [Arguments(0.1f, 0.2f)] // round-to-nearest-even
    [Arguments(-2.5f, 0.75f)] // opposite signs
    [Arguments(1.0f, -1.0f)] // exact cancellation
    [Arguments(100.0f, 0.5f)] // wide exponent gap -> shift/sticky alignment
    [Arguments(1000000.0f, 0.25f)] // very wide gap -> the small operand mostly sticky
    public async Task AddF32_MatchesHost(float x, float y) =>
        await Assert.That(SoftFloat.AddF32(Bits(x), Bits(y))).IsEqualTo(Bits(x + y));

    [Test]
    [Arguments(3.5f, 1.25f)]
    [Arguments(1.0f, 4.0f)] // negative result
    public async Task SubF32_MatchesHost(float x, float y) =>
        await Assert.That(SoftFloat.SubF32(Bits(x), Bits(y))).IsEqualTo(Bits(x - y));

    [Test]
    [Arguments(2.0f, 3.0f)]
    [Arguments(0.1f, 0.1f)] // rounding
    [Arguments(-1.5f, 4.0f)]
    public async Task MulF32_MatchesHost(float x, float y) =>
        await Assert.That(SoftFloat.MulF32(Bits(x), Bits(y))).IsEqualTo(Bits(x * y));

    [Test]
    [Arguments(7.0f, 2.0f)]
    [Arguments(1.0f, 3.0f)] // non-terminating -> rounding
    [Arguments(-9.0f, 4.0f)]
    public async Task DivF32_MatchesHost(float x, float y) =>
        await Assert.That(SoftFloat.DivF32(Bits(x), Bits(y))).IsEqualTo(Bits(x / y));

    [Test]
    [Arguments(3.5f)]
    [Arguments(-3.5f)]
    public async Task NegF32_MatchesHost(float x) =>
        await Assert.That(SoftFloat.NegF32(Bits(x))).IsEqualTo(Bits(-x));

    // ---- f32 comparisons ----------------------------------------------------------------------

    [Test]
    [Arguments(1.5f, 2.0f)]
    [Arguments(2.0f, 1.5f)]
    [Arguments(2.0f, 2.0f)]
    [Arguments(-1.0f, -2.0f)]
    [Arguments(0.1f, 0.1f)]
    [Arguments(-0.0f, 0.0f)] // -0.0 == 0.0
    public async Task F32Comparisons_MatchHost(float x, float y)
    {
        uint a = Bits(x);
        uint b = Bits(y);
        await Assert.That(SoftFloat.LtF32(a, b)).IsEqualTo(x < y);
        await Assert.That(SoftFloat.LeF32(a, b)).IsEqualTo(x <= y);
        await Assert.That(SoftFloat.GtF32(a, b)).IsEqualTo(x > y);
        await Assert.That(SoftFloat.GeF32(a, b)).IsEqualTo(x >= y);
        await Assert.That(SoftFloat.EqF32(a, b)).IsEqualTo(x == y);
        await Assert.That(SoftFloat.NeF32(a, b)).IsEqualTo(x != y);
    }

    // ---- f32 <-> int conversions ----------------------------------------------------------------

    [Test]
    [Arguments(3.7f, 3)]
    [Arguments(-3.7f, -3)] // truncates toward zero
    [Arguments(5.0f, 5)]
    [Arguments(0.9f, 0)]
    [Arguments(-0.9f, 0)]
    [Arguments(1000.25f, 1000)]
    [Arguments(-2147483648.0f, -2147483648)] // int.MinValue exactly: the saturation edge
    [Arguments(1e20f, int.MaxValue)] // magnitude overflows int -> saturate (positive)
    [Arguments(-1e20f, int.MinValue)] // magnitude overflows int -> saturate (negative)
    public async Task ToI32_MatchesHost(float x, int expected) =>
        await Assert.That(SoftFloat.ToI32(Bits(x))).IsEqualTo(expected);

    [Test]
    [Arguments(5.0f)]
    [Arguments(0.9f)]
    [Arguments(2147483648.0f)] // 2^31: in [2^31, 2^32)
    [Arguments(3000000000.0f)] // ~3e9: also in [2^31, 2^32)
    public async Task ToU32_MatchesHost(float x) =>
        await Assert.That(SoftFloat.ToU32(Bits(x))).IsEqualTo((uint)x);

    [Test]
    [Arguments(0)]
    [Arguments(5)]
    [Arguments(-5)]
    [Arguments(1000000)]
    [Arguments(-1000000)]
    [Arguments(16777217)] // 2^24 + 1: not exactly representable -> tests rounding
    public async Task FromI32_MatchesHost(int x) =>
        await Assert.That(SoftFloat.FromI32(x)).IsEqualTo(Bits((float)x));

    [Test]
    [Arguments(0u)]
    [Arguments(5u)]
    [Arguments(4000000000u)]
    public async Task FromU32_MatchesHost(uint x) =>
        await Assert.That(SoftFloat.FromU32(x)).IsEqualTo(Bits((float)x));

    // ---- f64 arithmetic -----------------------------------------------------------------------

    [Test]
    [Arguments(1.5, 2.0)]
    [Arguments(0.1, 0.2)]
    [Arguments(-2.5, 0.75)]
    [Arguments(1.0, -1.0)]
    [Arguments(100.0, 0.5)]
    public async Task AddF64_MatchesHost(double x, double y) =>
        await Assert.That(SoftFloat.AddF64(Bits(x), Bits(y))).IsEqualTo(Bits(x + y));

    [Test]
    [Arguments(3.5, 1.25)]
    [Arguments(1.0, 4.0)]
    public async Task SubF64_MatchesHost(double x, double y) =>
        await Assert.That(SoftFloat.SubF64(Bits(x), Bits(y))).IsEqualTo(Bits(x - y));

    [Test]
    [Arguments(2.0, 3.0)]
    [Arguments(0.1, 0.1)]
    [Arguments(-1.5, 4.0)]
    public async Task MulF64_MatchesHost(double x, double y) =>
        await Assert.That(SoftFloat.MulF64(Bits(x), Bits(y))).IsEqualTo(Bits(x * y));

    [Test]
    [Arguments(7.0, 2.0)]
    [Arguments(1.0, 3.0)]
    [Arguments(-9.0, 4.0)]
    public async Task DivF64_MatchesHost(double x, double y) =>
        await Assert.That(SoftFloat.DivF64(Bits(x), Bits(y))).IsEqualTo(Bits(x / y));

    [Test]
    [Arguments(1.5, 2.0)]
    [Arguments(2.0, 1.5)]
    [Arguments(2.0, 2.0)]
    [Arguments(-1.0, -2.0)]
    [Arguments(0.1, 0.1)]
    [Arguments(-0.0, 0.0)]
    public async Task F64Comparisons_MatchHost(double x, double y)
    {
        ulong a = Bits(x);
        ulong b = Bits(y);
        await Assert.That(SoftFloat.LtF64(a, b)).IsEqualTo(x < y);
        await Assert.That(SoftFloat.LeF64(a, b)).IsEqualTo(x <= y);
        await Assert.That(SoftFloat.GtF64(a, b)).IsEqualTo(x > y);
        await Assert.That(SoftFloat.GeF64(a, b)).IsEqualTo(x >= y);
        await Assert.That(SoftFloat.EqF64(a, b)).IsEqualTo(x == y);
        await Assert.That(SoftFloat.NeF64(a, b)).IsEqualTo(x != y);
    }

    // ---- f64 <-> int64 conversions --------------------------------------------------------------

    [Test]
    [Arguments(3.7, 3L)]
    [Arguments(-3.7, -3L)]
    [Arguments(1000.25, 1000L)]
    [Arguments(-9223372036854775808.0, long.MinValue)] // long.MinValue exactly: saturation edge
    [Arguments(1e30, long.MaxValue)]
    [Arguments(-1e30, long.MinValue)]
    public async Task ToI64_MatchesHost(double x, long expected) =>
        await Assert.That(SoftFloat.ToI64(Bits(x))).IsEqualTo(expected);

    [Test]
    [Arguments(5.0)]
    [Arguments(0.9)]
    [Arguments(9223372036854775808.0)] // 2^63: in [2^63, 2^64)
    public async Task ToU64_MatchesHost(double x) =>
        await Assert.That(SoftFloat.ToU64(Bits(x))).IsEqualTo((ulong)x);

    [Test]
    [Arguments(0L)]
    [Arguments(5L)]
    [Arguments(-5L)]
    [Arguments(1000000L)]
    [Arguments(9007199254740993L)] // 2^53 + 1: not exactly representable -> tests rounding
    public async Task FromI64_MatchesHost(long x) =>
        await Assert.That(SoftFloat.FromI64(x)).IsEqualTo(Bits((double)x));

    // ---- f32 <-> f64 widen/narrow ---------------------------------------------------------------

    [Test]
    [Arguments(1.5f)]
    [Arguments(-2.5f)]
    [Arguments(0.1f)]
    [Arguments(0.0f)]
    public async Task ToF64_MatchesHost(float x) =>
        await Assert.That(SoftFloat.ToF64(Bits(x))).IsEqualTo(Bits((double)x));

    [Test]
    [Arguments(1.5)]
    [Arguments(-2.5)]
    [Arguments(0.1)] // narrows with rounding
    [Arguments(0.0)]
    public async Task ToF32_MatchesHost(double x) =>
        await Assert.That(SoftFloat.ToF32(Bits(x))).IsEqualTo(Bits((float)x));
}
