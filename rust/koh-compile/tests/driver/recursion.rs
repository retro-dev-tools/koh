use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn recursive_cil_preserves_live_values_and_mutual_results(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Recursion");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, value, registers) in [
        ("Fibonacci", "00000037", "00370000"),
        ("Mutual", "0000000A", "00010000"),
    ] {
        let mut object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
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
            (fields[0], fields[1], fields[2], fields[3]),
            ("00", value, "D000", "OK"),
            "{entry}"
        );
        assert_eq!(fields[4], "5A".repeat(4096), "{entry} untouched heap");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} untouched bank2");
        }
        assert_eq!(fields[if color { 6 } else { 5 }], registers, "{entry} MMIO");
    }
}
