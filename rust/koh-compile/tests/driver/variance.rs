use super::{Host, dotnet, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn allow_ref_like_accepts_ordinary_arguments(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Variance");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "Int",
        "Long",
        "Value",
        "ClosedValue",
        "Reference",
        "Array",
        "String",
        "Class",
    ] {
        let object = compile::object(&resolver, Some(&format!("AntiRules::{entry}"))).unwrap();
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
    }
    for entry in ["RefMethod", "RefType", "ClosedRefMethod"] {
        let error = compile::object(&resolver, Some(&format!("AntiRules::{entry}"))).unwrap_err();
        assert!(
            error.contains("Byref-like generic arguments require ref-safety validation")
                && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
    let data = resolver.assembly(0).unwrap().metadata();
    let echo = data
        .method_defs
        .iter()
        .position(|m| data.strings.get(m.name).unwrap() == "Echo")
        .unwrap() as u32
        + 1;
    let parameter = data
        .generic_params
        .iter()
        .position(|p| p.owner.table == Some(clrmeta::TableId::MethodDef) && p.owner.row == echo)
        .unwrap();
    let mut bytes = images[0].clone();
    let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::GenericParam, parameter);
    bytes[offset + 2..offset + 4].copy_from_slice(&0u16.to_le_bytes());
    let changed = Resolver::new(&[&bytes, &images[1]]).unwrap();
    let error = compile::object(&changed, Some("AntiRules::RefMethod")).unwrap_err();
    assert!(
        error.contains("Byref-like generic argument requires AllowByRefLike")
            && error.contains("IL_"),
        "{error}"
    );
    let owner = data
        .type_defs
        .iter()
        .position(|r| data.strings.get(r.type_name).unwrap() == "StackValue")
        .unwrap() as u32
        + 1;
    let marker = data
        .custom_attributes
        .iter()
        .position(|a| {
            if a.parent.table != Some(clrmeta::TableId::TypeDef)
                || a.parent.row != owner
                || a.attr_type.table != Some(clrmeta::TableId::MemberRef)
            {
                return false;
            }
            let ctor = &data.member_refs[a.attr_type.row as usize - 1];
            ctor.class.table == Some(clrmeta::TableId::TypeRef)
                && data
                    .strings
                    .get(data.type_refs[ctor.class.row as usize - 1].type_name)
                    .unwrap()
                    == "IsByRefLikeAttribute"
        })
        .unwrap();
    let heap = data.blobs.data();
    let locations: Vec<_> = images[0]
        .windows(heap.len())
        .enumerate()
        .filter_map(|(offset, value)| (value == heap).then_some(offset))
        .collect();
    assert_eq!(locations.len(), 1);
    let blob = data.custom_attributes[marker].value as usize;
    assert_eq!(heap[blob], 4);
    for payload in [[0u8, 0, 0, 0], [1, 0, 1, 0]] {
        let mut bytes = images[0].clone();
        bytes[locations[0] + blob + 1..locations[0] + blob + 5].copy_from_slice(&payload);
        let changed = Resolver::new(&[&bytes, &images[1]]).unwrap();
        let error = compile::object(&changed, Some("AntiRules::RefMethod")).unwrap_err();
        assert!(
            error.contains("Invalid IsByRefLike attribute contract") && error.contains("IL_"),
            "{error}"
        );
    }
    let other = data
        .custom_attributes
        .iter()
        .enumerate()
        .position(|(i, a)| {
            i != marker
                && a.parent.table == Some(clrmeta::TableId::TypeDef)
                && a.parent.row == owner
        })
        .unwrap();
    let mut bytes = images[0].clone();
    let (source, context) = metadata::table_row(&bytes, clrmeta::TableId::CustomAttribute, marker);
    let (target, _) = metadata::table_row(&bytes, clrmeta::TableId::CustomAttribute, other);
    let size = context.row_size(clrmeta::TableId::CustomAttribute);
    bytes.copy_within(source..source + size, target);
    let changed = Resolver::new(&[&bytes, &images[1]]).unwrap();
    let error = compile::object(&changed, Some("AntiRules::RefMethod")).unwrap_err();
    assert!(
        error.contains("Invalid IsByRefLike attribute contract") && error.contains("IL_"),
        "{error}"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn exact_variant_interface_calls_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Variance");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "ExactProducer",
        "ExactIntProducer",
        "ExactConsumer",
        "ComplexShape",
        "StaticShape",
        "RefShape",
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
    }
    let data = resolver.assembly(0).unwrap().metadata();
    for (name, entry, flags) in [
        ("IRef`1", "RefShape", vec![1u16, 2]),
        ("IStatic`1", "StaticShape", vec![2u16]),
    ] {
        let owner = data
            .type_defs
            .iter()
            .position(|r| data.strings.get(r.type_name).unwrap() == name)
            .unwrap() as u32
            + 1;
        let parameter = data
            .generic_params
            .iter()
            .position(|p| p.owner.table == Some(clrmeta::TableId::TypeDef) && p.owner.row == owner)
            .unwrap();
        for variance in flags {
            let mut bytes = images[0].clone();
            let (offset, _) =
                metadata::table_row(&bytes, clrmeta::TableId::GenericParam, parameter);
            bytes[offset + 2..offset + 4].copy_from_slice(&variance.to_le_bytes());
            let changed = Resolver::new(&[&bytes, &images[1]]).unwrap();
            let error = compile::object(&changed, Some(&format!("Program::{entry}"))).unwrap_err();
            assert!(
                error.contains("Invalid variance position") && error.contains("IL_"),
                "{error}"
            );
        }
    }
    for (entry, diagnostic) in [
        (
            "CovariantConversion",
            "Variant interface conversion requires variance-aware binding",
        ),
        ("CovariantTypeTest", "variance-aware runtime tests"),
        ("CovariantCast", "variance-aware runtime tests"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(diagnostic) && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
}

#[rstest]
#[case("Debug", "IProducer`1")]
#[case("Release", "IProducer`1")]
#[case("Debug", "IConsumer`1")]
#[case("Release", "IConsumer`1")]
fn exact_variant_interface_shapes_validate_flags_and_positions(
    host: &Host,
    #[case] configuration: &str,
    #[case] name: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "Variance");
    let desktop = dotnet()
        .arg(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/Variance.dll")),
        )
        .output()
        .unwrap();
    assert!(
        desktop.status.success(),
        "{}",
        String::from_utf8_lossy(&desktop.stderr)
    );
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let owner = data
        .type_defs
        .iter()
        .position(|r| data.strings.get(r.type_name).unwrap() == name)
        .unwrap() as u32
        + 1;
    let parameter = data
        .generic_params
        .iter()
        .position(|r| r.owner.table == Some(clrmeta::TableId::TypeDef) && r.owner.row == owner)
        .unwrap();
    let entry = format!("Program::{}Shape", name.trim_end_matches("`1"));
    let original = data.generic_params[parameter].flags;
    let reference_variance = original | 4;
    for flags in [
        0u16,
        original,
        reference_variance,
        if original == 1 { 2 } else { 1 },
        3,
        0x0c,
        0x0d,
        0x0e,
        0x40,
        0x41,
    ] {
        let mut changed = images[0].clone();
        let (offset, _) = metadata::table_row(&changed, clrmeta::TableId::GenericParam, parameter);
        changed[offset + 2..offset + 4].copy_from_slice(&flags.to_le_bytes());
        let resolver = Resolver::new(&[&changed, &images[1]]).unwrap();
        let result = compile::object(&resolver, Some(&entry));
        if flags == 0 || flags == original || flags == reference_variance {
            let object = result.unwrap();
            let linked = koh_link::link(&[object], &Default::default()).unwrap();
            let report = execute_mode(
                host,
                directory.path(),
                &linked,
                "__koh_exit",
                "--native-heap",
            );
            let fields: Vec<_> = report.split_whitespace().collect();
            assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
            super::assert_heap_snapshot(fields[4], &[0x5a; 4096], &entry);
        } else {
            let error = result.unwrap_err();
            let expected = if flags == 1 || flags == 2 {
                "Invalid variance position"
            } else {
                "Invalid generic type parameter flags"
            };
            assert!(
                error.contains(expected) && error.contains("IL_"),
                "{name} {flags}: {error}"
            );
        }
    }
    let error = compile::object(&resolver, Some("Program::Boxed")).unwrap_err();
    assert!(
        error.contains("opcode 0x008c")
            && error.contains("Program::Boxed")
            && error.contains("IL_"),
        "{error}"
    );
}

#[rstest]
#[case("Debug", "Producer`1")]
#[case("Release", "Producer`1")]
#[case("Debug", "Consumer`1")]
#[case("Release", "Consumer`1")]
fn variant_delegates_accept_valid_flags_and_reject_malformed_variance(
    host: &Host,
    #[case] configuration: &str,
    #[case] name: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "Variance");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let owner = data
        .type_defs
        .iter()
        .position(|ty| data.strings.get(ty.type_name).unwrap() == name)
        .unwrap() as u32
        + 1;
    let parameter = data
        .generic_params
        .iter()
        .position(|p| p.owner.table == Some(clrmeta::TableId::TypeDef) && p.owner.row == owner)
        .unwrap();
    let entry = format!("Program::{}Call", name.trim_end_matches("`1"));
    for flags in [
        data.generic_params[parameter].flags,
        0,
        3,
        0x0c,
        0x0d,
        0x0e,
        0x40,
        0x41,
    ] {
        let mut bytes = images[0].clone();
        let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::GenericParam, parameter);
        bytes[offset + 2..offset + 4].copy_from_slice(&flags.to_le_bytes());
        let changed = Resolver::new(&[&bytes, &images[1]]).unwrap();
        let result = compile::object(&changed, Some(&entry));
        if flags == 0 || flags == data.generic_params[parameter].flags {
            let object = result.unwrap_or_else(|error| panic!("{name} flags{flags}: {error}"));
            let linked = koh_link::link(&[object], &Default::default()).unwrap();
            let report = execute_mode(
                host,
                directory.path(),
                &linked,
                "__koh_exit",
                "--native-heap",
            );
            let fields: Vec<_> = report.split_whitespace().collect();
            assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
            assert_eq!(fields[4].len(), 8192);
            let descriptor: Vec<_> = fields[4][..16]
                .as_bytes()
                .chunks_exact(2)
                .map(|hex| u8::from_str_radix(std::str::from_utf8(hex).unwrap(), 16).unwrap())
                .collect();
            assert_eq!(&descriptor[..2], &[6, 0]);
            assert_eq!(&descriptor[6..], &[0, 0]);
            let address = u16::from_le_bytes([descriptor[2], descriptor[3]]);
            let bank = u16::from_le_bytes([descriptor[4], descriptor[5]]);
            assert!(
                linked
                    .symbols
                    .iter()
                    .any(|symbol| symbol.name.starts_with("__koh_a0_m")
                        && symbol.value == i32::from(address)
                        && symbol.bank.unwrap_or(0) == u32::from(bank)),
                "Callback code/bank must identify linked code"
            );
            super::assert_heap_snapshot(&fields[4][16..], &[0x5a; 4088], &entry);
        } else {
            let error = result
                .err()
                .expect("Invalid generic delegate flags accepted");
            assert!(
                error.contains("Invalid delegate generic parameter flags")
                    && error.contains(&entry)
                    && error.contains("IL_"),
                "{name} flags{flags}: {error}"
            );
        }
    }
    let entry = format!("Program::{}Shape", name.trim_end_matches("`1"));
    let error = compile::object(&resolver, Some(&entry)).unwrap_err();
    assert!(
        error.contains("Compound generic arguments require instantiated type binding")
            && error.contains(&entry)
            && error.contains("IL_"),
        "{error}"
    );
}
