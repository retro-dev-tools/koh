use super::{Host, execute, gameboy_images, host};
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
        ("RawQuotient", "00000001"),
        ("RawProduct", "00000001"),
        ("Main", "40800000"),
        ("Negative", "BFE00000"),
        ("Join", "40800000"),
        ("Reference", "40800000"),
        ("Field", "40800000"),
        ("Array", "40800000"),
        ("Difference", "BF800000"),
        ("TinyDifference", "80000001"),
        ("InvalidDifference", "7FC00000"),
        ("Negated", "BFC00000"),
        ("NegatedNegative", "40200000"),
        ("NegatedZero", "80000000"),
        ("NegatedTiny", "80000001"),
        ("NegatedInfinity", "FF800000"),
        ("NegatedNan", "FFC12345"),
        ("Product", "40C00000"),
        ("TransitionProduct", "00800000"),
        ("HalfProduct", "00000000"),
        ("DeepUnderflowProduct", "00000000"),
        ("ZeroProduct", "80000000"),
        ("OverflowProduct", "7F800000"),
        ("InfinityProduct", "FF800000"),
        ("NanProduct", "7FC00000"),
        ("NegativeProduct", "C0C00000"),
        ("TinyProduct", "00000002"),
        ("UnderflowProduct", "00400000"),
        ("InvalidProduct", "7FC00000"),
        ("TieProduct", "3F802002"),
        ("Quotient", "40600000"),
        ("RepeatingQuotient", "3EAAAAAB"),
        ("NegativeQuotient", "C0100000"),
        ("TinyQuotient", "00000002"),
        ("SubnormalQuotient", "00400000"),
        ("TransitionQuotient", "00800000"),
        ("DeepQuotient", "00000000"),
        ("OverflowQuotient", "7F800000"),
        ("ZeroQuotient", "80000000"),
        ("ZeroDivisor", "7F800000"),
        ("InvalidQuotient", "7FC00000"),
        ("InfiniteQuotient", "7FC00000"),
        ("FiniteOverInfinity", "80000000"),
        ("InfinityOverFinite", "FF800000"),
        ("NanQuotient", "7FC00000"),
        ("RoundedSum", "3E99999A"),
        ("PositiveDifference", "40100000"),
        ("OriginalDifference", "C0400000"),
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
        assert_eq!(
            execute(host, directory.path(), &linked, "__koh_exit"),
            format!("00 {expected} D000 OK"),
            "{entry}"
        );
    }
    let error = compile::object(&resolver, Some("Program::Unsupported"))
        .err()
        .expect("unimplemented float operation must reject");
    assert!(
        error.contains("IL_") && error.contains("f32.rem"),
        "{error}"
    );
    for (before, after, entry, expected) in [
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
    let method = image
        .metadata()
        .method_defs
        .iter()
        .enumerate()
        .find(|(i, _)| {
            image
                .method_name(0x06000001 + *i as u32)
                .unwrap()
                .ends_with("SoftFloat::SumF32")
        })
        .unwrap()
        .1;
    let heap = heap_offset(&images[1], "#Blob");
    let start = heap + method.signature as usize;
    assert_eq!(&images[1][start..start + 6], &[5, 0, 2, 9, 9, 9]);
    let mut changed = images[1].clone();
    changed[start + 3] = 8;
    let resolver = Resolver::new(&[&images[0], &changed]).unwrap();
    let error = compile::object(&resolver, Some("Program::Main"))
        .err()
        .unwrap();
    assert!(
        error.contains("uint(uint,uint)") && error.contains("IL_"),
        "{error}"
    );
}
