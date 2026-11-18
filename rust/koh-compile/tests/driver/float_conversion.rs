use super::{Host, execute, execute_mode, float32::heap_offset, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

fn link_entry(resolver: &Resolver<'_>, entry: &str, color: bool) -> koh_link::LinkResult {
    let mut object = compile::object(resolver, Some(&format!("FloatConversions::{entry}")))
        .unwrap_or_else(|error| panic!("{entry}: {error}"));
    let mut bank = 1;
    for section in &mut object.sections {
        if section.name.starts_with(".text.__koh_a") {
            section.bank = Some(bank);
            bank += 1;
        }
    }
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    koh_link::link(
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
    .unwrap()
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn wide_float_conversions_round_once_and_preserve_all_return_bytes(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FloatConversions");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, expected) in [
        ("LongMin", 0xdf000000u32),
        ("LongMax", 0x5f000000),
        ("ULongMax", 0x5f800000),
        ("ULongHalfBelow", 0x5f000000),
        ("ULongHalfExact", 0x5f000000),
        ("ULongHalfAbove", 0x5f000001),
        ("LongNegative", 0xcb800000),
        ("ULongRounded", 0x4b800002),
    ] {
        let linked = link_entry(&resolver, entry, color);
        assert_eq!(
            execute(host, directory.path(), &linked, "__koh_exit"),
            format!("00 {expected:08X} D000 OK"),
            "{entry}"
        );
    }
    for (entry, expected) in [
        ("LongPositive", 3u64),
        ("LongNegativeResult", (-3i64) as u64),
        ("LongLimit", i64::MAX as u64),
        ("LongMinResult", i64::MIN as u64),
        ("LongBelowMin", i64::MIN as u64),
        ("ULongLimit", u64::MAX),
        ("ULongLargest", 0xffffff0000000000),
        ("LongLargest", 0x7fffff8000000000),
        ("ULongNegative", 0),
        ("LongNan", 0),
        ("LongNegativeNan", 0),
        ("LongInfinity", i64::MAX as u64),
        ("LongNegativeInfinity", i64::MIN as u64),
        ("LongSubnormal", 0),
        ("ULongInfinity", u64::MAX),
        ("ULongNegativeInfinity", 0),
    ] {
        let linked = link_entry(&resolver, entry, color);
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
        heap[..8].copy_from_slice(&expected.to_le_bytes());
        super::assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for (key, entry) in [("u64.toF32", "ULongMax"), ("f32.toI64", "LongPositive")] {
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
        let error = compile::object(&resolver, Some(&format!("FloatConversions::{entry}")))
            .err()
            .unwrap();
        assert!(
            error.contains(&format!("Missing KohRuntime {key}")) && error.contains("IL_"),
            "{error}"
        );
    }
    let runtime = resolver.assembly(1).unwrap();
    for (name, entry, signature, index, replacement, expected) in [
        (
            "FromU64F32",
            "ULongMax",
            [4, 0, 1, 9, 11],
            4,
            10,
            "uint(ulong)",
        ),
        (
            "ToI64F32",
            "LongPositive",
            [4, 0, 1, 10, 9],
            3,
            11,
            "long(uint)",
        ),
    ] {
        let method = runtime
            .metadata()
            .method_defs
            .iter()
            .enumerate()
            .find(|(i, _)| {
                runtime.method_name(0x06000001 + *i as u32).unwrap()
                    == format!("Koh.GameBoy.SoftFloat::{name}")
            })
            .unwrap()
            .1;
        let start = heap_offset(&images[1], "#Blob") + method.signature as usize;
        assert_eq!(&images[1][start..start + 5], &signature);
        let mut changed = images[1].clone();
        changed[start + index] = replacement;
        let resolver = Resolver::new(&[&images[0], &changed]).unwrap();
        let error = compile::object(&resolver, Some(&format!("FloatConversions::{entry}")))
            .err()
            .unwrap();
        assert!(error.contains(expected) && error.contains("IL_"), "{error}");
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn roslyn_float_integer_conversions_preserve_width_and_signedness(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, mut images) = gameboy_images(host, configuration, "FloatConversions");
    let runtime = koh_compile::assembly::Assembly::parse(&images[1]).unwrap();
    let method = runtime
        .metadata()
        .method_defs
        .iter()
        .enumerate()
        .find(|(i, _)| {
            runtime
                .method_name(0x06000001 + *i as u32)
                .unwrap()
                .ends_with("SoftFloat::FromI32")
        })
        .unwrap()
        .1;
    let name = heap_offset(&images[1], "#Strings") + method.name as usize;
    assert_eq!(&images[1][name..name + 7], b"FromI32");
    images[1][name..name + 7].copy_from_slice(b"CastI32");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, expected) in [
        ("Main", 0x4b800000u32),
        ("FromZero", 0),
        ("FromFive", 0x40a00000),
        ("FromNegative", 0xc0a00000),
        ("FromMillion", 0x49742400),
        ("FromMin", 0xcf000000),
        ("FromMax", 0x4f000000),
        ("UnsignedMax", 0x4f800000),
        ("NativeNegative", 0xc0000000),
        ("NativeUnsigned", 0x477fff00),
        ("Positive", 3),
        ("Negative", 0xfffffffd),
        ("Thousand", 1000),
        ("Nan", 0),
        ("NegativeNan", 0),
        ("Infinity", 0x7fffffff),
        ("NegativeInfinity", 0x80000000),
        ("SignedLimit", 0x7fffffff),
        ("UnsignedLimit", 0xffffffff),
        ("UnsignedLargest", 0xffffff00),
        ("UnsignedNegative", 0),
        ("SmallWrap", 0xffffff82),
        ("ByteWrap", 44),
        ("NativeWrap", 0xffff8000),
        ("WideInput", 0x4b800000),
        ("WideOutput", 1),
    ] {
        let entry = format!("FloatConversions::{entry}");
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
    let linked = link_entry(&resolver, "CheckedPositive", color);
    assert_eq!(
        execute(host, directory.path(), &linked, "__koh_exit"),
        "00 00000003 D000 OK"
    );
    for (key, entry) in [("i32.toF32", "Main"), ("f32.toI32", "Positive")] {
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
        let error = compile::object(&resolver, Some(&format!("FloatConversions::{entry}")))
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
                .ends_with("SoftFloat::CastI32")
        })
        .unwrap()
        .1;
    let start = heap_offset(&images[1], "#Blob") + method.signature as usize;
    assert_eq!(&images[1][start..start + 5], &[4, 0, 1, 9, 8]);
    let mut changed = images[1].clone();
    changed[start + 4] = 9;
    let resolver = Resolver::new(&[&images[0], &changed]).unwrap();
    let error = compile::object(&resolver, Some("FloatConversions::Main"))
        .err()
        .unwrap();
    assert!(
        error.contains("uint(int)") && error.contains("IL_"),
        "{error}"
    );
}
