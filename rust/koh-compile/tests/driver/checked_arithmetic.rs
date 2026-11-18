use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn checked_integer_arithmetic_preserves_results_or_fault_before_writes(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "CheckedArithmetic");
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/CheckedArithmetic.pdb")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let mut entries = Vec::new();
    for (prefix, bits, signed) in [
        ("I32", 32, true),
        ("U32", 32, false),
        ("I64", 64, true),
        ("U64", 64, false),
        ("IN", 16, true),
        ("UN", 16, false),
    ] {
        let max = (1i128 << (bits - i32::from(signed))) - 1;
        let min = if signed { -(1i128 << (bits - 1)) } else { 0 };
        let pairs = if signed {
            vec![
                (0, 0),
                (1, 2),
                (max, 0),
                (min, 0),
                (max, 1),
                (min, -1),
                (max, -1),
                (min, 1),
                (-1, -1),
                (0, min),
                (0, max),
                (max, max),
                (min, min),
                (min, max),
                (max, min),
            ]
        } else {
            vec![
                (0, 0),
                (1, 2),
                (max, 0),
                (0, max),
                (max, 1),
                (max, max),
                (max - 1, 1),
                (max - 1, 2),
                (0, 1),
                (1, 0),
            ]
        };
        for op in ["Add", "Sub", "Mul"] {
            let mut pairs = pairs.clone();
            if op == "Mul" {
                let root = (max as u128).isqrt() as i128;
                pairs.extend(if signed {
                    vec![
                        (root, root),
                        (root + 1, root + 1),
                        (-root, root),
                        (-(root + 1), root + 1),
                    ]
                } else {
                    vec![(root, root), (root + 1, root + 1), (max, 2), (max >> 1, 2)]
                });
                if prefix == "I64" {
                    pairs.extend([
                        (1i128 << 32, (1i128 << 31) - 1),
                        (-(1i128 << 32), 1i128 << 31),
                        (-(1i128 << 32), (1i128 << 31) + 1),
                    ]);
                }
                if prefix == "U64" {
                    pairs.extend([(1i128 << 32, (1i128 << 32) - 1), (max - 1, 0x1ffffffff)]);
                }
            }
            for (i, &(left, right)) in pairs.iter().enumerate() {
                let expected = if op == "Mul" && !signed {
                    let product = (left as u128) * (right as u128);
                    (product <= max as u128).then_some(product as u64)
                } else {
                    let value = match op {
                        "Add" => left + right,
                        "Sub" => left - right,
                        "Mul" => left * right,
                        _ => unreachable!(),
                    };
                    (min..=max).contains(&value).then_some(value as u64)
                };
                entries.push((format!("{prefix}{op}{i}"), expected));
            }
        }
    }
    entries.extend([
        ("Discard".into(), None),
        ("Loop".into(), Some(190)),
        ("LoopOverflow".into(), None),
    ]);
    entries.extend([
        ("Byte".into(), Some(255)),
        ("ByteOverflow".into(), None),
        ("SignedByte".into(), Some((-128i64) as u64)),
        ("SignedByteOverflow".into(), None),
        ("UShort".into(), Some(65535)),
        ("UShortOverflow".into(), None),
        ("SignedShort".into(), Some((-32768i64) as u64)),
        ("SignedShortOverflow".into(), None),
    ]);
    entries.extend([
        ("MulByte".into(), Some(255)),
        ("MulByteOverflow".into(), None),
        ("MulSignedByte".into(), Some((-128i64) as u64)),
        ("MulSignedByteOverflow".into(), None),
        ("MulShort".into(), Some(65535)),
        ("MulShortOverflow".into(), None),
        ("MulSignedShort".into(), Some((-32768i64) as u64)),
        ("MulSignedShortOverflow".into(), None),
        ("MulDiscard".into(), None),
        ("MulLoop".into(), Some(256)),
        ("MulLoopOverflow".into(), None),
    ]);
    assert_eq!(entries.len(), 276);
    let mut largest_function = 0;
    for (entry, expected) in entries {
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), None],
        )
        .unwrap_or_else(|e| panic!("{entry}: {e}"));
        largest_function = largest_function.max(
            object
                .sections
                .iter()
                .filter(|s| s.name.starts_with(".text.__koh_a"))
                .map(|s| s.data.len())
                .max()
                .unwrap(),
        );
        assert!(
            object
                .sections
                .iter()
                .flat_map(|s| &s.lines)
                .any(|line| line.file.ends_with("Program.cs") && line.line > 0)
        );
        let mut bank = 1;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
                section.bank = Some(bank);
                bank += 1;
            }
        }
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Compatible
                } else {
                    koh_link::CgbMode::Preserve
                },
                ..Default::default()
            },
        )
        .unwrap();
        assert!(linked.dwarf().unwrap().starts_with(b"\x7fELF"));
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            if expected.is_some() {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(
            fields[0],
            if expected.is_some() { "00" } else { "02" },
            "{entry}: {report}"
        );
        assert_eq!(fields[3], "OK", "{entry}");
        let mut heap = vec![0x5a; 4096];
        if let Some(value) = expected {
            assert_eq!(&fields[1..3], &["0000002A", "D000"], "{entry}");
            heap[4088..].copy_from_slice(&value.to_le_bytes());
        }
        super::assert_heap_snapshot(fields[4], &heap, &entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], &entry);
        }
    }
    assert!(largest_function <= 0x4000);
    println!("{configuration} color={color}: largest managed function {largest_function} bytes");
}
