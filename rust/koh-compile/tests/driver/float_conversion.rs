use super::{Host, execute, float32::heap_offset, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

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
    for (entry, text) in [
        ("WideUnsupported", "64-bit"),
        ("WideOutputUnsupported", "64-bit"),
        ("CheckedUnsupported", "0x00b7"),
    ] {
        let error = compile::object(&resolver, Some(&format!("FloatConversions::{entry}")))
            .err()
            .unwrap();
        assert!(error.contains("IL_") && error.contains(text), "{error}");
    }
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
