use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

pub(super) fn heap_offset(bytes: &[u8], name: &str) -> usize {
    let file = object::read::pe::PeFile32::parse(bytes).unwrap();
    let sections = file.section_table();
    let cli = file
        .data_directory(object::pe::IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR)
        .unwrap()
        .data(bytes, &sections)
        .unwrap();
    let rva = u32::from_le_bytes(cli[8..12].try_into().unwrap());
    let (root, _) = sections.pe_file_range_at(rva).unwrap();
    let metadata = clrmeta::MetadataRoot::parse(&bytes[root as usize..]).unwrap();
    root as usize + metadata.find_stream(name).unwrap().offset as usize
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn roslyn_float_arithmetic_calls_joins_and_references_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, mut images) = gameboy_images(host, configuration, "Float32");
    let image = koh_compile::assembly::Assembly::parse(&images[1]).unwrap();
    let name = image
        .metadata()
        .method_defs
        .iter()
        .enumerate()
        .find(|(i, _)| {
            image
                .method_name(0x06000001 + *i as u32)
                .unwrap()
                .ends_with("SoftFloat::AddF32")
        })
        .unwrap()
        .1
        .name;
    let name = heap_offset(&images[1], "#Strings") + name as usize;
    assert_eq!(&images[1][name..name + 6], b"AddF32");
    images[1][name..name + 6].copy_from_slice(b"SumF32");
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/Float32.pdb")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let token = (1..=image.metadata().method_defs.len() as u32)
        .map(|row| 0x06000000 | row)
        .find(|&token| image.method_name(token).unwrap() == "Program::Add")
        .unwrap();
    let direct = koh_compile::lower::assembly_method(
        &resolver,
        koh_compile::resolve::Definition { assembly: 0, token },
    )
    .unwrap();
    koh_compile::ir::verify(&direct).unwrap();
    for (entry, expected) in [
        (
            "RemNegative",
            format!(
                "{:08X}",
                (f32::from_bits(0xc0b00000) % f32::from_bits(0x40000000)).to_bits()
            ),
        ),
        (
            "RemNegativeDivisor",
            format!(
                "{:08X}",
                (f32::from_bits(0x40b00000) % f32::from_bits(0xc0000000)).to_bits()
            ),
        ),
        (
            "RemTrunc",
            format!(
                "{:08X}",
                (f32::from_bits(0x40e00000) % f32::from_bits(0x40000000)).to_bits()
            ),
        ),
        (
            "RemZero",
            format!(
                "{:08X}",
                (f32::from_bits(0x80000000) % f32::from_bits(0x40000000)).to_bits()
            ),
        ),
        (
            "RemMultiple",
            format!(
                "{:08X}",
                (f32::from_bits(0xc0800000) % f32::from_bits(0x40000000)).to_bits()
            ),
        ),
        (
            "RemSmall",
            format!(
                "{:08X}",
                (f32::from_bits(0x3f800000) % f32::from_bits(0x40000000)).to_bits()
            ),
        ),
        (
            "RemTiny",
            format!(
                "{:08X}",
                (f32::from_bits(0x00000003) % f32::from_bits(0x00000002)).to_bits()
            ),
        ),
        (
            "RemTinyNegative",
            format!(
                "{:08X}",
                (f32::from_bits(0x80000003) % f32::from_bits(0x00000002)).to_bits()
            ),
        ),
        (
            "RemTransition",
            format!(
                "{:08X}",
                (f32::from_bits(0x00800000) % f32::from_bits(0x00000003)).to_bits()
            ),
        ),
        (
            "RemExtreme",
            format!(
                "{:08X}",
                (f32::from_bits(0x7f7ffffe) % f32::from_bits(0x00000003)).to_bits()
            ),
        ),
        ("RemZeroDivisor", format!("{:08X}", 0x7fc00000u32)),
        ("RemInfinity", format!("{:08X}", 0x7fc00000u32)),
        ("RemInfInf", format!("{:08X}", 0x7fc00000u32)),
        (
            "RemByInfinity",
            format!(
                "{:08X}",
                (f32::from_bits(0xbf800000) % f32::from_bits(0x7f800000)).to_bits()
            ),
        ),
        ("RemNan", format!("{:08X}", 0x7fc00000u32)),
        ("RemZeroZero", format!("{:08X}", 0x7fc00000u32)),
        ("RemainderResult", "3FC00000".to_owned()),
        ("RawQuotient", "00000001".to_owned()),
        ("RawProduct", "00000001".to_owned()),
        ("Main", "40800000".to_owned()),
        ("Negative", "BFE00000".to_owned()),
        ("Join", "40800000".to_owned()),
        ("Reference", "40800000".to_owned()),
        ("Field", "40800000".to_owned()),
        ("Array", "40800000".to_owned()),
        ("Difference", "BF800000".to_owned()),
        ("TinyDifference", "80000001".to_owned()),
        ("InvalidDifference", "7FC00000".to_owned()),
        ("Negated", "BFC00000".to_owned()),
        ("NegatedNegative", "40200000".to_owned()),
        ("NegatedZero", "80000000".to_owned()),
        ("NegatedTiny", "80000001".to_owned()),
        ("NegatedInfinity", "FF800000".to_owned()),
        ("NegatedNan", "FFC12345".to_owned()),
        ("Product", "40C00000".to_owned()),
        ("TransitionProduct", "00800000".to_owned()),
        ("HalfProduct", "00000000".to_owned()),
        ("DeepUnderflowProduct", "00000000".to_owned()),
        ("ZeroProduct", "80000000".to_owned()),
        ("OverflowProduct", "7F800000".to_owned()),
        ("InfinityProduct", "FF800000".to_owned()),
        ("NanProduct", "7FC00000".to_owned()),
        ("NegativeProduct", "C0C00000".to_owned()),
        ("TinyProduct", "00000002".to_owned()),
        ("UnderflowProduct", "00400000".to_owned()),
        ("InvalidProduct", "7FC00000".to_owned()),
        ("TieProduct", "3F802002".to_owned()),
        ("Quotient", "40600000".to_owned()),
        ("RepeatingQuotient", "3EAAAAAB".to_owned()),
        ("NegativeQuotient", "C0100000".to_owned()),
        ("TinyQuotient", "00000002".to_owned()),
        ("SubnormalQuotient", "00400000".to_owned()),
        ("TransitionQuotient", "00800000".to_owned()),
        ("DeepQuotient", "00000000".to_owned()),
        ("OverflowQuotient", "7F800000".to_owned()),
        ("ZeroQuotient", "80000000".to_owned()),
        ("ZeroDivisor", "7F800000".to_owned()),
        ("InvalidQuotient", "7FC00000".to_owned()),
        ("InfiniteQuotient", "7FC00000".to_owned()),
        ("FiniteOverInfinity", "80000000".to_owned()),
        ("InfinityOverFinite", "FF800000".to_owned()),
        ("NanQuotient", "7FC00000".to_owned()),
        ("RoundedSum", "3E99999A".to_owned()),
        ("PositiveDifference", "40100000".to_owned()),
        ("OriginalDifference", "C0400000".to_owned()),
    ] {
        let name = format!("Program::{entry}");
        let mut object = compile::object_with_debug(&resolver, Some(&name), &[Some(&pdb), None])
            .unwrap_or_else(|e| panic!("{entry}: {e}"));
        assert!(
            object
                .sections
                .iter()
                .flat_map(|section| &section.lines)
                .any(|line| line.file.ends_with("Program.cs") && line.line > 0)
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
        assert!(linked.kdbg().unwrap().starts_with(b"KDBG"));
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 6 } else { 5 }, "{entry}");
        assert_eq!(
            &fields[..4],
            &["00", expected.as_str(), "D000", "OK"],
            "{entry}"
        );
        let mut heap = vec![0x5a; 4096];
        if entry == "Array" {
            heap[..2].copy_from_slice(&2u16.to_le_bytes());
            heap[2..6].copy_from_slice(&1.5f32.to_bits().to_le_bytes());
            heap[6..10].copy_from_slice(&2.5f32.to_bits().to_le_bytes());
        }
        super::assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for (before, after, entry, expected) in [
        (
            "f32.rem",
            "f32.zzz",
            "RemainderResult",
            "Missing KohRuntime f32.rem",
        ),
        ("f32.add", "f32.zzz", "Main", "Missing KohRuntime f32.add"),
        (
            "f32.sub",
            "f32.zzz",
            "Difference",
            "Missing KohRuntime f32.sub",
        ),
        ("f32.sub", "f32.add", "Main", "Duplicate KohRuntime"),
    ] {
        let mut changed = images[1].clone();
        let offsets: Vec<_> = changed
            .windows(before.len())
            .enumerate()
            .filter(|(_, bytes)| *bytes == before.as_bytes())
            .map(|(i, _)| i)
            .collect();
        assert_eq!(offsets.len(), 1);
        changed[offsets[0]..offsets[0] + after.len()].copy_from_slice(after.as_bytes());
        let resolver = Resolver::new(&[&images[0], &changed]).unwrap();
        let error = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .err()
            .unwrap();
        assert!(error.contains(expected), "{error}");
    }
    let image = resolver.assembly(1).unwrap();
    for (method_name, entry) in [("SumF32", "Main"), ("RemF32", "RemainderResult")] {
        let method = image
            .metadata()
            .method_defs
            .iter()
            .enumerate()
            .find(|(i, _)| {
                image
                    .method_name(0x06000001 + *i as u32)
                    .unwrap()
                    .ends_with(&format!("SoftFloat::{method_name}"))
            })
            .unwrap()
            .1;
        let offset = heap_offset(&images[1], "#Strings") + method.name as usize;
        let mut renamed = images[1].clone();
        renamed[offset] = b'Z';
        let selected = Resolver::new(&[&images[0], &renamed]).unwrap();
        compile::object(&selected, Some(&format!("Program::{entry}"))).unwrap();
        let heap = heap_offset(&images[1], "#Blob");
        let start = heap + method.signature as usize;
        assert_eq!(&images[1][start..start + 6], &[5, 0, 2, 9, 9, 9]);
        let mut changed = images[1].clone();
        changed[start + 3] = 8;
        let resolver = Resolver::new(&[&images[0], &changed]).unwrap();
        let error = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .err()
            .unwrap();
        assert!(
            error.contains("uint(uint,uint)") && error.contains("IL_"),
            "{error}"
        );
    }
}
