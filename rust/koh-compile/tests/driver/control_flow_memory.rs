use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn memory_calls_and_cartridge_boot_preserve_observable_state(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ControlFlowMemory");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, expected) in [
        ("Local", 43),
        ("Indexed", 30),
        ("Arguments", 42),
        ("WordReturn", 550),
        ("Nested", 42),
        ("Recursive", 42),
        ("Mutual", 42),
        ("CaseOne", 11),
        ("CaseTwo", 22),
        ("CaseDefault", 99),
        ("Boot", 42),
        ("Mmio", 0x37),
        ("ByteTable", 15),
        ("WordTable", 1000),
        ("RomByte", 15),
        ("RomWord", 1000),
        ("Shared", 2),
        ("DynamicByte", 30),
        ("DynamicWord", 200),
        ("PointerArgument", 77),
        ("DynamicStore", 99),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|e| panic!("{entry}: {e}"));
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let literal: &[u8] = match entry {
            "ByteTable" => &[5, 10, 15, 20],
            "WordTable" => &[0xe8, 3, 0xd0, 7, 0xb8, 0x0b],
            _ => &[],
        };
        if !literal.is_empty() {
            assert!(
                object
                    .sections
                    .iter()
                    .any(|s| s.name.starts_with(".rodata.")
                        && s.data.windows(literal.len()).any(|bytes| bytes == literal)),
                "{entry} ROM initializer"
            );
        }
        let mut objects = vec![object];
        let direct_rom = matches!(entry, "RomByte" | "RomWord");
        if direct_rom {
            let constants = koh_objects::Object {
                sections: vec![koh_objects::Section {
                    name: ".fixture.constants".into(),
                    kind: 0,
                    address: Some(0x3ff0),
                    bank: Some(0),
                    size: 6,
                    data: vec![5, 10, 15, 20, 0xe8, 3],
                    ..Default::default()
                }],
                ..Default::default()
            };
            objects
                .push(koh_objects::read_elf(&koh_objects::write_elf(&constants).unwrap()).unwrap());
        }
        let linked = koh_link::link(
            &objects,
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
        let rom = &linked.rom;
        if direct_rom {
            assert_eq!(&rom[0x3ff0..0x3ff6], &[5, 10, 15, 20, 0xe8, 3]);
        }
        assert_eq!(&rom[0x100..0x102], &[0, 0xc3]);
        let startup = linked
            .symbols
            .iter()
            .find(|s| s.name == "__koh_start")
            .unwrap();
        assert_eq!(
            i32::from(u16::from_le_bytes(rom[0x102..0x104].try_into().unwrap())),
            startup.value
        );
        assert_eq!(
            &rom[0x104..0x134],
            &[
                0xce, 0xed, 0x66, 0x66, 0xcc, 0x0d, 0, 0x0b, 3, 0x73, 0, 0x83, 0, 0x0c, 0, 0x0d, 0,
                8, 0x11, 0x1f, 0x88, 0x89, 0, 0x0e, 0xdc, 0xcc, 0x6e, 0xe6, 0xdd, 0xdd, 0xd9, 0x99,
                0xbb, 0xbb, 0x67, 0x63, 0x6e, 0x0e, 0xec, 0xcc, 0xdd, 0xdc, 0x99, 0x9f, 0xbb, 0xb9,
                0x33, 0x3e,
            ]
        );
        assert_eq!(rom[0x147], 0x19);
        assert_eq!(rom[0x143], if color { 0x80 } else { 0 });
        let header = rom[0x134..0x14d]
            .iter()
            .fold(0u8, |sum, b| sum.wrapping_sub(*b).wrapping_sub(1));
        assert_eq!(rom[0x14d], header);
        let global = rom
            .iter()
            .enumerate()
            .filter(|(i, _)| !matches!(i, 0x14e | 0x14f))
            .fold(0u16, |sum, (_, b)| sum.wrapping_add(u16::from(*b)));
        assert_eq!(
            u16::from_be_bytes(rom[0x14e..0x150].try_into().unwrap()),
            global
        );
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", &format!("{expected:08X}"), "D000", "OK"],
            "{entry} {configuration} color={color}"
        );
        if literal.is_empty() {
            super::assert_heap_snapshot(fields[4], &[0x5a; 4096], entry);
        }
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
}
