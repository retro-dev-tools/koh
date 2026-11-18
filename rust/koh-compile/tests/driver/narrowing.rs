use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn roslyn_byte_comparison_and_accumulation_preserve_values_and_cost(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "NarrowingContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let options = koh_link::LinkOptions {
        cgb: if color {
            koh_link::CgbMode::Compatible
        } else {
            koh_link::CgbMode::Preserve
        },
        ..Default::default()
    };
    for nops in [1, 6] {
        use koh_objects::{Object, Section, SectionId, Symbol, SymbolBinding, SymbolDefinition};
        let mut data = vec![0x31, 0, 0xD0, 0x11, 0, 0, 0x21, 42, 0, 0x3E, 0, 0xE0, 0x47];
        data.extend(vec![0; nops]);
        let stop = data.len() as u32;
        data.push(0x76);
        let object = Object {
            sections: vec![
                Section {
                    name: "entry".into(),
                    address: Some(0x100),
                    size: 3,
                    data: vec![0xC3, 0x50, 1],
                    ..Default::default()
                },
                Section {
                    name: "calibration".into(),
                    address: Some(0x150),
                    size: data.len() as u32,
                    data,
                    ..Default::default()
                },
            ],
            symbols: vec![Symbol {
                name: "__koh_exit".into(),
                binding: SymbolBinding::Exported,
                definition: SymbolDefinition::Label {
                    section: SectionId(1),
                    offset: stop,
                },
                source: None,
            }],
        };
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(&[object], &options).unwrap();
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-narrowing-cost",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..5], &["00", "0000002A", "D000", "OK", "00"]);
        assert_eq!(
            fields[5].parse::<usize>().unwrap(),
            72 + nops * 4,
            "SM83 timing calibration"
        );
    }
    let mut costs = Vec::new();
    for (entry, expected) in [
        ("Compare", 0),
        ("Accumulate", 150),
        ("WideAccumulate", 150),
        ("WrappingAccumulate", 88),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
        let bytes: usize = object
            .sections
            .iter()
            .map(|section| section.data.len())
            .sum();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(&[object], &options).unwrap();
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-narrowing-cost",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        assert_eq!(
            u8::from_str_radix(fields[4], 16).unwrap(),
            expected,
            "{entry}"
        );
        let cycles = fields[5].parse::<u64>().unwrap();
        if matches!(entry, "Accumulate" | "WideAccumulate") {
            costs.push((bytes, cycles));
        }
        println!("{configuration} {entry}: {bytes} section bytes, {cycles}T");
    }
    assert!(
        costs[0].0 < costs[1].0,
        "{configuration} narrow/wide emitted bytes: {costs:?}"
    );
    assert!(
        costs[0].1 < costs[1].1,
        "{configuration} narrow/wide cycles: {costs:?}"
    );
}
