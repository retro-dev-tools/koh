use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

#[rstest]
#[case("Debug")]
#[case("Release")]
fn unused_delegate_type_arguments_still_validate_the_runtime_contract(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "ForeignCallbacks");
    let mut library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let framework = super::delegates::framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework]).unwrap();
    let data = resolver.assembly(2).unwrap().metadata();
    let owner = data
        .type_defs
        .iter()
        .position(|ty| data.strings.get(ty.type_name).unwrap() == "ForeignFactory`1")
        .unwrap();
    let start = data.type_defs[owner].method_list;
    let end = data
        .type_defs
        .get(owner + 1)
        .map_or(data.method_defs.len() as u32 + 1, |ty| ty.method_list);
    let constructor = (start..end)
        .find(|&row| {
            data.strings
                .get(data.method_defs[row as usize - 1].name)
                .unwrap()
                == ".ctor"
        })
        .unwrap();
    let (offset, _) = metadata::table_row(
        &library,
        clrmeta::TableId::MethodDef,
        constructor as usize - 1,
    );
    library[offset + 4..offset + 6].fill(0);
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework]).unwrap();
    let error = compile::object(&resolver, Some("Program::UnusedFactory")).unwrap_err();
    assert!(
        error.contains("Invalid delegate .ctor runtime method contract") && error.contains("IL_"),
        "{error}"
    );
    let object = compile::object(&resolver, Some("Program::LocalReference")).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let text = execute_mode(
        host,
        directory.path(),
        &linked,
        "__koh_exit",
        "--native-heap",
    );
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
    assert_heap_snapshot(
        &fields[4][0xf00 * 2..],
        &[0x5a; 256],
        "valid after constructor rejection",
    );
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn foreign_callback_bindings_enforce_nominal_types_and_constraints(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "ForeignCallbacks");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let framework = super::delegates::framework_image(directory.path(), configuration);
    let missing = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    let error = compile::object(&missing, Some("Program::LocalValue")).unwrap_err();
    assert!(
        error.contains("Missing assembly GenericLibrary") && error.contains("IL_"),
        "{error}"
    );
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let method = |name| {
        (1..=image.metadata().method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap()
    };
    for (entry, original, replacement, expected) in [
        (
            "Program::LocalValue",
            "Program::Increment",
            "Program::IncrementRemote",
            "Delegate target signature differs from Invoke",
        ),
        (
            "Program::Enum",
            "Program::IdentityCode",
            "Program::IdentityLong",
            "Delegate target parameter type differs from Invoke",
        ),
    ] {
        let (changed, target) = super::delegates::replace_method_token(
            image,
            &images[0],
            method(entry),
            0xfe06,
            method(replacement),
        );
        assert_eq!(target, method(original));
        let changed = Resolver::new(&[&changed, &images[1], &library, &framework]).unwrap();
        let error = compile::object(&changed, Some(entry)).unwrap_err();
        assert!(error.contains(expected) && error.contains("IL_"), "{error}");
    }
    let data = resolver.assembly(2).unwrap().metadata();
    for (name, flags, entry, expected, valid) in [
        (
            "ForeignReferenceCallback`1",
            8u16,
            "Program::Constraint",
            "special constraints",
            "Program::Factory",
        ),
        (
            "ForeignFactory`1",
            2,
            "Program::Factory",
            "Invalid variance position",
            "Program::LocalValue",
        ),
    ] {
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
        let mut bytes = library.clone();
        let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::GenericParam, parameter);
        bytes[offset + 2..offset + 4].copy_from_slice(&flags.to_le_bytes());
        let changed = Resolver::new(&[&images[0], &images[1], &bytes, &framework]).unwrap();
        let error = compile::object(&changed, Some(entry)).unwrap_err();
        assert!(
            error.contains(expected) && error.contains("IL_"),
            "{name}: {error}"
        );
        let object = compile::object(&changed, Some(valid)).unwrap();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(&[object], &Default::default()).unwrap();
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{valid}");
        let mut packet = vec![0x5a; 256];
        if valid.ends_with("LocalValue") {
            packet[..4].copy_from_slice(&42i32.to_le_bytes());
            packet[4..12].copy_from_slice(&0x123456780000002Au64.to_le_bytes());
        }
        assert_heap_snapshot(&fields[4][0xf00 * 2..], &packet, valid);
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn callbacks_preserve_foreign_argument_scopes(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ForeignCallbacks");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let framework = super::delegates::framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework]).unwrap();
    let root_pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/ForeignCallbacks.pdb")),
    )
    .unwrap();
    let library_pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.pdb")),
    )
    .unwrap();
    for entry in [
        "LocalValue",
        "LocalReference",
        "RemoteValue",
        "Enum",
        "Arrays",
        "Nested",
        "Captured",
        "MethodScopes",
        "Constraint",
        "Factory",
        "UnusedFactory",
    ] {
        let object = compile::object_with_debug(
            &resolver,
            Some(&format!("Program::{entry}")),
            &[Some(&root_pdb), None, Some(&library_pdb), None],
        )
        .unwrap_or_else(|e| panic!("{entry}: {e}"));
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
        assert!(
            linked
                .lines
                .iter()
                .any(|line| line.file.ends_with("Program.cs")),
            "{entry}"
        );
        let library_body = !matches!(entry, "RemoteValue" | "Enum" | "Factory" | "UnusedFactory");
        assert_eq!(
            linked
                .lines
                .iter()
                .any(|line| line.file.ends_with("Library.cs")),
            library_body,
            "{entry}: reached library bodies only"
        );
        assert!(linked.kdbg().unwrap().starts_with(b"KDBG"));
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut expected = vec![0x5a; 256];
        if matches!(
            entry,
            "LocalValue" | "RemoteValue" | "Arrays" | "Nested" | "MethodScopes"
        ) {
            expected[..4].copy_from_slice(&42i32.to_le_bytes());
            expected[4..12].copy_from_slice(&0x123456780000002Au64.to_le_bytes());
        } else if entry == "Enum" {
            expected[..8].copy_from_slice(&5000000001u64.to_le_bytes());
        }
        assert_heap_snapshot(&fields[4][0xf00 * 2..], &expected, entry);
    }
}
