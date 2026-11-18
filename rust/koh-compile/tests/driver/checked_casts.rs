use super::{Host, execute_mode, float32::heap_offset, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn checked_casts_preserve_values_or_fault_before_observable_writes(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "CheckedCasts");
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/CheckedCasts.pdb")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, fault, expected) in [
        ("I0long0", 0u8, 0xffffffffffffff80u64),
        ("I0long1", 0u8, 0x000000000000007fu64),
        ("I0long2", 2u8, 0x0000000000000000u64),
        ("I0long3", 2u8, 0x0000000000000000u64),
        ("I0ulong0", 0u8, 0x0000000000000000u64),
        ("I0ulong1", 0u8, 0x000000000000007fu64),
        ("I0ulong2", 2u8, 0x0000000000000000u64),
        ("F0float0", 0u8, 0x0000000000000003u64),
        ("F0float1", 0u8, 0x0000000000000000u64),
        ("F0float2", 0u8, 0xffffffffffffff80u64),
        ("F0float3", 2u8, 0x0000000000000000u64),
        ("F0float4", 0u8, 0xffffffffffffff80u64),
        ("F0float5", 2u8, 0x0000000000000000u64),
        ("F0float6", 0u8, 0x000000000000007fu64),
        ("F0double0", 0u8, 0x0000000000000003u64),
        ("F0double1", 0u8, 0x0000000000000000u64),
        ("F0double2", 0u8, 0xffffffffffffff80u64),
        ("F0double3", 2u8, 0x0000000000000000u64),
        ("F0double4", 0u8, 0xffffffffffffff80u64),
        ("F0double5", 2u8, 0x0000000000000000u64),
        ("F0double6", 0u8, 0x000000000000007fu64),
        ("I1long0", 0u8, 0x0000000000000000u64),
        ("I1long1", 0u8, 0x00000000000000ffu64),
        ("I1long2", 2u8, 0x0000000000000000u64),
        ("I1long3", 2u8, 0x0000000000000000u64),
        ("I1ulong0", 0u8, 0x0000000000000000u64),
        ("I1ulong1", 0u8, 0x00000000000000ffu64),
        ("I1ulong2", 2u8, 0x0000000000000000u64),
        ("F1float0", 0u8, 0x0000000000000003u64),
        ("F1float1", 0u8, 0x0000000000000000u64),
        ("F1float2", 0u8, 0x0000000000000000u64),
        ("F1float3", 2u8, 0x0000000000000000u64),
        ("F1float4", 0u8, 0x0000000000000000u64),
        ("F1float5", 2u8, 0x0000000000000000u64),
        ("F1float6", 0u8, 0x00000000000000ffu64),
        ("F1double0", 0u8, 0x0000000000000003u64),
        ("F1double1", 0u8, 0x0000000000000000u64),
        ("F1double2", 0u8, 0x0000000000000000u64),
        ("F1double3", 2u8, 0x0000000000000000u64),
        ("F1double4", 0u8, 0x0000000000000000u64),
        ("F1double5", 2u8, 0x0000000000000000u64),
        ("F1double6", 0u8, 0x00000000000000ffu64),
        ("I2long0", 0u8, 0xffffffffffff8000u64),
        ("I2long1", 0u8, 0x0000000000007fffu64),
        ("I2long2", 2u8, 0x0000000000000000u64),
        ("I2long3", 2u8, 0x0000000000000000u64),
        ("I2ulong0", 0u8, 0x0000000000000000u64),
        ("I2ulong1", 0u8, 0x0000000000007fffu64),
        ("I2ulong2", 2u8, 0x0000000000000000u64),
        ("F2float0", 0u8, 0x0000000000000003u64),
        ("F2float1", 0u8, 0x0000000000000000u64),
        ("F2float2", 0u8, 0xffffffffffff8000u64),
        ("F2float3", 2u8, 0x0000000000000000u64),
        ("F2float4", 0u8, 0xffffffffffff8000u64),
        ("F2float5", 2u8, 0x0000000000000000u64),
        ("F2float6", 0u8, 0x0000000000007fffu64),
        ("F2double0", 0u8, 0x0000000000000003u64),
        ("F2double1", 0u8, 0x0000000000000000u64),
        ("F2double2", 0u8, 0xffffffffffff8000u64),
        ("F2double3", 2u8, 0x0000000000000000u64),
        ("F2double4", 0u8, 0xffffffffffff8000u64),
        ("F2double5", 2u8, 0x0000000000000000u64),
        ("F2double6", 0u8, 0x0000000000007fffu64),
        ("I3long0", 0u8, 0x0000000000000000u64),
        ("I3long1", 0u8, 0x000000000000ffffu64),
        ("I3long2", 2u8, 0x0000000000000000u64),
        ("I3long3", 2u8, 0x0000000000000000u64),
        ("I3ulong0", 0u8, 0x0000000000000000u64),
        ("I3ulong1", 0u8, 0x000000000000ffffu64),
        ("I3ulong2", 2u8, 0x0000000000000000u64),
        ("F3float0", 0u8, 0x0000000000000003u64),
        ("F3float1", 0u8, 0x0000000000000000u64),
        ("F3float2", 0u8, 0x0000000000000000u64),
        ("F3float3", 2u8, 0x0000000000000000u64),
        ("F3float4", 0u8, 0x0000000000000000u64),
        ("F3float5", 2u8, 0x0000000000000000u64),
        ("F3float6", 0u8, 0x000000000000ffffu64),
        ("F3double0", 0u8, 0x0000000000000003u64),
        ("F3double1", 0u8, 0x0000000000000000u64),
        ("F3double2", 0u8, 0x0000000000000000u64),
        ("F3double3", 2u8, 0x0000000000000000u64),
        ("F3double4", 0u8, 0x0000000000000000u64),
        ("F3double5", 2u8, 0x0000000000000000u64),
        ("F3double6", 0u8, 0x000000000000ffffu64),
        ("I4long0", 0u8, 0xffffffff80000000u64),
        ("I4long1", 0u8, 0x000000007fffffffu64),
        ("I4long2", 2u8, 0x0000000000000000u64),
        ("I4long3", 2u8, 0x0000000000000000u64),
        ("I4ulong0", 0u8, 0x0000000000000000u64),
        ("I4ulong1", 0u8, 0x000000007fffffffu64),
        ("I4ulong2", 2u8, 0x0000000000000000u64),
        ("F4float0", 0u8, 0x0000000000000003u64),
        ("F4float1", 0u8, 0x0000000000000000u64),
        ("F4float2", 0u8, 0xffffffff80000000u64),
        ("F4float3", 2u8, 0x0000000000000000u64),
        ("F4double0", 0u8, 0x0000000000000003u64),
        ("F4double1", 0u8, 0x0000000000000000u64),
        ("F4double2", 0u8, 0xffffffff80000000u64),
        ("F4double3", 2u8, 0x0000000000000000u64),
        ("F4double4", 0u8, 0xffffffff80000000u64),
        ("F4double5", 2u8, 0x0000000000000000u64),
        ("F4double6", 0u8, 0x000000007fffffffu64),
        ("I5long0", 0u8, 0x0000000000000000u64),
        ("I5long1", 0u8, 0x00000000ffffffffu64),
        ("I5long2", 2u8, 0x0000000000000000u64),
        ("I5long3", 2u8, 0x0000000000000000u64),
        ("I5ulong0", 0u8, 0x0000000000000000u64),
        ("I5ulong1", 0u8, 0x00000000ffffffffu64),
        ("I5ulong2", 2u8, 0x0000000000000000u64),
        ("F5float0", 0u8, 0x0000000000000003u64),
        ("F5float1", 0u8, 0x0000000000000000u64),
        ("F5float2", 0u8, 0x0000000000000000u64),
        ("F5float3", 2u8, 0x0000000000000000u64),
        ("F5float4", 0u8, 0x0000000000000000u64),
        ("F5float5", 2u8, 0x0000000000000000u64),
        ("F5double0", 0u8, 0x0000000000000003u64),
        ("F5double1", 0u8, 0x0000000000000000u64),
        ("F5double2", 0u8, 0x0000000000000000u64),
        ("F5double3", 2u8, 0x0000000000000000u64),
        ("F5double4", 0u8, 0x0000000000000000u64),
        ("F5double5", 2u8, 0x0000000000000000u64),
        ("F5double6", 0u8, 0x00000000ffffffffu64),
        ("I6long0", 0u8, 0x8000000000000000u64),
        ("I6long1", 0u8, 0x7fffffffffffffffu64),
        ("I6ulong0", 0u8, 0x0000000000000000u64),
        ("I6ulong1", 0u8, 0x7fffffffffffffffu64),
        ("I6ulong2", 2u8, 0x0000000000000000u64),
        ("F6float0", 0u8, 0x0000000000000003u64),
        ("F6float1", 0u8, 0x0000000000000000u64),
        ("F6float2", 0u8, 0x8000000000000000u64),
        ("F6float3", 2u8, 0x0000000000000000u64),
        ("F6float4", 2u8, 0x0000000000000000u64),
        ("F6float5", 2u8, 0x0000000000000000u64),
        ("F6float6", 2u8, 0x0000000000000000u64),
        ("F6double0", 0u8, 0x0000000000000003u64),
        ("F6double1", 0u8, 0x0000000000000000u64),
        ("F6double2", 0u8, 0x8000000000000000u64),
        ("F6double3", 2u8, 0x0000000000000000u64),
        ("F6double4", 2u8, 0x0000000000000000u64),
        ("F6double5", 2u8, 0x0000000000000000u64),
        ("F6double6", 2u8, 0x0000000000000000u64),
        ("I7long0", 0u8, 0x0000000000000000u64),
        ("I7long1", 2u8, 0x0000000000000000u64),
        ("I7ulong0", 0u8, 0x0000000000000000u64),
        ("I7ulong1", 0u8, 0xffffffffffffffffu64),
        ("F7float0", 0u8, 0x0000000000000003u64),
        ("F7float1", 0u8, 0x0000000000000000u64),
        ("F7float2", 0u8, 0x0000000000000000u64),
        ("F7float3", 2u8, 0x0000000000000000u64),
        ("F7float4", 2u8, 0x0000000000000000u64),
        ("F7float5", 2u8, 0x0000000000000000u64),
        ("F7float6", 2u8, 0x0000000000000000u64),
        ("F7double0", 0u8, 0x0000000000000003u64),
        ("F7double1", 0u8, 0x0000000000000000u64),
        ("F7double2", 0u8, 0x0000000000000000u64),
        ("F7double3", 2u8, 0x0000000000000000u64),
        ("F7double4", 2u8, 0x0000000000000000u64),
        ("F7double5", 2u8, 0x0000000000000000u64),
        ("F7double6", 2u8, 0x0000000000000000u64),
        ("I8long0", 0u8, 0xffffffffffff8000u64),
        ("I8long1", 0u8, 0x0000000000007fffu64),
        ("I8long2", 2u8, 0x0000000000000000u64),
        ("I8long3", 2u8, 0x0000000000000000u64),
        ("I8ulong0", 0u8, 0x0000000000000000u64),
        ("I8ulong1", 0u8, 0x0000000000007fffu64),
        ("I8ulong2", 2u8, 0x0000000000000000u64),
        ("F8float0", 0u8, 0x0000000000000003u64),
        ("F8float1", 0u8, 0x0000000000000000u64),
        ("F8float2", 0u8, 0xffffffffffff8000u64),
        ("F8float3", 2u8, 0x0000000000000000u64),
        ("F8float4", 0u8, 0xffffffffffff8000u64),
        ("F8float5", 2u8, 0x0000000000000000u64),
        ("F8float6", 0u8, 0x0000000000007fffu64),
        ("F8double0", 0u8, 0x0000000000000003u64),
        ("F8double1", 0u8, 0x0000000000000000u64),
        ("F8double2", 0u8, 0xffffffffffff8000u64),
        ("F8double3", 2u8, 0x0000000000000000u64),
        ("F8double4", 0u8, 0xffffffffffff8000u64),
        ("F8double5", 2u8, 0x0000000000000000u64),
        ("F8double6", 0u8, 0x0000000000007fffu64),
        ("I9long0", 0u8, 0x0000000000000000u64),
        ("I9long1", 0u8, 0x000000000000ffffu64),
        ("I9long2", 2u8, 0x0000000000000000u64),
        ("I9long3", 2u8, 0x0000000000000000u64),
        ("I9ulong0", 0u8, 0x0000000000000000u64),
        ("I9ulong1", 0u8, 0x000000000000ffffu64),
        ("I9ulong2", 2u8, 0x0000000000000000u64),
        ("F9float0", 0u8, 0x0000000000000003u64),
        ("F9float1", 0u8, 0x0000000000000000u64),
        ("F9float2", 0u8, 0x0000000000000000u64),
        ("F9float3", 2u8, 0x0000000000000000u64),
        ("F9float4", 0u8, 0x0000000000000000u64),
        ("F9float5", 2u8, 0x0000000000000000u64),
        ("F9float6", 0u8, 0x000000000000ffffu64),
        ("F9double0", 0u8, 0x0000000000000003u64),
        ("F9double1", 0u8, 0x0000000000000000u64),
        ("F9double2", 0u8, 0x0000000000000000u64),
        ("F9double3", 2u8, 0x0000000000000000u64),
        ("F9double4", 0u8, 0x0000000000000000u64),
        ("F9double5", 2u8, 0x0000000000000000u64),
        ("F9double6", 0u8, 0x000000000000ffffu64),
        ("NativeNegative", 2u8, 0x0000000000000000u64),
        ("NativePositive", 0u8, 0x000000000000007bu64),
        ("UNativePositive", 0u8, 0x000000000000ffffu64),
        ("UIntOverflow", 2u8, 0x0000000000000000u64),
        ("DiscardValid", 0u8, 0x000000000000002au64),
        ("DiscardOverflow", 2u8, 0x0000000000000000u64),
        ("LoopValid", 0u8, 0x0000000000000006u64),
        ("LoopOverflow", 2u8, 0x0000000000000000u64),
    ] {
        println!("{configuration} color={color}: {entry} expected fault={fault}");
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("CheckedCasts::{entry}")),
            &[Some(&pdb), None],
        )
        .unwrap_or_else(|e| panic!("{entry}: {e}"));
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
            if fault == 0 {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 6 } else { 5 }, "{entry}: {report}");
        assert_eq!(fields[0], format!("{fault:02X}"), "{entry}: {report}");
        assert_eq!(fields[3], "OK", "{entry}: frame guards");
        let mut heap = vec![0x5a; 4096];
        if fault == 0 {
            assert_eq!(&fields[1..3], &["0000002A", "D000"], "{entry}");
            heap[4088..].copy_from_slice(&expected.to_le_bytes());
        }
        super::assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for (key, method_name, entry, raw, signature) in [
        (
            "f32.checkInt",
            "CheckIntF32",
            "F0float0",
            9u8,
            "byte(uint,byte,byte)",
        ),
        (
            "f64.checkInt",
            "CheckIntF64",
            "F0double0",
            11u8,
            "byte(ulong,byte,byte)",
        ),
    ] {
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
                    .ends_with(&format!("SoftFloat::{method_name}"))
            })
            .unwrap()
            .1;
        let name = heap_offset(&images[1], "#Strings") + method.name as usize;
        let mut renamed = images[1].clone();
        renamed[name] = b'Z';
        let selected = Resolver::new(&[&images[0], &renamed]).unwrap();
        compile::object(&selected, Some(&format!("CheckedCasts::{entry}"))).unwrap();
        let start = heap_offset(&images[1], "#Blob") + method.signature as usize;
        assert_eq!(&images[1][start..start + 7], &[6, 0, 3, 5, raw, 5, 5]);
        for index in 3..7 {
            let mut changed = images[1].clone();
            changed[start + index] -= 1;
            let wrong = Resolver::new(&[&images[0], &changed]).unwrap();
            let error =
                compile::object(&wrong, Some(&format!("CheckedCasts::{entry}"))).unwrap_err();
            assert!(
                error.contains(signature) && error.contains("IL_"),
                "{error}"
            );
        }
        let mut changed = images[1].clone();
        let offsets: Vec<_> = changed
            .windows(key.len())
            .enumerate()
            .filter(|(_, s)| *s == key.as_bytes())
            .map(|(i, _)| i)
            .collect();
        assert_eq!(offsets.len(), 1);
        changed[offsets[0]] = b'z';
        let missing = Resolver::new(&[&images[0], &changed]).unwrap();
        let error = compile::object(&missing, Some(&format!("CheckedCasts::{entry}"))).unwrap_err();
        assert!(
            error.contains(&format!("Missing KohRuntime {key}")) && error.contains("IL_"),
            "{error}"
        );
    }
}
