use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn struct_pointer_and_switch_contracts_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "StructContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let dispatch = (1..=assembly.metadata().method_defs.len())
        .map(|r| 0x06000000 | r as u32)
        .find(|&t| assembly.method_name(t).unwrap() == "Program::Dispatch")
        .unwrap();
    assert!(
        koh_compile::cil::decode(assembly.method_body(dispatch).unwrap().il)
            .unwrap()
            .iter()
            .any(|i| i.opcode == 0x45),
        "fixture must exercise real CIL switch"
    );
    for (entry, value, packet) in [
        ("Copy", 65u8, vec![10, 20, 15, 20]),
        ("Pointer", 42, vec![]),
        ("Offsets", 51, vec![]),
        ("Stack", 10, vec![1, 2, 3, 4]),
        ("RefByte", 42, vec![42]),
        ("RefStruct", 37, vec![17, 20]),
        ("ByValue", 30, vec![10, 20, 37]),
        ("Selected", 66, vec![]),
        ("Default", 77, vec![]),
        ("SwitchCoverage", 42, vec![]),
        ("Enum", 200, vec![]),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
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
        eprintln!("Execute {entry}");
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-video",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", &format!("{value:08X}"), "D000", "OK"],
            "{entry}"
        );
        let mut expected = [0x5a; 256];
        expected[..packet.len()].copy_from_slice(&packet);
        if entry == "SwitchCoverage" {
            let normal = [99, 10, 11, 12, 13, 14, 15, 66, 17, 18, 19, 99, 99, 99];
            let defaults = [77, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 77, 77, 77];
            expected[..14].copy_from_slice(&normal);
            expected[16..30].copy_from_slice(&defaults);
        }
        assert_heap_snapshot(&fields[4][0xf00 * 2..], &expected, entry);
        let mut vram = [0xa7; 8192];
        if entry == "Pointer" {
            vram[0] = 42;
        }
        if entry == "Offsets" {
            vram[3..5].copy_from_slice(&[0x11, 0x22]);
        }
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, entry);
        if color {
            assert_heap_snapshot(fields[7], &[0xb8; 8192], entry);
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
        let oam = if color { 9 } else { 7 };
        assert_heap_snapshot(fields[oam], &[0xc9; 160], entry);
        assert_eq!(&fields[oam + 1][..4], format!("00{value:02X}"), "{entry}");
    }
}
