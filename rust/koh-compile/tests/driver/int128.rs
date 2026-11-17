use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest::fixture]
#[once]
fn managed() -> Host {
    super::support::build_fixture_host("ManagedHost")
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn int128_operators_preserve_every_result_byte(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "WideIntegers");
    let references = std::fs::read_to_string(
        directory
            .path()
            .join(format!("obj/{configuration}/net10.0/reference-paths.txt")),
    )
    .unwrap();
    let runtime = references
        .lines()
        .map(std::path::Path::new)
        .find(|path| {
            path.file_name()
                .is_some_and(|name| name == "System.Runtime.dll")
        })
        .expect("Roslyn System.Runtime reference");
    let runtime = std::fs::read(runtime).unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &runtime]).unwrap();
    let missing = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let error = compile::object(&missing, Some("Program::Carry"))
        .err()
        .expect("Missing numeric reference must diagnose");
    assert!(
        error.contains("System.Runtime") && error.contains("IL_"),
        "{error}"
    );
    let wide = (0x12345678u128 << 64) | 0xfedcba9876543210;
    for (entry, expected) in [
        ("Carry", (0x123456789abcdef1u128 << 64)),
        ("Borrow", (0x123456789abcdef0u128 << 64) - 1),
        ("SignExtend", (-42i128) as u128),
        ("NegativeOne", u128::MAX),
        ("PositiveExtend", 0x0102030405060708),
        ("Multiply", wide.wrapping_mul(1000)),
        ("Divide", wide / 1000),
        ("Remainder", wide % 1000),
        ("SignedDivide", (-(wide as i128) / 1000) as u128),
        ("SignedRemainder", (-(wide as i128) % 1000) as u128),
        ("Complement", !wide),
        ("ShiftMask", wide),
        ("HighShift", wide >> 64),
        ("SignedShift", u128::MAX),
        ("Compare", 119),
        ("Recursive", (0x123456789abcdef1u128 << 64) | 2),
        ("StaticField", wide),
        ("ByReference", wide + 42),
        ("Aggregate", wide + 5),
        ("Array", wide + 42),
        ("HugeDivide", u128::MAX / ((1u128 << 127) | 1)),
        ("HugeRemainder", u128::MAX % ((1u128 << 127) | 1)),
        ("SignedMinimum", 1u128 << 127),
        ("LogicalSignedShift", 1),
        ("NarrowSigned", (wide as i64 as i128) as u128),
        (
            "MixedBits",
            (wide & ((0x76543210u128 << 64) | 0x123456789abcdef0))
                ^ ((0xabcdef01u128 << 64) | 0xabcdef0123456789),
        ),
        (
            "LegacyRoundTrip",
            123456789012345678u128 + 987654321098765432,
        ),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|e| panic!("{configuration} {entry}: {e}"));
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{entry}: {report}"
        );
        let mut heap = [0x5a; 4096];
        let offset = if entry == "Array" {
            heap[..2].copy_from_slice(&3u16.to_le_bytes());
            heap[2..18].copy_from_slice(
                &((0xabcdef0123456789u128 << 64) | 0x76543210fedcba98).to_le_bytes(),
            );
            heap[18..34].copy_from_slice(&(wide + 42).to_le_bytes());
            heap[34..50].fill(0);
            50
        } else {
            0
        };
        heap[offset..offset + 2].copy_from_slice(&16u16.to_le_bytes());
        heap[offset + 2..offset + 18].copy_from_slice(&expected.to_le_bytes());
        super::assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    let assembly = directory
        .path()
        .join(format!("bin/{configuration}/net10.0/WideIntegers.dll"));
    for (entry, expectation) in [
        ("Oracle", "C6A729F0AA756B0F0000000000000000"),
        ("ZeroDivisor", "THROW:System.DivideByZeroException"),
        ("SignedOverflow", "THROW:System.OverflowException"),
        ("RemainderOverflow", "THROW:System.OverflowException"),
    ] {
        let result = super::support::dotnet()
            .arg(&managed.dll)
            .arg(&assembly)
            .arg(entry)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{entry}: {}",
            String::from_utf8_lossy(&result.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&result.stdout).trim(),
            expectation,
            "{entry}"
        );
        if entry == "Oracle" {
            continue;
        }
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_abort",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields[0], "02", "{entry}: {report}");
        assert_eq!(fields[3], "OK", "{entry}: {report}");
        super::assert_heap_snapshot(fields[4], &[0x5a; 4096], entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for (entry, message) in [
        (
            "FloatingConversion",
            "128-bit floating-point conversion is unsupported",
        ),
        (
            "CheckedAddition",
            "Unsupported 128-bit member: op_CheckedAddition",
        ),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .err()
            .expect("Unsupported 128-bit operation must diagnose");
        assert!(
            error.contains(message) && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
}
