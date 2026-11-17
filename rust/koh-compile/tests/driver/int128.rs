use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn int128_operators_preserve_every_result_byte(
    host: &Host,
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
    let wide = (0x12345678u128 << 64) | 0xfedcba9876543210;
    for (entry, expected) in [
        ("Carry", (0x123456789abcdef1u128 << 64)),
        ("Borrow", (0x123456789abcdef0u128 << 64) - 1),
        ("SignExtend", (-42i128) as u128),
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
        heap[..2].copy_from_slice(&16u16.to_le_bytes());
        heap[2..18].copy_from_slice(&expected.to_le_bytes());
        super::assert_heap_snapshot(fields[4], &heap, entry);
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
