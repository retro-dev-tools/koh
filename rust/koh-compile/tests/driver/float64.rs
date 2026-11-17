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
        ("Widen0", f64::from(f32::from_bits(0x3fc00000)).to_bits()),
        ("Widen1", f64::from(f32::from_bits(0xc0200000)).to_bits()),
        ("Widen2", f64::from(f32::from_bits(0x3dcccccd)).to_bits()),
        ("Widen3", f64::from(f32::from_bits(0x00000001)).to_bits()),
        ("Widen4", f64::from(f32::from_bits(0x007fffff)).to_bits()),
        ("Widen5", f64::from(f32::from_bits(0x00800000)).to_bits()),
        ("Widen6", f64::from(f32::from_bits(0x80000000)).to_bits()),
        ("Widen7", f64::from(f32::from_bits(0x7f800000)).to_bits()),
        ("Widen8", f64::from(f32::from_bits(0xff800000)).to_bits()),
        (
            "Narrow0",
            u64::from((f64::from_bits(0x3ff8000000000000) as f32).to_bits()),
        ),
        (
            "Narrow1",
            u64::from((f64::from_bits(0xc004000000000000) as f32).to_bits()),
        ),
        (
            "Narrow2",
            u64::from((f64::from_bits(0x3fb999999999999a) as f32).to_bits()),
        ),
        (
            "Narrow3",
            u64::from((f64::from_bits(0x36a0000000000000) as f32).to_bits()),
        ),
        (
            "Narrow4",
            u64::from((f64::from_bits(0x3690000000000000) as f32).to_bits()),
        ),
        (
            "Narrow5",
            u64::from((f64::from_bits(0x3690000000000001) as f32).to_bits()),
        ),
        (
            "Narrow6",
            u64::from((f64::from_bits(0x36a8000000000000) as f32).to_bits()),
        ),
        (
            "Narrow7",
            u64::from((f64::from_bits(0x380fffffe0000000) as f32).to_bits()),
        ),
        (
            "Narrow8",
            u64::from((f64::from_bits(0x3ff0000010000000) as f32).to_bits()),
        ),
        (
            "Narrow9",
            u64::from((f64::from_bits(0x3ff0000010000001) as f32).to_bits()),
        ),
        (
            "Narrow10",
            u64::from((f64::from_bits(0x3ff0000030000000) as f32).to_bits()),
        ),
        (
            "Narrow11",
            u64::from((f64::from_bits(0x47effffff0000000) as f32).to_bits()),
        ),
        (
            "Narrow12",
            u64::from((f64::from_bits(0x8000000000000000) as f32).to_bits()),
        ),
        (
            "Narrow13",
            u64::from((f64::from_bits(0x7ff0000000000000) as f32).to_bits()),
        ),
        ("WidenNan", 0x7ff8000000000000),
        ("NarrowNan", 0x7fc00000),
        ("RawWidenTiny", (f64::from(f32::from_bits(1))).to_bits()),
        ("RawMultiplyTiny", 1),
        ("Multiplication", 6.5f64.to_bits()),
        ("MultiplyTiny", 1),
        ("MultiplyHalf", 0),
        ("MultiplyOddHalf", 2),
        ("MultiplyTransition", 0x0010000000000000),
        ("MultiplyFraction", (0.1f64 * 0.1).to_bits()),
        (
            "MultiplyCarry",
            (f64::from_bits(0x3fffffffffffffff) * f64::from_bits(0x3ff0000000000001)).to_bits(),
        ),
        (
            "MultiplyBothCarries",
            (f64::from_bits(0x3fffffffffffffff) * f64::from_bits(0x3fffffffffffffff)).to_bits(),
        ),
        ("MultiplyOverflow", 0x7ff0000000000000),
        ("MultiplyZero", 0x8000000000000000),
        ("MultiplyInfinity", 0xfff0000000000000),
        ("MultiplyInvalid", 0x7ff8000000000000),
        ("MultiplyNan", 0x7ff8000000000000),
        ("OriginalAdd", 4.75f64.to_bits()),
        ("OriginalSubtract", 3.5f64.to_bits()),
        ("RawDivideTiny", 1),
        ("Division", 3.5f64.to_bits()),
        ("DivideThird", (1.0f64 / 3.0).to_bits()),
        ("DivideTiny", 1),
        ("DivideHalf", 0),
        ("DivideOddHalf", 2),
        ("DivideTransition", 0x0010000000000000),
        ("DivideOverflow", 0x7ff0000000000000),
        ("DivideZero", 0x8000000000000000),
        ("DivideInfinity", 0xfff0000000000000),
        ("DivideInvalid", 0x7ff8000000000000),
        ("DivideNan", 0x7ff8000000000000),
        ("DivideUnderflowTie", 0),
        ("DivideUnderflowAbove", 1),
        ("DivideUnderflowBelow", 0),
        ("DivideNegativeHalf", 0x8000000000000000),
        ("DivideByInfinity", 0x8000000000000000),
        ("DivideInfiniteInvalid", 0x7ff8000000000000),
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
        ("UnsupportedConversion", "conversion"),
        ("UnsupportedUnsigned32", "conv.r.un"),
        ("UnsupportedUnsigned64", "conv.r.un"),
        ("UnsupportedRemainder", "f64.rem"),
        ("UnsupportedInteger", "conversion"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Float64::{entry}")))
            .err()
            .unwrap();
        assert!(error.contains("IL_") && error.contains(expected), "{error}");
    }
    for (key, entry) in [
        ("f64.add", "Main"),
        ("f64.cmp", "CompareNan"),
        ("f64.div", "Division"),
        ("f64.mul", "Multiplication"),
        ("f32.toF64", "Widen0"),
        ("f64.toF32", "Narrow0"),
    ] {
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
    for (name, entry, expected, returns) in [
        ("ToF64", "Widen0", "ulong(uint)", 11u8),
        ("ToF32", "Narrow0", "uint(ulong)", 9),
    ] {
        let mut changed = images[1].clone();
        let name = format!("{name}\0");
        let offsets: Vec<_> = changed
            .windows(name.len())
            .enumerate()
            .filter(|(_, bytes)| *bytes == name.as_bytes())
            .map(|(i, _)| i)
            .collect();
        assert_eq!(offsets.len(), 1);
        changed[offsets[0]] = b'Z';
        let renamed = Resolver::new(&[&images[0], &changed]).unwrap();
        compile::object(&renamed, Some(&format!("Float64::{entry}"))).unwrap();
        let method = runtime
            .metadata()
            .method_defs
            .iter()
            .enumerate()
            .find(|(i, _)| {
                runtime
                    .method_name(0x06000001 + *i as u32)
                    .unwrap()
                    .ends_with(name.trim_end_matches('\0'))
            })
            .unwrap()
            .1;
        let start = heap_offset(&images[1], "#Blob") + method.signature as usize;
        assert_eq!(&images[1][start..start + 4], &[4, 0, 1, returns]);
        let mut changed = images[1].clone();
        changed[start + 3] = returns - 1;
        let wrong = Resolver::new(&[&images[0], &changed]).unwrap();
        let error = compile::object(&wrong, Some(&format!("Float64::{entry}"))).unwrap_err();
        assert!(error.contains(expected) && error.contains("IL_"), "{error}");
    }
}
