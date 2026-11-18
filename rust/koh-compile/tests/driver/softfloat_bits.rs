use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn referenced_softfloat_integer_body_matches_independent_ieee_results(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "SoftFloatBits");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, left, right) in [
        ("Main", 0x3fc00000, 0x40200000),
        ("Rounding", 0x3dcccccd, 0x3e4ccccd),
        ("Opposite", 0xc0200000, 0x3f400000),
        ("Cancellation", 0x3f800000, 0xbf800000),
        ("ExponentGap", 0x42c80000, 0x3f000000),
        ("Sticky", 0x49742400, 0x3e800000),
        ("Subnormal", 1, 1),
        ("Transition", 0x007fffff, 1),
        ("TinyCancellation", 0x00800000, 0x807fffff),
        ("NegativeTiny", 0x80000001, 0x80000001),
        ("NegativeZero", 0x80000000, 0x80000000),
        ("OppositeZeros", 0x80000000, 0),
        ("Infinity", 0x7f800000, 0x3f800000),
        ("OppositeInfinities", 0x7f800000, 0xff800000),
        ("QuietNan", 0x7fc12345, 0x3f800000),
        ("SignalingNan", 0x7fa12345, 0x3f800000),
        ("Overflow", 0x7f7fffff, 0x7f7fffff),
        ("HalfEvenLow", 0x3f800000, 0x33800000),
        ("HalfEvenHigh", 0x3f800001, 0x33800000),
        ("HalfBelowOne", 0x3f800000, 0xb3000000),
    ] {
        let name = format!("Program::{entry}");
        let selected = compile::entry(&resolver, Some(&name)).unwrap();
        let functions = compile::reachable(&resolver, selected).unwrap();
        assert!(
            functions.iter().any(|function| {
                function.identity.assembly == 1
                    && resolver
                        .assembly(1)
                        .unwrap()
                        .method_name(function.identity.token)
                        .unwrap()
                        .ends_with("SoftFloat::AddF32")
            }),
            "actual referenced AddF32 body must be included"
        );
        let mut object = compile::object(&resolver, Some(&name)).unwrap();
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-registers",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        let expected = (f32::from_bits(left) + f32::from_bits(right)).to_bits();
        assert_eq!(
            (fields[0], fields[2], fields[3]),
            ("00", "D000", "OK"),
            "{entry}"
        );
        let actual = u32::from_str_radix(fields[1], 16).unwrap();
        if f32::from_bits(expected).is_nan() {
            assert!(
                f32::from_bits(actual).is_nan() && actual & 0x00400000 != 0,
                "{entry}"
            );
        } else {
            assert_eq!(actual, expected, "{entry}");
        }
        assert_eq!(fields[4], "5A".repeat(4096), "{entry} untouched heap");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} untouched bank2");
        }
        assert_eq!(
            fields[if color { 6 } else { 5 }],
            "EE010000",
            "{entry} actual MMIO"
        );
    }
}
