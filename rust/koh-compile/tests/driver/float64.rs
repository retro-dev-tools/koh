use super::{Host, execute_mode, float32::heap_offset, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn roslyn_binary64_arithmetic_and_storage_preserve_all_bits(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Float64");
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/Float64.pdb")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, expected) in [
        ("RawTiny", 2u64),
        ("Main", 0x4010000000000000),
        ("Opposite", 0xbffc000000000000),
        ("Tiny", 2),
        ("Transition", 0x0010000000000000),
        ("Zero", 0x8000000000000000),
        ("Cancellation", 0),
        ("Infinity", 0x7ff0000000000000),
        ("Invalid", 0x7ff8000000000000),
        ("Nan", 0x7ff8000000000000),
        ("Negative", 0xbff8000000000000),
        ("Difference", 0xbff0000000000000),
        ("Join", 0x4010000000000000),
        ("Reference", 0x4010000000000000),
        ("Field", 0x4010000000000000),
        ("Array", 0x4010000000000000),
        ("CompareNan", 32 | (32 << 6)),
        ("CompareNegative", 44 | (44 << 6)),
        ("CompareZero", 26 | (26 << 6)),
        ("CompareForward", 35 | (35 << 6)),
        ("CompareEqual", 26 | (26 << 6)),
        ("CompareFractionEqual", 26 | (26 << 6)),
        ("CompareFractions", 35 | (35 << 6)),
    ] {
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("Float64::{entry}")),
            &[Some(&pdb), None],
        )
        .unwrap_or_else(|error| panic!("{entry}: {error}"));
        assert!(
            object
                .sections
                .iter()
                .flat_map(|s| &s.lines)
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
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut heap = vec![0x5a; 4096];
        heap[4088..].copy_from_slice(&expected.to_le_bytes());
        if entry == "Array" {
            heap[..2].copy_from_slice(&2u16.to_le_bytes());
            heap[2..10].copy_from_slice(&1.5f64.to_bits().to_le_bytes());
            heap[10..18].copy_from_slice(&2.5f64.to_bits().to_le_bytes());
        }
        super::assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for (entry, expected) in [
        ("UnsupportedMultiply", "multiplication"),
        ("UnsupportedDivide", "division"),
        ("UnsupportedConversion", "conversion"),
        ("UnsupportedInteger", "conversion"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Float64::{entry}")))
            .err()
            .unwrap();
        assert!(error.contains("IL_") && error.contains(expected), "{error}");
    }
    for (key, entry) in [("f64.add", "Main"), ("f64.cmp", "CompareNan")] {
        let mut changed = images[1].clone();
        let offsets: Vec<_> = changed
            .windows(key.len())
            .enumerate()
            .filter(|(_, bytes)| *bytes == key.as_bytes())
            .map(|(i, _)| i)
            .collect();
        assert_eq!(offsets.len(), 1);
        changed[offsets[0]] = b'z';
        let resolver = Resolver::new(&[&images[0], &changed]).unwrap();
        let error = compile::object(&resolver, Some(&format!("Float64::{entry}")))
            .err()
            .unwrap();
        assert!(
            error.contains(&format!("Missing KohRuntime {key}")) && error.contains("IL_"),
            "{error}"
        );
    }
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
                .ends_with("SoftFloat::AddF64")
        })
        .unwrap()
        .1;
    let start = heap_offset(&images[1], "#Blob") + method.signature as usize;
    assert_eq!(&images[1][start..start + 6], &[5, 0, 2, 11, 11, 11]);
    let mut changed = images[1].clone();
    changed[start + 3] = 10;
    let resolver = Resolver::new(&[&images[0], &changed]).unwrap();
    let error = compile::object(&resolver, Some("Float64::Main"))
        .err()
        .unwrap();
    assert!(
        error.contains("ulong(ulong,ulong)") && error.contains("IL_"),
        "{error}"
    );
}
