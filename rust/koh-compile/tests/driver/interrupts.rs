use super::{Host, compile, execute_mode, gameboy_images, host};
use koh_compile::resolve::Resolver;
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

#[rstest]
#[case("Debug")]
#[case("Release")]
fn interrupt_contract_failures_are_explicit(host: &Host, #[case] configuration: &str) {
    let (directory, images) = gameboy_images(host, configuration, "InterruptDispatch");
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
    for replacement in [
        "Calls",
        "Nested",
        "Allocates",
        "UnsafeDma",
        "Recursive",
        "UnsafeVramBank",
        "UnsafeWramBank",
        "UnsafeSpeed",
        "UnsafeBoot",
        "UnsafeHdma",
        "UnsafeWideWrite",
        "UnsafeWideRead",
        "UnsafeRam",
        "UnsafeRom",
        "UnsafeHram",
        "UnsafeWrappedBank",
        "UnsafeDynamic",
        "RecursiveHelper",
    ] {
        let mut bytes = images[0].clone();
        bytes[row..row + 4].copy_from_slice(&methods[index(replacement)].rva.to_le_bytes());
        variants.push((
            bytes,
            if replacement == "RecursiveHelper" {
                "acyclic"
            } else {
                "Interrupt"
            },
        ));
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
    let mut bytes = images[0].clone();
    let (joypad, _) = metadata::table_row(&bytes, clrmeta::TableId::MethodDef, index("OnJoypad"));
    bytes[joypad..joypad + 4].copy_from_slice(&methods[index("SafeMask")].rva.to_le_bytes());
    let resolver = Resolver::new(&[&bytes, &images[1]]).unwrap();
    let object = compile::object(&resolver, Some("Program::Software")).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    for color in [false, true] {
        let linked = koh_link::link(
            &[object.clone()],
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
        assert_eq!(
            &report.split_whitespace().collect::<Vec<_>>()[..4],
            ["00", "00011111", "D000", "OK"]
        );
    }
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
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/InterruptDispatch.pdb")),
    )
    .unwrap();
    let runtime_pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/Koh.GameBoy.pdb")),
    )
    .unwrap();
    for (entry, expected) in [
        ("Main", "00020038"),
        ("Count3", "00030038"),
        ("Count5", "00050038"),
        ("BankZero", "00020038"),
        ("Software", "01F11111"),
        ("Stack", ""),
    ] {
        eprintln!("Interrupt entry {entry}");
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), Some(&runtime_pdb)],
        )
        .unwrap();
        object.sections.push(koh_objects::Section {
            name: ".test.irq.output".into(),
            kind: 4,
            address: Some(0xc008),
            size: 0x20,
            ..Default::default()
        });
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
        if entry == "BankZero" {
            for section in object
                .sections
                .iter_mut()
                .filter(|s| s.kind == 1 && s.name.starts_with(".text.__koh_a"))
            {
                section.kind = 0;
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
            assert_eq!(&linked.rom[address..address + 4], &[0xf5, 0xc5, 0xd5, 0xe5]);
            let end = linked
                .symbols
                .iter()
                .filter(|s| {
                    s.kind == 0 && (s.name.starts_with("__koh_irq_") || s.name == "__koh_far_call")
                })
                .map(|s| s.value as usize)
                .filter(|&value| value > address && value < 0x4000)
                .min()
                .unwrap();
            assert_eq!(&linked.rom[end - 5..end], &[0xe1, 0xd1, 0xc1, 0xf1, 0xd9]);
        }
        let reserve = linked
            .symbols
            .iter()
            .find(|s| s.name == "__koh_irq_stack_reserve")
            .unwrap()
            .value as u16;
        for name in [
            "Program::AddNative [",
            "Program::SharedAdd [",
            "Program::AddWide [",
            "Koh.GameBoy.SoftFloat::NegF32 [",
        ] {
            let symbol = linked
                .symbols
                .iter()
                .find(|s| s.name.starts_with(name))
                .unwrap_or_else(|| panic!("Missing IRQ helper {name}"));
            assert_eq!(symbol.bank, Some(0));
            let source = symbol.source.as_ref().unwrap();
            assert!(
                source.file.ends_with(if name.starts_with("Program") {
                    "Program.cs"
                } else {
                    "SoftFloat.cs"
                }) && source.line > 0
            );
            let end = linked
                .symbols
                .iter()
                .filter(|next| {
                    next.bank == Some(0)
                        && next.value > symbol.value
                        && next.name.contains(" [__koh_")
                })
                .map(|next| next.value as u32)
                .min()
                .unwrap_or(0x4000);
            assert!(
                linked.lines.iter().any(|line| line.bank == 0
                    && line.address >= symbol.value as u32
                    && line.address < end
                    && line.count > 0
                    && line.file == source.file
                    && line.line > 0),
                "{name}: source mapping within function"
            );
        }
        let debug = koh_link::read_dwarf(&linked.dwarf().unwrap()).unwrap();
        assert_eq!(debug.symbols.len(), linked.symbols.len());
        if entry == "Main" {
            assert!(
                debug
                    .symbols
                    .iter()
                    .any(|symbol| symbol.bank.is_some_and(|bank| bank > 255))
            );
        }
        assert!(
            !linked
                .symbols
                .iter()
                .any(|s| s.name.starts_with("Koh.GameBoy.SoftFloat::AddF32 ["))
        );
        let mode = if entry == "Stack" {
            format!("--native-irq-abi:{reserve:x}")
        } else if entry != "Software" {
            "--native-irq-count".into()
        } else {
            "--native-heap".into()
        };
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            if entry == "Stack" {
                "__koh_abort"
            } else {
                "__koh_exit"
            },
            &mode,
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        if entry == "Stack" {
            assert_eq!(fields[0], "01");
            assert_eq!(fields[3], "OK");
            let probe: Vec<_> = fields.last().unwrap().split(':').collect();
            assert_eq!(probe[0], "1");
            let stack = u16::from_str_radix(probe[1], 16).unwrap();
            assert!((0xc100 + reserve..0xc200 + reserve).contains(&stack));
        } else {
            assert_eq!(&fields[..4], ["00", expected, "D000", "OK"]);
            if entry != "Software" {
                let packet: Vec<_> = fields.last().unwrap().split(':').collect();
                let frames = u32::from_str_radix(expected, 16).unwrap() >> 16;
                assert_eq!(packet[0].parse::<u32>().unwrap(), frames);
                assert_eq!(
                    packet[1],
                    format!(
                        "{:02X}EE{:02X}{:02X}",
                        frames * 3,
                        0xe0 | frames,
                        (0xfe + frames) & 0xff
                    )
                );
                assert_eq!(
                    packet[2],
                    format!(
                        "{:016X}",
                        u64::from(frames)
                            .wrapping_mul(0x8000000100000002)
                            .swap_bytes()
                    )
                );
                assert_eq!(
                    packet[3],
                    format!("{:04X}", (0x200u16 + frames as u16).swap_bytes())
                );
                let bank = linked
                    .symbols
                    .iter()
                    .find(|s| s.name.starts_with("Program::Real ["))
                    .unwrap()
                    .bank
                    .unwrap();
                assert_eq!(packet[4], format!("{bank:04X}"));
                assert_eq!(packet[5], "2A000000");
            }
        }
    }
}
