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
        assert_eq!(result, expected, "{query}");
    }
}
