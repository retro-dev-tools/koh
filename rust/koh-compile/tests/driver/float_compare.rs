use super::{Host, execute, float32::heap_offset, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn roslyn_float_comparisons_preserve_ordered_and_unordered_semantics(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FloatComparisons");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let root = resolver.assembly(0).unwrap();
    let mut value_opcodes = std::collections::BTreeSet::new();
    let mut fused_branch = false;
    let mut conditional_branch = false;
    for row in 1..=root.metadata().method_defs.len() as u32 {
        let token = 0x06000000 | row;
        let name = root.method_name(token).unwrap();
        if [
            "Less",
            "LessEqual",
            "Greater",
            "GreaterEqual",
            "Equal",
            "NotEqual",
            "Branches",
        ]
        .iter()
        .any(|method| name == format!("FloatComparisons::{method}"))
        {
            for instruction in
                koh_compile::cil::decode(root.method_body(token).unwrap().il).unwrap()
            {
                if name.ends_with("::Branches") {
                    fused_branch |= matches!(instruction.opcode, 0x2e..=0x37 | 0x3b..=0x44);
                    conditional_branch |= matches!(instruction.opcode, 0x2c..=0x37 | 0x39..=0x44);
                } else {
                    value_opcodes.insert(instruction.opcode);
                }
            }
        }
    }
    assert!(
        conditional_branch,
        "fixture must exercise conditional branches"
    );
    if configuration == "Release" {
        assert!(fused_branch, "Release fixture must exercise fused branches");
    }
    for opcode in 0xfe01..=0xfe05 {
        assert!(
            value_opcodes.contains(&opcode),
            "fixture must exercise comparison{opcode:04x}"
        );
    }
    let pairs: [(u32, u32); 18] = [
        (0x3fc00000, 0x40000000),
        (0x40000000, 0x3fc00000),
        (0x40000000, 0x40000000),
        (0xbf800000, 0xc0000000),
        (0x3f800000, 0x3f800000),
        (0x3dcccccd, 0x3dcccccd),
        (0x3dcccccd, 0x3e4ccccd),
        (0x80000000, 0x00000000),
        (0xc0000000, 0xbf800000),
        (0xbf800000, 0xbf800000),
        (0xbf000000, 0xbf000000),
        (0x7fc12345, 0x3f800000),
        (0x3f800000, 0xffc12345),
        (0x7fa12345, 0xffc12345),
        (0x7f800000, 0xff800000),
        (0xff800000, 0xff800000),
        (0x00000001, 0x80000001),
        (0x00000000, 0x80000000),
    ];
    let comparisons = pairs.into_iter().enumerate().map(|(i, (a, b))| {
        let (a, b) = (f32::from_bits(a), f32::from_bits(b));
        let predicates = [a < b, a <= b, a > b, a >= b, a == b, a != b];
        let mask = predicates
            .into_iter()
            .enumerate()
            .fold(0, |mask, (bit, value)| mask | (u32::from(value) << bit));
        (format!("Case{i}"), mask | (mask << 6))
    });
    for (entry, expected) in comparisons.chain([
        ("MinForward".into(), 0x40200000),
        ("MinReverse".into(), 0x40200000),
        ("MinNegative".into(), 0xbf800000),
    ]) {
        let entry = format!("FloatComparisons::{entry}");
        let mut object = compile::object(&resolver, Some(&entry))
            .unwrap_or_else(|error| panic!("{entry}: {error}"));
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
        assert_eq!(
            execute(host, directory.path(), &linked, "__koh_exit"),
            format!("00 {expected:08X} D000 OK"),
            "{entry}"
        );
    }
    let mut missing = images[1].clone();
    let offsets: Vec<_> = missing
        .windows(7)
        .enumerate()
        .filter(|(_, bytes)| *bytes == b"f32.cmp")
        .map(|(i, _)| i)
        .collect();
    assert_eq!(offsets.len(), 1);
    missing[offsets[0]..offsets[0] + 7].copy_from_slice(b"f32.zzz");
    let changed = Resolver::new(&[&images[0], &missing]).unwrap();
    let error = compile::object(&changed, Some("FloatComparisons::Case0"))
        .err()
        .unwrap();
    assert!(
        error.contains("Missing KohRuntime f32.cmp") && error.contains("IL_"),
        "{error}"
    );

    let runtime = resolver.assembly(1).unwrap();
    let method = runtime
        .metadata()
        .method_defs
        .iter()
        .enumerate()
        .find(|(i, _)| {
            runtime
                .method_name(0x06000001 + *i as u32)
                .unwrap()
                .ends_with("SoftFloat::CmpF32")
        })
        .unwrap()
        .1;
    let start = heap_offset(&images[1], "#Blob") + method.signature as usize;
    assert_eq!(&images[1][start..start + 6], &[5, 0, 2, 5, 9, 9]);
    let mut wrong = images[1].clone();
    wrong[start + 3] = 9;
    let changed = Resolver::new(&[&images[0], &wrong]).unwrap();
    let error = compile::object(&changed, Some("FloatComparisons::Case0"))
        .err()
        .unwrap();
    assert!(
        error.contains("byte(uint,uint)") && error.contains("IL_"),
        "{error}"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", true)]
fn every_float_branch_encoding_handles_nan_and_signed_zero(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FloatComparisons");
    let root = koh_compile::assembly::Assembly::parse(&images[0]).unwrap();
    let token = (1..=root.metadata().method_defs.len() as u32)
        .map(|row| 0x06000000 | row)
        .find(|&token| root.method_name(token).unwrap() == "FloatComparisons::Probe")
        .unwrap();
    let method = root.method(token).unwrap();
    let body = root.method_body(token).unwrap();
    assert!(!body.extra_sections && body.max_stack >= 2 && body.il.len() >= 11);
    let code = body.il.as_ptr() as usize - images[0].as_ptr() as usize;
    let file = object::read::pe::PeFile32::parse(images[0].as_slice()).unwrap();
    let header = file.section_table().pe_file_range_at(method.rva).unwrap().0 as usize;
    for short in [true, false] {
        for op in 0x2e_u8..=0x37 {
            let mut il = vec![0x02, 0x03, if short { op } else { op + 0x0d }, 2];
            if !short {
                il.extend([0, 0, 0]);
            }
            il.extend([0x16, 0x2a, 0x17, 0x2a]);
            let mut changed = images[0].clone();
            match changed[header] & 3 {
                2 => changed[header] = (il.len() as u8) << 2 | 2,
                3 => changed[header + 4..header + 8]
                    .copy_from_slice(&(il.len() as u32).to_le_bytes()),
                _ => panic!("fixture method header"),
            }
            changed[code..code + il.len()].copy_from_slice(&il);
            let resolver = Resolver::new(&[&changed, &images[1]]).unwrap();
            for (entry, ordering) in [
                ("ProbeLeftNan", None),
                ("ProbeRightNan", None),
                ("ProbeLess", Some(std::cmp::Ordering::Less)),
                ("ProbeGreater", Some(std::cmp::Ordering::Greater)),
                ("ProbeEqual", Some(std::cmp::Ordering::Equal)),
            ] {
                use std::cmp::Ordering::{Equal, Greater, Less};
                let expected = match op {
                    0x2e => ordering == Some(Equal),
                    0x2f => matches!(ordering, Some(Greater | Equal)),
                    0x30 => ordering == Some(Greater),
                    0x31 => matches!(ordering, Some(Less | Equal)),
                    0x32 => ordering == Some(Less),
                    0x33 => ordering != Some(Equal),
                    0x34 => ordering != Some(Less),
                    0x35 => matches!(ordering, None | Some(Greater)),
                    0x36 => ordering != Some(Greater),
                    0x37 => matches!(ordering, None | Some(Less)),
                    _ => unreachable!(),
                };
                let object =
                    compile::object(&resolver, Some(&format!("FloatComparisons::{entry}")))
                        .unwrap();
                let object =
                    koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
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
                assert_eq!(
                    execute(host, directory.path(), &linked, "__koh_exit"),
                    format!("00 {:08X} D000 OK", u32::from(expected)),
                    "{entry} opcode{op:02x} short={short}"
                );
            }
        }
    }
}
