use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn struct_results_preserve_ordered_fields_and_frame_independence(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "StructReturns");
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/StructReturns.pdb")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, expected) in [
        (
            "Lines",
            &[10, 20, 30, 40, 100, 40, 30, 20, 10, 10, 10, 99, 10][..],
        ),
        ("Factories", &[100, -50, 110, 10, 100, -50, 15, 30][..]),
        ("Recursion", &[0, 1, 1, 1, 55, 89, 89, 144][..]),
        ("Generic", &[200, 1, 40000, 2][..]),
        ("Enumeration", &[0, 10, 20, 30, 60, 4][..]),
    ] {
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), None],
        )
        .unwrap_or_else(|e| panic!("{entry}: {e}"));
        assert!(object.symbols.iter().any(|symbol| {
            symbol
                .source
                .as_ref()
                .is_some_and(|source| source.file.ends_with("Program.cs") && source.line > 0)
        }));
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
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(
            (fields[0], fields[2], fields[3]),
            ("00", "D000", "OK"),
            "{entry}"
        );
        let pointer = u32::from_str_radix(fields[1], 16).unwrap();
        let bytes = expected.len() * 4;
        assert!(
            (0xd000..=0xe000 - bytes as u32).contains(&pointer),
            "{entry}"
        );
        let start = (pointer as usize - 0xd000) * 2;
        let expected: String = expected
            .iter()
            .flat_map(|&value: &i32| value.to_le_bytes())
            .map(|byte| format!("{byte:02X}"))
            .collect();
        assert_eq!(
            &fields[4][start..start + bytes * 2],
            expected,
            "{entry} {configuration}"
        );
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry}");
        }
    }
}
