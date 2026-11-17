use super::{Host, support};
use rstest::{fixture, rstest};

#[fixture]
#[once]
fn runtime() -> Host {
    support::build_fixture_host("SoftFloatHost")
}

#[rstest]
#[case("f32-arithmetic", 14)]
#[case("f32-negation", 2)]
#[case("f32-comparison", 36)]
#[case("f32-integer", 22)]
#[case("f64-arithmetic", 13)]
#[case("f64-comparison", 36)]
#[case("f64-integer", 14)]
#[case("width-conversion", 8)]
#[case("f32-boundaries", 14)]
#[case("f32-sweep", 256)]
#[case::f32_mul_boundaries("f32-mul-boundaries", 26)]
#[case::f32_mul_sweep("f32-mul-sweep", 256)]
#[case::f32_div_boundaries("f32-div-boundaries", 26)]
#[case::f32_div_sweep("f32-div-sweep", 256)]
#[case::f32_conversion_boundaries("f32-conversions", 43)]
#[case::f32_conversion_sweep0("f32-conversions-sweep-0", 256)]
#[case::f32_conversion_sweep1("f32-conversions-sweep-1", 256)]
#[case::f32_conversion_sweep2("f32-conversions-sweep-2", 256)]
#[case::f32_conversion_sweep3("f32-conversions-sweep-3", 256)]
#[case::f32_wide_boundaries("f32-wide", 48)]
#[case::f32_wide_sweep0("f32-wide-sweep-0", 256)]
#[case::f32_wide_sweep1("f32-wide-sweep-1", 256)]
#[case::f32_wide_sweep2("f32-wide-sweep-2", 256)]
#[case::f32_wide_sweep3("f32-wide-sweep-3", 256)]
fn software_runtime_matches_independent_numeric_oracles(
    runtime: &Host,
    #[case] group: &str,
    #[case] count: usize,
) {
    let mut queries = Vec::new();
    let mut expected = Vec::new();
    let mut add = |method: &str, arguments: &[u64], result: u64| {
        let mut query = method.to_owned();
        for argument in arguments {
            query.push_str(&format!(":{argument:016X}"));
        }
        queries.push(query);
        expected.push(result);
    };
    match group {
        "f32-wide" => {
            for x in [
                0i64,
                5,
                -5,
                16777217,
                -16777217,
                i64::MIN,
                i64::MAX,
                0x4000004000000001,
            ] {
                add("FromI64F32", &[x as u64], u64::from((x as f32).to_bits()));
            }
            for x in [
                0u64,
                5,
                16777219,
                1 << 63,
                u64::MAX,
                0x8000007fffffffff,
                0x8000008000000000,
                0x8000008000000001,
            ] {
                add("FromU64F32", &[x], u64::from((x as f32).to_bits()));
            }
            for bits in [
                0u32, 0x80000000, 1, 0x80000001, 0x406ccccd, 0xc06ccccd, 0x7fc12345, 0xffc12345,
                0x7f800000, 0xff800000, 0x5f000000, 0xdf000000, 0x5f7fffff, 0x5effffff, 0x5f800000,
                0xdf000001,
            ] {
                let x = f32::from_bits(bits);
                add("ToI64F32", &[u64::from(bits)], x as i64 as u64);
                add("ToU64F32", &[u64::from(bits)], x as u64);
            }
        }
        group if group.starts_with("f32-wide-sweep-") => {
            let start = group.rsplit('-').next().unwrap().parse::<u32>().unwrap() * 64;
            let mut seed = 0x91e10da5c79e7b1du64 ^ u64::from(start);
            for exponent in start..start + 64 {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                let bits = (seed as u32 & 0x807fffff) | (exponent << 23);
                let x = f32::from_bits(bits);
                add(
                    "FromI64F32",
                    &[seed],
                    u64::from((seed as i64 as f32).to_bits()),
                );
                add("FromU64F32", &[seed], u64::from((seed as f32).to_bits()));
                add("ToI64F32", &[u64::from(bits)], x as i64 as u64);
                add("ToU64F32", &[u64::from(bits)], x as u64);
            }
        }
        "f32-conversions" => {
            for x in [0i32, 5, -5, 1000000, 16777217, i32::MIN, i32::MAX] {
                add(
                    "FromI32",
                    &[u64::from(x as u32)],
                    u64::from((x as f32).to_bits()),
                );
            }
            for x in [0u32, 5, 0xffffff, 0x1000001, 0x80000000, u32::MAX] {
                add("FromU32", &[u64::from(x)], u64::from((x as f32).to_bits()));
            }
            for bits in [
                0u32, 0x80000000, 1, 0x80000001, 0x7fc12345, 0xffc12345, 0x7fa12345, 0x7f800000,
                0xff800000, 0x4f000000, 0xcf000000, 0x4f7fffff, 0x4f800000, 0x406ccccd, 0xc06ccccd,
            ] {
                let x = f32::from_bits(bits);
                add("ToI32", &[u64::from(bits)], u64::from(x as i32 as u32));
                add("ToU32", &[u64::from(bits)], u64::from(x as u32));
            }
        }
        group if group.starts_with("f32-conversions-sweep-") => {
            let start = group.rsplit('-').next().unwrap().parse::<u32>().unwrap() * 64;
            let mut seed = 0x91e10da5u32 ^ start;
            for exponent in start..start + 64 {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                let bits = (seed & 0x807fffff) | (exponent << 23);
                let x = f32::from_bits(bits);
                add(
                    "FromI32",
                    &[u64::from(seed)],
                    u64::from((seed as i32 as f32).to_bits()),
                );
                add(
                    "FromU32",
                    &[u64::from(seed)],
                    u64::from((seed as f32).to_bits()),
                );
                add("ToI32", &[u64::from(bits)], u64::from(x as i32 as u32));
                add("ToU32", &[u64::from(bits)], u64::from(x as u32));
            }
        }
        "f32-div-boundaries" => {
            for (a, b) in [
                (1, 0x3f800000),
                (1, 0x40000000),
                (3, 0x40000000),
                (5, 0x40000000),
                (0x00800000, 0x40000000),
                (0x007fffff, 0x00800000),
                (0x00800000, 0x007fffff),
                (1, 1),
                (1, 0x7f7fffff),
                (0x7f7fffff, 1),
                (0x80000000, 0x3f800000),
                (0x80000000, 0xbf800000),
                (0x3f800000, 0),
                (0x3f800000, 0x80000000),
                (0, 0),
                (0x7f800000, 0x7f800000),
                (0xbf800000, 0x7f800000),
                (0x7f800000, 0xbf800000),
                (0x7fc12345, 0x3f800000),
                (0x3f800000, 0x7fa12345),
                (0x00ffffff, 0x40000000),
                (0x3f800000, 0x40400000),
                (0x40e00000, 0x40000000),
                (0xc1100000, 0x40800000),
                (0x7f7fffff, 0x3f7fffff),
                (0x007fffff, 0x3f7fffff),
            ] {
                add(
                    "DivF32",
                    &[u64::from(a), u64::from(b)],
                    u64::from((f32::from_bits(a) / f32::from_bits(b)).to_bits()),
                );
            }
        }
        "f32-div-sweep" => {
            let mut seed = 0x91e10da5u32;
            for exponent in 0..256u32 {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                let a = (seed & 0x807fffff) | (exponent << 23);
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                let b = if exponent % 4 == 0 {
                    0x3f800000 | (seed & 0x807fffff)
                } else {
                    seed
                };
                add(
                    "DivF32",
                    &[u64::from(a), u64::from(b)],
                    u64::from((f32::from_bits(a) / f32::from_bits(b)).to_bits()),
                );
            }
        }
        "f32-mul-boundaries" => {
            for (a, b) in [
                (1, 1),
                (0x007fffff, 0x3f800001),
                (0x3fffffff, 0x3f800001),
                (0x7f7ffffe, 0x3f800001),
                (1, 0x3f800000),
                (1, 0x40000000),
                (0x007fffff, 0x3f800000),
                (0x00800000, 0x3f000000),
                (0x00800000, 0x33800000),
                (0x00800000, 0x33c00000),
                (1, 0x3f000000),
                (1, 0x3fc00000),
                (3, 0x3f000000),
                (5, 0x3f000000),
                (0x80000000, 0x40000000),
                (0x80000000, 0xc0000000),
                (0, 0x7f800000),
                (0x7f800000, 0),
                (0x7f800000, 0xbf000000),
                (0x7f800000, 0xff800000),
                (0x7fc12345, 0x3f800000),
                (0x3f800000, 0x7fa12345),
                (0x7f7fffff, 0x40000000),
                (0x7f7fffff, 0x3f800000),
                (0x3f800800, 0x3f800800),
                (0x3f800800, 0x3f801800),
            ] {
                add(
                    "MulF32",
                    &[u64::from(a), u64::from(b)],
                    u64::from((f32::from_bits(a) * f32::from_bits(b)).to_bits()),
                );
            }
        }
        "f32-mul-sweep" => {
            let mut seed = 0x91e10da5u32;
            for exponent in 0..256u32 {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                let a = (seed & 0x807fffff) | (exponent << 23);
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                let b = if exponent % 4 == 0 {
                    0x3f800000 | (seed & 0x807fffff)
                } else {
                    seed
                };
                add(
                    "MulF32",
                    &[u64::from(a), u64::from(b)],
                    u64::from((f32::from_bits(a) * f32::from_bits(b)).to_bits()),
                );
            }
        }
        "f32-boundaries" => {
            for (a, b) in [
                (1, 1),
                (0x007fffff, 1),
                (0x00800000, 0x807fffff),
                (0x80000001, 0x80000001),
                (0x80000000, 0x80000000),
                (0x80000000, 0),
                (0x7f800000, 0x3f800000),
                (0x7f800000, 0xff800000),
                (0x7fc12345, 0x3f800000),
                (0x7fa12345, 0x3f800000),
                (0x7f7fffff, 0x7f7fffff),
                (0x3f800000, 0x33800000),
                (0x3f800001, 0x33800000),
                (0x3f800000, 0xb3000000),
            ] {
                add(
                    "AddF32",
                    &[u64::from(a), u64::from(b)],
                    u64::from((f32::from_bits(a) + f32::from_bits(b)).to_bits()),
                );
            }
        }
        "f32-sweep" => {
            let mut seed = 0x91e10da5u32;
            for exponent in 0..256u32 {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                let a = (seed & 0x807fffff) | (exponent << 23);
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                let b = if exponent % 4 == 0 {
                    a ^ 0x80000000
                } else {
                    seed
                };
                add(
                    "AddF32",
                    &[u64::from(a), u64::from(b)],
                    u64::from((f32::from_bits(a) + f32::from_bits(b)).to_bits()),
                );
            }
        }
        "f32-arithmetic" => {
            for (op, pairs) in [
                (
                    "AddF32",
                    &[
                        (1.5f32, 2.0),
                        (0.1, 0.2),
                        (-2.5, 0.75),
                        (1.0, -1.0),
                        (100.0, 0.5),
                        (1000000.0, 0.25),
                    ][..],
                ),
                ("SubF32", &[(3.5f32, 1.25), (1.0, 4.0)][..]),
                ("MulF32", &[(2.0f32, 3.0), (0.1, 0.1), (-1.5, 4.0)][..]),
                ("DivF32", &[(7.0f32, 2.0), (1.0, 3.0), (-9.0, 4.0)][..]),
            ] {
                for &(x, y) in pairs {
                    let result = match op {
                        "AddF32" => x + y,
                        "SubF32" => x - y,
                        "MulF32" => x * y,
                        "DivF32" => x / y,
                        _ => unreachable!(),
                    };
                    add(
                        op,
                        &[u64::from(x.to_bits()), u64::from(y.to_bits())],
                        u64::from(result.to_bits()),
                    );
                }
            }
        }
        "f32-negation" => {
            for x in [3.5f32, -3.5] {
                add(
                    "NegF32",
                    &[u64::from(x.to_bits())],
                    u64::from((-x).to_bits()),
                );
            }
        }
        "f32-comparison" => {
            for (x, y) in [
                (1.5f32, 2.0),
                (2.0, 1.5),
                (2.0, 2.0),
                (-1.0, -2.0),
                (0.1, 0.1),
                (-0.0, 0.0),
            ] {
                for (op, result) in [
                    ("LtF32", x < y),
                    ("LeF32", x <= y),
                    ("GtF32", x > y),
                    ("GeF32", x >= y),
                    ("EqF32", x == y),
                    ("NeF32", x != y),
                ] {
                    add(
                        op,
                        &[u64::from(x.to_bits()), u64::from(y.to_bits())],
                        u64::from(result),
                    );
                }
            }
        }
        "f32-integer" => {
            for x in [
                3.7f32,
                -3.7,
                5.0,
                0.9,
                -0.9,
                1000.25,
                -2147483648.0,
                1e20,
                -1e20,
            ] {
                add(
                    "ToI32",
                    &[u64::from(x.to_bits())],
                    u64::from(x as i32 as u32),
                );
            }
            for x in [5.0f32, 0.9, 2147483648.0, 3000000000.0] {
                add("ToU32", &[u64::from(x.to_bits())], u64::from(x as u32));
            }
            for x in [0i32, 5, -5, 1000000, -1000000, 16777217] {
                add(
                    "FromI32",
                    &[u64::from(x as u32)],
                    u64::from((x as f32).to_bits()),
                );
            }
            for x in [0u32, 5, 4000000000] {
                add("FromU32", &[u64::from(x)], u64::from((x as f32).to_bits()));
            }
        }
        "f64-arithmetic" => {
            for (op, pairs) in [
                (
                    "AddF64",
                    &[
                        (1.5f64, 2.0),
                        (0.1, 0.2),
                        (-2.5, 0.75),
                        (1.0, -1.0),
                        (100.0, 0.5),
                    ][..],
                ),
                ("SubF64", &[(3.5f64, 1.25), (1.0, 4.0)][..]),
                ("MulF64", &[(2.0f64, 3.0), (0.1, 0.1), (-1.5, 4.0)][..]),
                ("DivF64", &[(7.0f64, 2.0), (1.0, 3.0), (-9.0, 4.0)][..]),
            ] {
                for &(x, y) in pairs {
                    let result = match op {
                        "AddF64" => x + y,
                        "SubF64" => x - y,
                        "MulF64" => x * y,
                        "DivF64" => x / y,
                        _ => unreachable!(),
                    };
                    add(op, &[x.to_bits(), y.to_bits()], result.to_bits());
                }
            }
        }
        "f64-comparison" => {
            for (x, y) in [
                (1.5f64, 2.0),
                (2.0, 1.5),
                (2.0, 2.0),
                (-1.0, -2.0),
                (0.1, 0.1),
                (-0.0, 0.0),
            ] {
                for (op, result) in [
                    ("LtF64", x < y),
                    ("LeF64", x <= y),
                    ("GtF64", x > y),
                    ("GeF64", x >= y),
                    ("EqF64", x == y),
                    ("NeF64", x != y),
                ] {
                    add(op, &[x.to_bits(), y.to_bits()], u64::from(result));
                }
            }
        }
        "f64-integer" => {
            for x in [3.7f64, -3.7, 1000.25, -9223372036854775808.0, 1e30, -1e30] {
                add("ToI64", &[x.to_bits()], x as i64 as u64);
            }
            for x in [5.0f64, 0.9, 9223372036854775808.0] {
                add("ToU64", &[x.to_bits()], x as u64);
            }
            for x in [0i64, 5, -5, 1000000, 9007199254740993] {
                add("FromI64", &[x as u64], (x as f64).to_bits());
            }
        }
        "width-conversion" => {
            for x in [1.5f32, -2.5, 0.1, 0.0] {
                add("ToF64", &[u64::from(x.to_bits())], (x as f64).to_bits());
            }
            for x in [1.5f64, -2.5, 0.1, 0.0] {
                add("ToF32", &[x.to_bits()], u64::from((x as f32).to_bits()));
            }
        }
        _ => unreachable!(),
    }
    assert_eq!(queries.len(), count);
    let output = support::dotnet()
        .arg(&runtime.dll)
        .args(&queries)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    let results: Vec<_> = stdout
        .lines()
        .map(|line| u64::from_str_radix(line, 16).unwrap())
        .collect();
    assert_eq!(results.len(), expected.len());
    for ((query, result), expected) in queries.iter().zip(results).zip(expected) {
        if matches!(
            group,
            "f32-boundaries"
                | "f32-sweep"
                | "f32-mul-boundaries"
                | "f32-mul-sweep"
                | "f32-div-boundaries"
                | "f32-div-sweep"
        ) && f32::from_bits(expected as u32).is_nan()
        {
            assert!(
                f32::from_bits(result as u32).is_nan() && result & 0x00400000 != 0,
                "{query}"
            );
        } else {
            assert_eq!(result, expected, "{query}");
        }
    }
}
