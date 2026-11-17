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
#[case::f64_add_boundaries("f64-add-boundaries", 14)]
#[case::f64_add_sweep0("f64-add-sweep-0", 256)]
#[case::f64_add_sweep1("f64-add-sweep-1", 256)]
#[case::f64_add_sweep2("f64-add-sweep-2", 256)]
#[case::f64_add_sweep3("f64-add-sweep-3", 256)]
#[case::f64_add_sweep4("f64-add-sweep-4", 256)]
#[case::f64_add_sweep5("f64-add-sweep-5", 256)]
#[case::f64_add_sweep6("f64-add-sweep-6", 256)]
#[case::f64_add_sweep7("f64-add-sweep-7", 256)]
#[case::f64_div_boundaries("f64-div-boundaries", 24)]
#[case::f64_div_sweep0("f64-div-sweep-0", 256)]
#[case::f64_div_sweep1("f64-div-sweep-1", 256)]
#[case::f64_div_sweep2("f64-div-sweep-2", 256)]
#[case::f64_div_sweep3("f64-div-sweep-3", 256)]
#[case::f64_div_sweep4("f64-div-sweep-4", 256)]
#[case::f64_div_sweep5("f64-div-sweep-5", 256)]
#[case::f64_div_sweep6("f64-div-sweep-6", 256)]
#[case::f64_div_sweep7("f64-div-sweep-7", 256)]
#[case::f64_mul_boundaries("f64-mul-boundaries", 23)]
#[case::f64_mul_sweep0("f64-mul-sweep-0", 256)]
#[case::f64_mul_sweep1("f64-mul-sweep-1", 256)]
#[case::f64_mul_sweep2("f64-mul-sweep-2", 256)]
#[case::f64_mul_sweep3("f64-mul-sweep-3", 256)]
#[case::f64_mul_sweep4("f64-mul-sweep-4", 256)]
#[case::f64_mul_sweep5("f64-mul-sweep-5", 256)]
#[case::f64_mul_sweep6("f64-mul-sweep-6", 256)]
#[case::f64_mul_sweep7("f64-mul-sweep-7", 256)]
#[case::widen_boundaries("width-widen-boundaries", 12)]
#[case::widen_sweep("width-widen-sweep", 256)]
#[case::narrow_boundaries("width-narrow-boundaries", 18)]
#[case::narrow_near("width-narrow-near", 256)]
#[case::narrow_sweep0("width-narrow-sweep-0", 256)]
#[case::narrow_sweep1("width-narrow-sweep-1", 256)]
#[case::narrow_sweep2("width-narrow-sweep-2", 256)]
#[case::narrow_sweep3("width-narrow-sweep-3", 256)]
#[case::narrow_sweep4("width-narrow-sweep-4", 256)]
#[case::narrow_sweep5("width-narrow-sweep-5", 256)]
#[case::narrow_sweep6("width-narrow-sweep-6", 256)]
#[case::narrow_sweep7("width-narrow-sweep-7", 256)]
#[case::f64_from_integer_bounds("f64-from-integer-bounds", 32)]
#[case::f64_to_integer_bounds("f64-to-integer-bounds", 96)]
#[case::f64_from_integer_sweep0("f64-from-integer-sweep-0", 256)]
#[case::f64_to_integer_sweep0("f64-to-integer-sweep-0", 256)]
#[case::f64_from_integer_sweep1("f64-from-integer-sweep-1", 256)]
#[case::f64_to_integer_sweep1("f64-to-integer-sweep-1", 256)]
#[case::f64_from_integer_sweep2("f64-from-integer-sweep-2", 256)]
#[case::f64_to_integer_sweep2("f64-to-integer-sweep-2", 256)]
#[case::f64_from_integer_sweep3("f64-from-integer-sweep-3", 256)]
#[case::f64_to_integer_sweep3("f64-to-integer-sweep-3", 256)]
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
        "f64-from-integer-bounds" => {
            for bits in [
                0u64,
                1,
                5,
                u64::MAX,
                0x8000000000000000,
                0x7fffffffffffffff,
                9007199254740991,
                9007199254740992,
                9007199254740993,
                9007199254740995,
                0x8000000000000400,
                0x8000000000000401,
                0xfffffffffffffc00,
                0xfffffffffffffbff,
                0xfffffffffffffc01,
                1000000,
            ] {
                add("FromI64", &[bits], ((bits as i64) as f64).to_bits());
                add("FromU64", &[bits], (bits as f64).to_bits());
            }
        }
        group if group.starts_with("f64-from-integer-sweep-") => {
            let batch = group.rsplit('-').next().unwrap().parse::<u64>().unwrap();
            let mut seed = 0xe54b71da9c203f81u64 ^ batch;
            for _ in 0..128 {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                add("FromI64", &[seed], ((seed as i64) as f64).to_bits());
                add("FromU64", &[seed], (seed as f64).to_bits());
            }
        }
        group if group.starts_with("f64-to-integer-") => {
            let values: Vec<u64> = if group == "f64-to-integer-bounds" {
                vec![
                    0,
                    0x8000000000000000,
                    3.7f64.to_bits(),
                    (-3.7f64).to_bits(),
                    1000.25f64.to_bits(),
                    2147483647f64.to_bits(),
                    2147483648f64.to_bits(),
                    (-2147483648f64).to_bits(),
                    (-2147483649f64).to_bits(),
                    4294967295f64.to_bits(),
                    4294967296f64.to_bits(),
                    0x43e0000000000000,
                    0x43dfffffffffffff,
                    0xc3e0000000000000,
                    0xc3e0000000000001,
                    0x43f0000000000000,
                    0x43efffffffffffff,
                    (-0.9f64).to_bits(),
                    0.9f64.to_bits(),
                    0x7ff0000000000000,
                    0xfff0000000000000,
                    0x7ff8123456789abc,
                    0xfff8123456789abc,
                    0x7ff0123456789abc,
                ]
            } else {
                let batch = group.rsplit('-').next().unwrap().parse::<u64>().unwrap();
                let mut seed = 0x27e8f3a96cb1504du64 ^ batch;
                (0..64)
                    .map(|i| {
                        seed ^= seed << 13;
                        seed ^= seed >> 7;
                        seed ^= seed << 17;
                        let exponent = 1022 + (i + batch * 17) % 68;
                        (seed & 0x800fffffffffffff) | (exponent << 52)
                    })
                    .collect()
            };
            for bits in values {
                let value = f64::from_bits(bits);
                add("ToI64", &[bits], value as i64 as u64);
                add("ToU64", &[bits], value as u64);
                add("ToI32F64", &[bits], u64::from(value as i32 as u32));
                add("ToU32F64", &[bits], u64::from(value as u32));
            }
        }
        "width-widen-boundaries" => {
            for bits in [
                0u32, 0x80000000, 1, 0x7fffff, 0x800000, 0x3fc00000, 0xc0200000, 0x3dcccccd,
                0x7f7fffff, 0x7f800000, 0x7f812345, 0x7fc12345,
            ] {
                add(
                    "ToF64",
                    &[u64::from(bits)],
                    f64::from(f32::from_bits(bits)).to_bits(),
                );
            }
        }
        "width-widen-sweep" => {
            let mut seed = 0x5a713cd9u32;
            for exponent in 0..256 {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                let bits = (seed & 0x807fffff) | (exponent << 23);
                add(
                    "ToF64",
                    &[u64::from(bits)],
                    f64::from(f32::from_bits(bits)).to_bits(),
                );
            }
        }
        "width-narrow-boundaries" => {
            for bits in [
                0x3ff8000000000000u64,
                0xc004000000000000,
                0x3fb999999999999a,
                0x36a0000000000000,
                0x3690000000000000,
                0x3690000000000001,
                0x36a8000000000000,
                0x380fffffe0000000,
                0x3ff0000010000000,
                0x3ff0000010000001,
                0x3ff0000030000000,
                0x47effffff0000000,
                0x8000000000000000,
                0x7ff0000000000000,
                1,
                0x7fefffffffffffff,
                0xfff0000000000000,
                0x7ff0123456789abc,
            ] {
                add(
                    "ToF32",
                    &[bits],
                    u64::from((f64::from_bits(bits) as f32).to_bits()),
                );
            }
        }
        group if group.starts_with("width-narrow-") => {
            let near = group == "width-narrow-near";
            let start = if near {
                0
            } else {
                group.rsplit('-').next().unwrap().parse::<u64>().unwrap() * 256
            };
            let mut seed = 0x2a7e91d68cb50143u64 ^ start;
            for i in start..start + 256 {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                let exponent = if near { 872 + i * 280 / 255 } else { i };
                let bits = (seed & 0x800fffffffffffff) | (exponent << 52);
                add(
                    "ToF32",
                    &[bits],
                    u64::from((f64::from_bits(bits) as f32).to_bits()),
                );
            }
        }
        "f64-mul-boundaries" => {
            for (a, b) in [
                (1u64, 0x3ff0000000000000),
                (1, 0x3fe0000000000000),
                (3, 0x3fe0000000000000),
                (0x001fffffffffffff, 0x3fe0000000000000),
                (0x0010000000000000, 0x3fe0000000000000),
                (0x000fffffffffffff, 0x0010000000000000),
                (0x3fb999999999999a, 0x3fb999999999999a),
                (0x7fefffffffffffff, 0x4000000000000000),
                (0x8000000000000000, 0x4000000000000000),
                (0, 0),
                (0x7ff0000000000000, 0),
                (0, 0xfff0000000000000),
                (0x7ff0000000000000, 0xfff0000000000000),
                (0x7ff0000000000000, 0xbff0000000000000),
                (0x7ff0123456789abc, 0x3ff0000000000000),
                (0x3ff0000000000000, 0x7ff0123456789abc),
                (0x3fffffffffffffff, 0x3ff0000000000001),
                (0x3fffffffffffffff, 0x3fffffffffffffff),
                (0x3ff0000000000001, 0x3ff0000000000001),
                (0x8000000000000001, 0x3fe0000000000000),
                (0x8000000000000003, 0x3fe0000000000000),
                (0x0010000000000000, 0x3ca0000000000000),
                (0x0010000000000001, 0x3ca0000000000000),
            ] {
                add(
                    "MulF64",
                    &[a, b],
                    (f64::from_bits(a) * f64::from_bits(b)).to_bits(),
                );
            }
        }
        "f64-div-boundaries" => {
            for (a, b) in [
                (1u64, 0x3ff0000000000000),
                (1, 0x4000000000000000),
                (3, 0x4000000000000000),
                (0x001fffffffffffff, 0x4000000000000000),
                (0x0010000000000000, 0x4000000000000000),
                (0x000fffffffffffff, 0x0010000000000000),
                (0x3ff0000000000000, 0x4008000000000000),
                (0x7fefffffffffffff, 0x3fe0000000000000),
                (0x8000000000000000, 0x4000000000000000),
                (0, 0),
                (0x3ff0000000000000, 0),
                (0xbff0000000000000, 0),
                (0x7ff0000000000000, 0x7ff0000000000000),
                (0x7ff0000000000000, 0xbff0000000000000),
                (0x3ff0000000000000, 0xfff0000000000000),
                (0x7ff0123456789abc, 0x3ff0000000000000),
                (0x3ff0000000000000, 0x7ff0123456789abc),
                (0x0010000000000000, 0x4340000000000000),
                (0x0010000000000001, 0x4340000000000000),
                (0x0010000000000000, 0x4350000000000000),
                (0x8000000000000001, 0x4000000000000000),
                (0x8000000000000003, 0x4000000000000000),
                (0x3ff0000000000000, 0x3ff0000000000001),
                (0x3ff0000000000001, 0x3ff0000000000000),
            ] {
                add(
                    "DivF64",
                    &[a, b],
                    (f64::from_bits(a) / f64::from_bits(b)).to_bits(),
                );
            }
        }
        "f64-add-boundaries" => {
            for (a, b) in [
                (1u64, 1),
                (0x000fffffffffffff, 1),
                (0x0010000000000000, 0x800fffffffffffff),
                (0x8000000000000001, 0x8000000000000001),
                (0x8000000000000000, 0x8000000000000000),
                (0x8000000000000000, 0),
                (0x7ff0000000000000, 0x3ff0000000000000),
                (0x7ff0000000000000, 0xfff0000000000000),
                (0x7ff8123456789abc, 0x3ff0000000000000),
                (0x7ff0123456789abc, 0x3ff0000000000000),
                (0x7fefffffffffffff, 0x7fefffffffffffff),
                (0x3ff0000000000000, 0x3ca0000000000000),
                (0x3ff0000000000001, 0x3ca0000000000000),
                (0x3ff0000000000000, 0xbc90000000000000),
            ] {
                add(
                    "AddF64",
                    &[a, b],
                    (f64::from_bits(a) + f64::from_bits(b)).to_bits(),
                );
            }
        }
        group
            if group.starts_with("f64-add-sweep-")
                || group.starts_with("f64-div-sweep-")
                || group.starts_with("f64-mul-sweep-") =>
        {
            let start = group.rsplit('-').next().unwrap().parse::<u64>().unwrap() * 256;
            let mut seed = 0x91e10da5c79e7b1du64 ^ start;
            for exponent in start..start + 256 {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                let a = (seed & 0x800fffffffffffff) | (exponent << 52);
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                let division = group.starts_with("f64-div-");
                let multiplication = group.starts_with("f64-mul-");
                let b = if !division && !multiplication && exponent % 4 == 0 {
                    a ^ 0x8000000000000000
                } else {
                    seed
                };
                add(
                    if multiplication {
                        "MulF64"
                    } else if division {
                        "DivF64"
                    } else {
                        "AddF64"
                    },
                    &[a, b],
                    if multiplication {
                        f64::from_bits(a) * f64::from_bits(b)
                    } else if division {
                        f64::from_bits(a) / f64::from_bits(b)
                    } else {
                        f64::from_bits(a) + f64::from_bits(b)
                    }
                    .to_bits(),
                );
            }
        }
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
        if (matches!(
            group,
            "f32-boundaries"
                | "f32-sweep"
                | "f32-mul-boundaries"
                | "f32-mul-sweep"
                | "f32-div-boundaries"
                | "f32-div-sweep"
                | "width-narrow-boundaries"
                | "width-narrow-near"
        ) || group.starts_with("width-narrow-sweep-"))
            && f32::from_bits(expected as u32).is_nan()
        {
            assert!(
                f32::from_bits(result as u32).is_nan() && result & 0x00400000 != 0,
                "{query}"
            );
        } else if (group == "f64-add-boundaries"
            || group.starts_with("f64-add-sweep-")
            || group == "f64-div-boundaries"
            || group.starts_with("f64-div-sweep-")
            || group == "f64-mul-boundaries"
            || group.starts_with("f64-mul-sweep-")
            || group.starts_with("width-widen-"))
            && f64::from_bits(expected).is_nan()
        {
            assert!(
                f64::from_bits(result).is_nan() && result & 0x0008000000000000 != 0,
                "{query}"
            );
        } else {
            assert_eq!(result, expected, "{query}");
        }
    }
}
