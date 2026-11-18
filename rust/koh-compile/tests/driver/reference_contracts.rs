use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{
    compile,
    resolve::{Definition, Resolver},
    sm83,
};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn string_tables_and_reached_library_memory_preserve_values(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FrameworkContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, fault) in [
        ("ReferenceRules::Memory", None),
        ("ReferenceRules::SingleCell", None),
        ("ReferenceRules::Clear", None),
        ("StringTables::Values", None),
        ("StringTables::NullElement", Some("04")),
        ("StringTables::EndIndex", Some("06")),
        ("StringTables::NegativeIndex", Some("06")),
    ] {
        let object = compile::object(&resolver, Some(entry)).unwrap();
        for (assembly, name) in [(0, "Unreached"), (1, "TryEnableDoubleSpeed")] {
            let data = resolver.assembly(assembly).unwrap().metadata();
            let row = data
                .method_defs
                .iter()
                .position(|m| data.strings.get(m.name).unwrap() == name)
                .unwrap();
            let absent = sm83::symbol(Definition {
                assembly,
                token: 0x06000001 + row as u32,
            });
            assert!(
                !object.symbols.iter().any(|s| s.name == absent),
                "{entry}: uncalled {name}"
            );
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
        let video = matches!(
            entry,
            "ReferenceRules::SingleCell" | "ReferenceRules::Clear"
        );
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            if fault.is_some() {
                "__koh_abort"
            } else {
                "__koh_exit"
            },
            if video {
                "--native-maps"
            } else {
                "--native-heap"
            },
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        if let Some(fault) = fault {
            assert_eq!(fields[0], fault, "{entry}: fault packet {:?}", &fields[..4]);
            assert_eq!(fields[3], "OK", "{entry}: reserved-memory guard");
            continue;
        }
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{entry}: {text}"
        );
        if video {
            let mut vram = vec![0xa7; 8192];
            vram[0x1800..0x1c00].fill(if entry.ends_with("Clear") { 7 } else { 0 });
            if entry.ends_with("SingleCell") {
                vram[0x1800 + 101] = 9;
            }
            assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, entry);
            if color {
                assert_heap_snapshot(fields[7], &[0xb8; 8192], "unmodified VRAM1");
            }
        } else {
            let expected = if entry.ends_with("Memory") {
                (1..=8).chain([0x7a; 8]).chain([0x1b]).collect::<Vec<u8>>()
            } else {
                vec![3, b'H', b'W', b'G', 6, 2, b'C', 3, b'Z', 1]
            };
            assert_heap_snapshot(
                &fields[4][0xf00 * 2..(0xf00 + expected.len()) * 2],
                &expected,
                entry,
            );
            assert_heap_snapshot(
                &fields[4][(0xf00 + expected.len()) * 2..],
                &vec![0x5a; 256 - expected.len()],
                "packet extent",
            );
        }
    }
}
