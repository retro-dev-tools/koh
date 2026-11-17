use super::{Host, compile, execute_mode, gameboy_images, host};
use koh_compile::resolve::Resolver;
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

#[rstest]
#[case("Debug")]
#[case("Release")]
fn interrupt_contract_failures_are_explicit(host: &Host, #[case] configuration: &str) {
    let (_directory, images) = gameboy_images(host, configuration, "InterruptDispatch");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let methods = &image.metadata().method_defs;
    let index = |name: &str| {
        methods
            .iter()
            .position(|m| image.metadata().strings.get(m.name).unwrap() == name)
            .unwrap()
    };
    let handler = index("OnVBlank");
    let (row, context) = metadata::table_row(&images[0], clrmeta::TableId::MethodDef, handler);
    let mut variants = Vec::new();
    for replacement in ["Calls", "Nested", "Allocates", "RawWrite", "Recursive"] {
        let mut bytes = images[0].clone();
        bytes[row..row + 4].copy_from_slice(&methods[index(replacement)].rva.to_le_bytes());
        variants.push((bytes, "Interrupt"));
    }
    let mut bytes = images[0].clone();
    let (main, _) = metadata::table_row(&bytes, clrmeta::TableId::MethodDef, index("Main"));
    let offset = 8 + context.string_index_size();
    let signature = bytes[main + offset..main + offset + context.blob_index_size()].to_vec();
    bytes[row + offset..row + offset + signature.len()].copy_from_slice(&signature);
    variants.push((bytes, "static void"));
    let attribute = |method| {
        image
            .metadata()
            .custom_attributes
            .iter()
            .position(|a| {
                a.parent.table == Some(clrmeta::TableId::MethodDef)
                    && a.parent.row == method as u32 + 1
            })
            .unwrap()
    };
    let (source, _) = metadata::table_row(
        &images[0],
        clrmeta::TableId::CustomAttribute,
        attribute(handler),
    );
    let (destination, _) = metadata::table_row(
        &images[0],
        clrmeta::TableId::CustomAttribute,
        attribute(index("OnStat")),
    );
    let width = context.coded_index_size(clrmeta::CodedIndexKind::HasCustomAttribute);
    let mut bytes = images[0].clone();
    let parent = bytes[source..source + width].to_vec();
    bytes[destination..destination + width].copy_from_slice(&parent);
    variants.push((bytes, "Multiple Interrupt"));
    for (replacement, diagnostic) in [
        (b"VBlank", "Duplicate"),
        (b"BogusX", "Unknown"),
        (b"Bogus!", "payload"),
    ] {
        let mut bytes = images[0].clone();
        let position = bytes
            .windows(10)
            .position(|w| w == b"\x01\x00\x06Joypad\x00")
            .unwrap();
        bytes[position + 3..position + 9].copy_from_slice(replacement);
        variants.push((bytes, diagnostic));
    }
    for offset in [0, 3, 9] {
        let mut bytes = images[0].clone();
        let position = bytes
            .windows(10)
            .position(|w| w == b"\x01\x00\x06Joypad\x00")
            .unwrap();
        bytes[position + offset] = if offset == 3 { 0xff } else { 2 };
        variants.push((bytes, "payload"));
    }
    for (bytes, diagnostic) in variants {
        let error = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let resolver = Resolver::new(&[&bytes, &images[1]])?;
            compile::object(&resolver, None)
        }))
        .expect("Interrupt metadata must not panic")
        .unwrap_err();
        assert!(error.contains(diagnostic), "{error}");
        if diagnostic == "payload" {
            assert!(!error.contains("KohRuntime"), "{error}");
        }
    }
    let error = compile::object(&resolver, Some("Program::OnVBlank")).unwrap_err();
    assert!(
        error.contains("Interrupt handler cannot be the cartridge entry"),
        "{error}"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn virtual_dispatch_coexists_with_interrupt_roots(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "InterruptDispatch");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, expected) in [
        ("Main", "00020038"),
        ("Count3", "00030038"),
        ("Count5", "00050038"),
        ("Software", "00011111"),
        ("Stack", ""),
    ] {
        eprintln!("Interrupt entry {entry}");
        let mut object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
        if entry == "Main" {
            let mut bank = 257;
            for section in object
                .sections
                .iter_mut()
                .filter(|s| s.kind == 1 && s.name.starts_with(".text.__koh_a"))
            {
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
        for vector in [0x40, 0x48, 0x50, 0x58, 0x60] {
            assert_eq!(linked.rom[vector], 0xc3);
            let address = usize::from(u16::from_le_bytes(
                linked.rom[vector + 1..vector + 3].try_into().unwrap(),
            ));
            assert!(address < 0x4000);
            assert_eq!(
                &linked.rom[address..address + 5],
                &[0xf5, 0xc5, 0xd5, 0xe5, 0xcd]
            );
            assert_eq!(
                &linked.rom[address + 7..address + 12],
                &[0xe1, 0xd1, 0xc1, 0xf1, 0xd9]
            );
        }
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            if entry == "Stack" {
                "__koh_abort"
            } else {
                "__koh_exit"
            },
            if entry == "Stack" {
                "--native-irq-abi"
            } else if entry != "Software" {
                "--native-irq-count"
            } else {
                "--native-heap"
            },
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        if entry == "Stack" {
            assert_eq!(fields[0], "01");
            assert_eq!(fields[3], "OK");
            let probe: Vec<_> = fields.last().unwrap().split(':').collect();
            assert_eq!(probe[0], "1");
            let stack = u16::from_str_radix(probe[1], 16).unwrap();
            assert!((0xc100..0xc200).contains(&stack));
        } else {
            assert_eq!(&fields[..4], ["00", expected, "D000", "OK"]);
            if entry != "Software" {
                assert_eq!(
                    fields.last().unwrap().parse::<u32>().unwrap(),
                    u32::from_str_radix(expected, 16).unwrap() >> 16
                );
            }
        }
    }
}
