use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use koh_objects::{read_elf, write_elf};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn canonical_ir_behaviors_execute_from_real_cil(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "IrContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, expected) in [
        ("Main", &[10u16, 260, 1, 1, 2, 3, 11, 22, 33][..]),
        ("Pointers", &[0u16, 1, 0x7fff, 0x8000, 0xc000, 0xffff][..]),
    ] {
        let selected = format!("Program::{entry}");
        let object = compile::object(&resolver, Some(&selected)).unwrap();
        let first = write_elf(&object).unwrap();
        assert_eq!(
            first,
            write_elf(&compile::object(&resolver, Some(&selected)).unwrap()).unwrap()
        );
        let mut object = read_elf(&first).unwrap();
        assert_eq!(first, write_elf(&object).unwrap());
        let mut bank = 1;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
                section.bank = Some(bank);
                bank += 1;
            }
        }
        assert!(
            bank > 2,
            "{entry} must exercise multiple managed code banks"
        );
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
        assert_eq!(&fields[..4], &["00", "0000DFE0", "D000", "OK"], "{entry}");
        let packet: String = expected
            .iter()
            .flat_map(|v| v.to_le_bytes())
            .map(|v| format!("{v:02X}"))
            .collect();
        let start = 0xfe0 * 2;
        assert_eq!(&fields[4][start..start + packet.len()], packet, "{entry}");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry}");
        }
    }
}
