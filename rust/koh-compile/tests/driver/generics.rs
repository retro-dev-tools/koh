use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn generic_method_instances_execute_transitively(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "GenericMethods");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericMethods.pdb")),
    )
    .unwrap();
    let library_pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.pdb")),
    )
    .unwrap();
    for (entry, expected) in [
        ("Main", "00000082"),
        ("Wide", "0000002A"),
        ("References", "0000002A"),
        ("Storage", "0000002A"),
        ("Referenced", "00000082"),
        ("PrimitiveWidths", "0000002A"),
        ("Main", "00000082"),
    ] {
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), None, Some(&library_pdb)],
        )
        .unwrap_or_else(|error| panic!("{entry}: {error}"));
        assert!(
            object
                .symbols
                .iter()
                .all(|symbol| !symbol.name.contains("Unused")),
            "{entry} dead unsupported bodies"
        );
        let fresh = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
        let repeated = compile::object_with_debug(
            &fresh,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), None, Some(&library_pdb)],
        )
        .unwrap();
        assert_eq!(
            koh_objects::write_elf(&object).unwrap(),
            koh_objects::write_elf(&repeated).unwrap(),
            "{entry} resolver history"
        );
        assert!(
            object
                .symbols
                .iter()
                .any(|symbol| symbol.name.contains("fb0000")
                    && symbol
                        .source
                        .as_ref()
                        .is_some_and(|source| source.file.ends_with(if entry == "Referenced" {
                            "Library.cs"
                        } else {
                            "Program.cs"
                        }) && source.line > 0)),
            "{entry} instance source"
        );
        assert!(
            object
                .sections
                .iter()
                .any(|section| section.name.contains("mfb0000")
                    && section.lines.iter().any(|line| line.file.ends_with(
                        if entry == "Referenced" {
                            "Library.cs"
                        } else {
                            "Program.cs"
                        }
                    ) && line.line > 0)),
            "{entry} instance machine source ranges"
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", expected, "D000", "OK"], "{entry}");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} bank2");
        }
    }
    for (entry, expected) in [
        ("Constraint", "Generic constraints"),
        (
            "Expanding",
            "Managed references require primitive/pointer scalar storage",
        ),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(expected) && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
    let missing = Resolver::new(&[&images[0], &images[1]]).unwrap();
    compile::object(&missing, Some("Program::Main")).unwrap();
    let error = compile::object(&missing, Some("Program::Referenced")).unwrap_err();
    assert!(
        error.contains("GenericLibrary") && error.contains("IL_"),
        "{error}"
    );
}
