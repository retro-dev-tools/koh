use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn cil_core_contracts_execute_with_independent_register_and_packet_checks(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "CilBasics");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let method = |name| {
        (1..=image.metadata().method_defs.len() as u32)
            .map(|row| 0x06000000 | row)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap()
    };
    for name in ["Program::ArrayLoop", "Program::Len", "Program::LenInt"] {
        let body = image.method_body(method(name)).unwrap();
        assert!(
            koh_compile::cil::decode(body.il)
                .unwrap()
                .iter()
                .any(|i| i.opcode == 0x8e),
            "{name} must exercise ldlen"
        );
    }
    for (entry, values, registers) in [
        ("RegisterLoop", &[5][..], "00000500"),
        ("RegisterReadWrite", &[7, 8][..], "07000800"),
        ("StaticCall", &[3][..], "00000300"),
        ("TernaryLow", &[9][..], "02000900"),
        ("TernaryHigh", &[1][..], "06000100"),
        ("Arithmetic", &[40, -13, -14, 63][..], "00003F00"),
        ("Loops", &[5, 4, 9, 1, 1, 0][..], "00000900"),
        (
            "Conversions",
            &[44, 4464, 4464, 8972, -128, -1, 65535][..],
            "00000C00",
        ),
        ("ArrayLoop", &[5, 150][..], "00009600"),
        ("ArrayParameters", &[3, 7, 11, 42][..], "07EE030B"),
    ] {
        let entry_name = format!("Program::{entry}");
        let selected = if entry == "RegisterLoop" {
            None
        } else {
            Some(entry_name.as_str())
        };
        let mut object =
            compile::object(&resolver, selected).unwrap_or_else(|e| panic!("{entry}: {e}"));
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
        assert_eq!(
            (fields[0], fields[2], fields[3]),
            ("00", "D000", "OK"),
            "{entry}"
        );
        assert_eq!(
            fields[if color { 6 } else { 5 }],
            registers,
            "{entry} actual MMIO"
        );
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry}");
        }
        let pointer = u32::from_str_radix(fields[1], 16).unwrap();
        let bytes = values.len() * 4;
        assert!(
            (0xd000..=0xe000 - bytes as u32).contains(&pointer),
            "{entry}"
        );
        let expected: String = values
            .iter()
            .flat_map(|&v: &i32| v.to_le_bytes())
            .map(|b| format!("{b:02X}"))
            .collect();
        let start = (pointer as usize - 0xd000) * 2;
        assert_eq!(&fields[4][start..start + bytes * 2], expected, "{entry}");
    }
    let error = compile::object(&resolver, Some("Program::Unsupported"))
        .err()
        .expect("Console runtime must not silently compile");
    assert!(
        error.contains("System.Console") && error.contains("IL_"),
        "{error}"
    );
}
