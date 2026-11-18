use super::{Host, execute_mode, host};
use koh_objects::*;
use rstest::rstest;

#[rstest]
#[case(2, false)]
#[case(2, true)]
#[case(257, false)]
#[case(257, true)]
fn stop_uses_physical_bank_and_window_address(
    host: &Host,
    #[case] source: i32,
    #[case] color: bool,
) {
    let section = |name: &str, kind, bank, address, data: Vec<u8>| Section {
        name: name.into(),
        kind,
        bank: Some(bank),
        address: Some(address),
        size: data.len() as u32,
        data,
        ..Default::default()
    };
    let mut header = vec![0; 0x50];
    header[..4].copy_from_slice(&[0, 0xc3, 0x50, 1]);
    header[0x47] = 0x19;
    let object = Object {
        sections: vec![
            section(".header", 0, 0, 0x100, header),
            section(
                ".entry",
                0,
                0,
                0x150,
                vec![
                    0xf3,
                    0x31,
                    0,
                    0xd0,
                    0x3e,
                    source as u8,
                    0xea,
                    0,
                    0x20,
                    0x3e,
                    (source >> 8) as u8,
                    0xea,
                    0,
                    0x30,
                    0xc3,
                    0,
                    0x40,
                ],
            ),
            section(
                ".return",
                0,
                0,
                0x180,
                vec![0x3e, 1, 0xea, 0, 0x20, 0xaf, 0xea, 0, 0x30, 0xc3, 0, 0x40],
            ),
            section(".stop", 1, 1, 0x4000, vec![0x76]),
            section(
                ".work",
                1,
                source,
                0x4000,
                vec![
                    0x21, 8, 0xc0, 0x06, 8, 0xaf, 0x22, 0x05, 0x20, 0xfc, 0x3e, 42, 0xea, 8, 0xc0,
                    0xc3, 0x80, 1,
                ],
            ),
        ],
        symbols: vec![Symbol {
            name: "stop".into(),
            binding: SymbolBinding::Exported,
            definition: SymbolDefinition::Label {
                section: SectionId(3),
                offset: 0,
            },
            source: None,
        }],
    };
    let object = read_elf(&write_elf(&object).unwrap()).unwrap();
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
    let stop = linked.symbols.iter().find(|s| s.name == "stop").unwrap();
    assert_eq!((stop.bank, stop.value), (Some(1), 0x4000));
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let report = execute_mode(host, directory.path(), &linked, "stop", "--native-scene");
    let fields: Vec<_> = report.split_whitespace().collect();
    assert_eq!(fields.last().unwrap(), &"2A00000000000000", "bank{source}");
    assert_eq!(fields[2], "D000", "{report}");
    assert_eq!(fields[3], "OK", "{report}");
}
