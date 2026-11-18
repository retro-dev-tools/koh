use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn recursive_cil_preserves_live_values_mutual_calls_and_depth_500(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Recursion");
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/Recursion.pdb")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, value, registers) in [
        ("Fibonacci", "00000037", "00370000"),
        ("Mutual", "0000000A", "00010000"),
        ("Deep", "00000042", "00420000"),
        ("Signed", "FFFFFFBF", "00000000"),
        ("Word", "0000D3A2", "00000000"),
        ("Integer", "8001E932", "00000000"),
        ("WideLow", "0001E932", "00000000"),
        ("WideHigh", "80000000", "00000000"),
    ] {
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), None],
        )
        .unwrap();
        let mut bank = 1;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
                section.bank = Some(bank);
                bank += 1;
            }
        }
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let sum_bank = if entry == "Deep" {
            let image = resolver.assembly(0).unwrap();
            let token = (1..=image.metadata().method_defs.len() as u32)
                .map(|row| 0x06000000 | row)
                .find(|&token| image.method_name(token).unwrap() == "Program::Sum")
                .unwrap();
            let name = format!(
                ".text.{}",
                koh_compile::sm83::symbol(koh_compile::resolve::Definition { assembly: 0, token })
            );
            let section = object.sections.iter().find(|s| s.name == name).unwrap();
            assert!(
                section
                    .lines
                    .iter()
                    .any(|line| line.file.ends_with("Program.cs")
                        && line.line > 0
                        && line.count > 0)
            );
            section.bank.map(|bank| u32::try_from(bank).unwrap())
        } else {
            None
        };
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
        if let Some(bank) = sum_bank {
            assert!(linked.lines.iter().any(|line| line.bank == bank
                && line.file.ends_with("Program.cs")
                && line.line > 0));
            assert!(linked.dwarf().unwrap().starts_with(b"\x7fELF"));
        }
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
