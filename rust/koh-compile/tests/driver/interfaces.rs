use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

fn write_index(bytes: &mut [u8], offset: usize, width: usize, value: u32) {
    assert!(width == 4 || value <= u16::MAX as u32);
    bytes[offset..offset + width].copy_from_slice(&value.to_le_bytes()[..width]);
}

#[rstest::fixture]
#[once]
fn managed() -> Host {
    super::support::build_fixture_host("ManagedHost")
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn interface_calls_through_static_storage_execute(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "InterfaceCalls");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/InterfaceLibrary.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    for entry in [
        "Main",
        "LargeFrame",
        "LargeClassFrame",
        "VirtualOverride",
        "HiddenMethod",
        "Reimplementation",
        "ExplicitMapping",
        "ReimplementedExplicit",
        "ReimplementedOnlyExplicit",
        "InheritedPublic",
        "InheritedInterface",
        "RemoteImplicit",
        "RemoteExplicit",
        "RemoteMemberRef",
        "LargeDispatch",
        "WideResult",
        "AggregateResult",
        "GenericInterface",
        "Casts",
        "NonmatchingCast",
        "ClassIsInstance",
        "NullCast",
        "ConditionalFirst",
        "ConditionalSecond",
        "MultipleSlots",
        "BoundInterfaceDelegate",
        "ReferenceStorage",
        "LegacyStorage",
        "LegacyHardware",
        "DefaultOverridden",
        "DefaultInterface",
    ] {
        if entry != "LegacyHardware" {
            let result = managed
                .command()
                .arg(
                    directory
                        .path()
                        .join(format!("bin/{configuration}/net10.0/InterfaceCalls.dll")),
                )
                .arg(entry)
                .output()
                .unwrap();
            assert!(
                result.status.success(),
                "{}",
                String::from_utf8_lossy(&result.stderr)
            );
            assert_eq!(
                String::from_utf8_lossy(&result.stdout).trim(),
                "42",
                "Managed {entry}"
            );
        }
        let mut object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|e| panic!("{entry}: {e}"));
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            if entry == "LegacyHardware" {
                "--native-scroll"
            } else {
                "--native-heap"
            },
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} bank2");
        }
        if entry == "LegacyHardware" {
            assert_eq!(fields.last().copied(), Some("07EE"), "{entry}");
        }
    }
    for (entry, code, exception) in [
        ("InvalidCast", "0A", "System.InvalidCastException"),
        ("NullReceiver", "04", "System.NullReferenceException"),
    ] {
        let result = managed
            .command()
            .arg(
                directory
                    .path()
                    .join(format!("bin/{configuration}/net10.0/InterfaceCalls.dll")),
            )
            .arg(entry)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&result.stdout).trim(),
            format!("THROW:{exception}")
        );
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
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
            "__koh_abort",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields[0], code, "{entry}");
        assert_eq!(fields[3], "OK", "{entry}");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} bank2");
        }
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn interface_metadata_contracts_diagnose_and_inherited_memberref_bodies_execute(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
) {
    use clrmeta::{CodedIndexKind, TableId};
    use koh_compile::assembly::Assembly;
    let (directory, images) = gameboy_images(host, configuration, "InterfaceCalls");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/InterfaceLibrary.dll")),
    )
    .unwrap();
    let assembly = Assembly::parse(&images[0]).unwrap();
    let md = assembly.metadata();
    let owner = |name: &str| {
        md.type_defs
            .iter()
            .position(|t| md.strings.get(t.type_name).unwrap() == name)
            .unwrap() as u32
            + 1
    };
    let methods = |name: &str| {
        (1..=md.method_defs.len())
            .find(|&row| assembly.method_name(0x06000000 | row as u32).unwrap() == name)
            .unwrap() as u32
    };
    let rows: Vec<_> = md
        .method_impls
        .iter()
        .enumerate()
        .filter(|(_, row)| row.class == owner("MultiExplicit"))
        .map(|(i, _)| i)
        .collect();
    assert_eq!(rows.len(), 2);
    let (implementation, context) = metadata::table_row(&images[0], TableId::MethodImpl, rows[0]);
    let class_width = context.table_index_size(TableId::TypeDef);
    let method_width = context.coded_index_size(CodedIndexKind::MethodDefOrRef);
    let body = implementation + class_width;
    let declaration = body + method_width;
    let interface_row = md
        .interface_impls
        .iter()
        .position(|row| row.class == owner("MultiExplicit"))
        .unwrap();
    let (interface, _) = metadata::table_row(&images[0], TableId::InterfaceImpl, interface_row);
    let interface_width = context.coded_index_size(CodedIndexKind::TypeDefOrRef);
    let mut probes: Vec<(&str, Vec<u8>, &str, &str)> = Vec::new();
    let mapping = &md.method_impls[rows[0]];
    let (body_definition, _) = metadata::table_row(
        &images[0],
        TableId::MethodDef,
        mapping.method_body.row as usize - 1,
    );
    let mut missing_body = images[0].clone();
    write_index(&mut missing_body, body_definition, 4, 0);
    probes.push((
        "missing executable body",
        missing_body,
        "MultipleSlots",
        "executable IL body",
    ));
    let (decl_definition, _) = metadata::table_row(
        &images[0],
        TableId::MethodDef,
        mapping.method_declaration.row as usize - 1,
    );
    let flags = md.method_defs[mapping.method_declaration.row as usize - 1].flags;
    let mut inaccessible = images[0].clone();
    write_index(
        &mut inaccessible,
        decl_definition + 6,
        2,
        u32::from((flags & !7) | 0x201),
    );
    probes.push((
        "inaccessible strict declaration",
        inaccessible,
        "MultipleSlots",
        "inaccessible strict",
    ));
    let mut final_declaration = images[0].clone();
    write_index(
        &mut final_declaration,
        decl_definition + 6,
        2,
        u32::from(flags | 0x20),
    );
    probes.push((
        "final declaration",
        final_declaration,
        "MultipleSlots",
        "nonfinal",
    ));
    for (label, offset, width, value, needle) in [
        (
            "null interface owner",
            interface,
            class_width,
            0,
            "InterfaceImpl",
        ),
        (
            "null interface",
            interface + class_width,
            interface_width,
            0,
            "InterfaceImpl",
        ),
        (
            "invalid interface tag",
            interface + class_width,
            interface_width,
            3,
            "InterfaceImpl",
        ),
        (
            "non-interface target",
            interface + class_width,
            interface_width,
            owner("PlainParent") << 2,
            "interface",
        ),
        (
            "null implementation owner",
            implementation,
            class_width,
            0,
            "MethodImpl",
        ),
        (
            "null implementation body",
            body,
            method_width,
            0,
            "MethodImpl",
        ),
        (
            "null implementation declaration",
            declaration,
            method_width,
            0,
            "MethodImpl",
        ),
        (
            "wrong body owner",
            body,
            method_width,
            methods("Unit::Value") << 1,
            "MethodImpl",
        ),
        (
            "signature mismatch",
            body,
            method_width,
            methods("MultiExplicit::Wide") << 1,
            "MethodImpl",
        ),
        (
            "wrong declaration owner",
            declaration,
            method_width,
            methods("Unit::Value") << 1,
            "MethodImpl",
        ),
    ] {
        let mut bytes = images[0].clone();
        write_index(&mut bytes, offset, width, value);
        probes.push((label, bytes, "MultipleSlots", needle));
    }
    let (other, _) = metadata::table_row(&images[0], TableId::MethodImpl, rows[1]);
    let mut duplicate = images[0].clone();
    let saved = duplicate[declaration..declaration + method_width].to_vec();
    duplicate[other + class_width + method_width..other + class_width + method_width * 2]
        .copy_from_slice(&saved);
    probes.push((
        "duplicate declaration",
        duplicate,
        "MultipleSlots",
        "Duplicate MethodImpl",
    ));
    let child = md
        .interface_impls
        .iter()
        .position(|row| row.class == owner("IChild"))
        .unwrap();
    let (child, _) = metadata::table_row(&images[0], TableId::InterfaceImpl, child);
    let mut cycle = images[0].clone();
    write_index(
        &mut cycle,
        child + class_width,
        interface_width,
        owner("IChild") << 2,
    );
    probes.push(("interface cycle", cycle, "InheritedInterface", "cycle"));
    for (label, bytes, entry, needle) in probes {
        let result = Resolver::new(&[&bytes, &images[1], &library])
            .and_then(|r| compile::object(&r, Some(&format!("Program::{entry}"))));
        let error = result.unwrap_err();
        assert!(error.contains(needle), "{label}: {error}");
    }
    for (entry, needle) in [("BoxedInterface", "0x008c")] {
        let result = managed
            .command()
            .arg(
                directory
                    .path()
                    .join(format!("bin/{configuration}/net10.0/InterfaceCalls.dll")),
            )
            .arg(entry)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&result.stdout).trim(),
            "42",
            "{entry}"
        );
        let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(needle) && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
    let row = md
        .method_impls
        .iter()
        .position(|row| row.class == owner("RemoteLocalExplicit"))
        .unwrap();
    let (offset, _) = metadata::table_row(&images[0], TableId::MethodImpl, row);
    let lookup = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let member = md
        .member_refs
        .iter()
        .enumerate()
        .find_map(|(index, row)| {
            if md.strings.get(row.name).unwrap() != "Value" {
                return None;
            }
            let resolved = lookup.resolve_method(0, 0x0a000001 + index as u32).unwrap();
            (lookup
                .assembly(resolved.definition.assembly)
                .unwrap()
                .method_name(resolved.definition.token)
                .unwrap()
                == "InterfaceLibrary.RemoteBase::Value")
                .then_some(index as u32 + 1)
        })
        .unwrap();
    let mut inherited = images[0].clone();
    write_index(
        &mut inherited,
        offset + class_width,
        method_width,
        (member << 1) | 1,
    );
    let explicit = md
        .method_impls
        .iter()
        .position(|row| row.class == owner("Explicit"))
        .unwrap();
    let (explicit, _) = metadata::table_row(&images[0], TableId::MethodImpl, explicit);
    let mut inherited_definition = images[0].clone();
    write_index(
        &mut inherited_definition,
        explicit,
        class_width,
        owner("ReboundExplicit"),
    );
    let mut class_alias = images[0].clone();
    write_index(
        &mut class_alias,
        offset + class_width + method_width,
        method_width,
        (member << 1) | 1,
    );
    for (bytes, entry, label, inherited_body) in [
        (inherited, "MetadataBody", "InheritedReferenceBody", true),
        (
            inherited_definition,
            "ReimplementedExplicit",
            "InheritedDefinitionBody",
            true,
        ),
        (class_alias, "ClassMetadataBody", "ClassAlias", false),
    ] {
        let path = directory
            .path()
            .join(format!("bin/{configuration}/net10.0/{label}.dll"));
        std::fs::write(&path, &bytes).unwrap();
        let result = managed.command().arg(&path).arg(entry).output().unwrap();
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        let managed_report = String::from_utf8_lossy(&result.stdout);
        if inherited_body && managed_report.trim() == "THROW:System.TypeLoadException" {
            assert!(
                String::from_utf8_lossy(&result.stderr).contains(
                    "Body referenced in a method implementation must be defined in the same type."
                ),
                "{label}: {}",
                String::from_utf8_lossy(&result.stderr)
            );
            eprintln!("{label}: CLR same-type restriction; native execution checked independently");
        } else {
            assert_eq!(
                managed_report.trim(),
                "42",
                "{label}: {}",
                String::from_utf8_lossy(&result.stderr)
            );
        }
        let resolver = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
        let linked = koh_link::link(&[object], &Default::default()).unwrap();
        let report = execute_mode(host, directory.path(), &linked, "__koh_exit", "--native");
        assert_eq!(report, "00 0000002A D000 OK", "{label}");
    }
}
