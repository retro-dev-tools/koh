use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

#[rstest::fixture]
#[once]
fn managed() -> Host {
    super::support::build_fixture_host("ManagedHost")
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn generic_override_arity_must_match_its_metadata(host: &Host, #[case] configuration: &str) {
    let (directory, images) = gameboy_images(host, configuration, "ClosedTypes");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let method = |name| {
        (1..=data.method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap()
    };
    let forward = method("GenericVirtualOverride`1::Forward");
    let other = method("DefaultGenericReference`1::Get");
    let parameter = data
        .generic_params
        .iter()
        .position(|entry| {
            entry.owner.table == Some(clrmeta::TableId::MethodDef)
                && entry.owner.row == forward & 0xffffff
        })
        .unwrap();
    let mut bytes = images[0].clone();
    let (offset, context) = metadata::table_row(&bytes, clrmeta::TableId::GenericParam, parameter);
    let offset = offset + 4;
    let width = context.coded_index_size(clrmeta::CodedIndexKind::TypeOrMethodDef);
    bytes[offset..offset + width]
        .copy_from_slice(&(((other & 0xffffff) << 1) | 1).to_le_bytes()[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::GenericOverrideArity"))
        .err()
        .expect("Generic override metadata mismatch must diagnose");
    assert!(
        error.contains("Generic arity disagrees") && error.contains("IL_"),
        "{error}"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn generic_methodimpl_bodies_keep_the_closed_ancestor(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ClosedTypes");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let owners = ["GenericMappedReference`1", "FixedGenericMappedReference"].map(|name| {
        data.type_defs
            .iter()
            .position(|row| data.strings.get(row.type_name).unwrap() == name)
            .unwrap() as u32
            + 1
    });
    let mappings: Vec<_> = data
        .method_impls
        .iter()
        .enumerate()
        .filter_map(|(index, row)| owners.contains(&row.class).then_some(index))
        .collect();
    assert_eq!(mappings.len(), 2);
    for (name, valid) in [
        ("GenericVirtualReference`1::Forward", true),
        ("GenericOtherReference`1::Forward", false),
    ] {
        let body = (1..=data.method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap();
        let mut bytes = images[0].clone();
        for &mapping in &mappings {
            let (offset, context) =
                metadata::table_row(&bytes, clrmeta::TableId::MethodImpl, mapping);
            let offset = offset + context.table_index_size(clrmeta::TableId::TypeDef);
            let width = context.coded_index_size(clrmeta::CodedIndexKind::MethodDefOrRef);
            bytes[offset..offset + width]
                .copy_from_slice(&((body & 0xffffff) << 1).to_le_bytes()[..width]);
        }
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let result = compile::object(&changed, Some("Program::InheritedGenericBody"));
        if !valid {
            let error = result
                .err()
                .expect("Generic MethodImpl body cannot borrow an unrelated owner");
            assert!(
                error.contains("outside the class inheritance") && error.contains("IL_"),
                "{error}"
            );
            continue;
        }
        let mut object = result.unwrap();
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
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], name);
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn constrained_instantiated_interfaces_preserve_values_and_mutation(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ClosedInterfaces");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let object = compile::object(&resolver, Some("Program::Main")).unwrap();
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
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
    super::assert_heap_snapshot(
        fields[4],
        &[0x5a; 4096],
        "Constrained interfaces must not box",
    );
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let body = (1..=data.method_defs.len())
        .find(|&index| {
            let name = image.method_name(0x06000000 | index as u32).unwrap();
            name.starts_with("ExplicitCell`1::")
                && name.contains("IValue")
                && name.ends_with(".Read")
        })
        .unwrap();
    let reference = (1..=data.member_refs.len())
        .find(|&index| {
            resolver
                .resolve_method(0, 0x0a000000 | index as u32)
                .is_ok_and(|target| {
                    target.type_arguments == [clrmeta::TypeSig::Primitive(clrmeta::ElementType::U8)]
                        && image.method_name(target.definition.token).unwrap()
                            == "ExplicitCell`1::Read"
                })
        })
        .unwrap();
    let mapping = data
        .method_impls
        .iter()
        .position(|entry| {
            entry.method_body.table == Some(clrmeta::TableId::MethodDef)
                && entry.method_body.row == body as u32
        })
        .unwrap();
    let mut bytes = images[0].clone();
    let (offset, context) = metadata::table_row(&bytes, clrmeta::TableId::MemberRef, reference - 1);
    let offset = offset + context.coded_index_size(clrmeta::CodedIndexKind::MemberRefParent);
    let width = context.string_index_size();
    bytes[offset..offset + width]
        .copy_from_slice(&data.method_defs[body - 1].name.to_le_bytes()[..width]);
    let (offset, context) = metadata::table_row(&bytes, clrmeta::TableId::MethodImpl, mapping);
    let offset = offset + context.table_index_size(clrmeta::TableId::TypeDef);
    let width = context.coded_index_size(clrmeta::CodedIndexKind::MethodDefOrRef);
    bytes[offset..offset + width]
        .copy_from_slice(&((reference as u32) << 1 | 1).to_le_bytes()[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::Main"))
        .err()
        .expect("Closed body owner must match the receiver instantiation");
    assert!(
        error.contains("Constrained MethodImpl body must belong") && error.contains("IL_"),
        "{error}"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn closed_methodimpl_bodies_keep_the_inherited_declaring_scope(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ClosedTypes");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let owners = ["MappedCell`1", "FixedMappedCell"].map(|name| {
        data.type_defs
            .iter()
            .position(|row| data.strings.get(row.type_name).unwrap() == name)
            .unwrap() as u32
            + 1
    });
    let mappings: Vec<_> = data
        .method_impls
        .iter()
        .enumerate()
        .filter_map(|(index, row)| owners.contains(&row.class).then_some(index))
        .collect();
    assert_eq!(mappings.len(), 2);
    for (name, valid) in [
        ("VirtualCell`1::Read", true),
        ("InterfaceCell`1::Read", false),
    ] {
        let body = (1..=data.method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap();
        let mut bytes = images[0].clone();
        for &mapping in &mappings {
            let (offset, context) =
                metadata::table_row(&bytes, clrmeta::TableId::MethodImpl, mapping);
            let offset = offset + context.table_index_size(clrmeta::TableId::TypeDef);
            let width = context.coded_index_size(clrmeta::CodedIndexKind::MethodDefOrRef);
            bytes[offset..offset + width]
                .copy_from_slice(&((body & 0xffffff) << 1).to_le_bytes()[..width]);
        }
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let result = compile::object(&changed, Some("Program::InheritedClosedBody"));
        if !valid {
            let error = result
                .err()
                .expect("MethodImpl cannot borrow a body outside its closed ancestry");
            assert!(
                error.contains("outside the class inheritance") && error.contains("IL_"),
                "{error}"
            );
            continue;
        }
        let mut object = result.unwrap();
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
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], name);
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn inherited_member_references_bind_the_closed_base(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
    #[values(false, true)] fields: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ClosedTypes");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let mut bytes = images[0].clone();
    let mut changes = 0;
    let payload = data
        .type_defs
        .iter()
        .position(|ty| data.strings.get(ty.type_name).unwrap() == "Payload")
        .unwrap() as u32
        + 1;
    for (name, owner, argument) in [
        (
            "Program::InheritedMembers",
            "DerivedCell`1",
            clrmeta::TypeSig::Primitive(clrmeta::ElementType::U1),
        ),
        (
            "Program::ReadInherited",
            "DerivedCell`1",
            clrmeta::TypeSig::MVar(0),
        ),
        (
            "Program::InheritedRemoteMembers",
            "LocalDerived`1",
            clrmeta::TypeSig::ValueType(payload << 2),
        ),
    ] {
        let derived = data
            .type_defs
            .iter()
            .position(|ty| data.strings.get(ty.type_name).unwrap() == owner)
            .unwrap() as u32
            + 1;
        let parent = data.type_specs.iter().position(|row|
            matches!(koh_compile::signature::type_spec(data.blobs.get(row.signature).unwrap()).unwrap(),
                clrmeta::TypeSig::GenericInst { type_ref, type_args, .. }
                if type_ref == derived << 2 && type_args == vec![argument.clone()])
        ).unwrap() as u32 + 1;
        let method = (1..=data.method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap();
        for instruction in koh_compile::cil::decode(image.method_body(method).unwrap().il).unwrap()
        {
            let koh_compile::cil::Operand::Token(mut token) = instruction.operand else {
                continue;
            };
            if token >> 24 == 0x2b {
                token = 0x0a000000
                    | data.method_specs[(token & 0xffffff) as usize - 1]
                        .method
                        .row;
            }
            if token >> 24 != 0x0a {
                continue;
            }
            let row = &data.member_refs[(token & 0xffffff) as usize - 1];
            if !matches!(
                data.strings.get(row.name).unwrap(),
                "Read" | "Value" | "Echo"
            ) {
                continue;
            }
            if !fields && data.strings.get(row.name).unwrap() == "Value" {
                continue;
            }
            let (offset, context) = metadata::table_row(
                &bytes,
                clrmeta::TableId::MemberRef,
                (token & 0xffffff) as usize - 1,
            );
            let width = context.coded_index_size(clrmeta::CodedIndexKind::MemberRefParent);
            bytes[offset..offset + width]
                .copy_from_slice(&((parent << 3) | 4).to_le_bytes()[..width]);
            changes += 1;
        }
    }
    assert_eq!(changes, if fields { 8 } else { 6 });
    let assembly = directory
        .path()
        .join(format!("bin/{configuration}/net10.0/ClosedTypes.dll"));
    std::fs::write(&assembly, &bytes).unwrap();
    let result = super::dotnet()
        .arg(&managed.dll)
        .arg(&assembly)
        .arg("InheritedMembers")
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&result.stdout).trim(),
        if fields {
            "THROW:System.MissingFieldException"
        } else {
            "42"
        }
    );
    let resolver = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let result = compile::object(&resolver, Some("Program::InheritedMembers"));
    if fields {
        let error = result
            .err()
            .expect("Field reference must name its declaring type");
        assert!(
            error.contains("Unresolved field Value") && error.contains("IL_"),
            "{error}"
        );
        return;
    }
    for entry in ["InheritedMembers", "InheritedRemoteMembers"] {
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
        assert_eq!(
            execute_mode(host, directory.path(), &linked, "__koh_exit", "--native"),
            "00 0000002A D000 OK"
        );
    }
}

#[rstest]
#[case(8, false)]
#[case(1024, true)]
fn declaring_constraint_scans_obey_the_work_budget(
    host: &Host,
    #[case] count: usize,
    #[case] limited: bool,
) {
    let directory = tempfile::tempdir().unwrap();
    let project = directory.path().join("TypeWork.csproj");
    std::fs::write(&project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><TreatWarningsAsErrors>true</TreatWarningsAsErrors><NuGetAudit>true</NuGetAudit></PropertyGroup></Project>").unwrap();
    let parameters = (0..count)
        .map(|index| format!("T{index}"))
        .collect::<Vec<_>>()
        .join(",");
    let constraints = (0..count)
        .map(|index| format!(" where T{index}:IMarker"))
        .collect::<String>();
    let arguments = vec!["Item"; count].join(",");
    let source = format!(
        "interface IMarker{{}} class Item:IMarker{{}} class Root<{parameters}>{constraints}{{}} public static class Program{{ public static int Main()=>Read(null); static int Read(Root<{arguments}> value)=>value==null?42:0; }}"
    );
    std::fs::write(directory.path().join("Program.cs"), source).unwrap();
    let result = super::dotnet()
        .arg("build")
        .arg(&project)
        .args(["-c", "Release", "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let image = std::fs::read(directory.path().join("bin/Release/net10.0/TypeWork.dll")).unwrap();
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let resolver = Resolver::new(&[&image])?;
        compile::object(&resolver, None)
    }))
    .expect("Constraint budget must diagnose without panicking");
    if limited {
        let error = result
            .err()
            .expect("Large declaring constraint scan must reject before lowering");
        assert!(
            error.contains("Generic instantiation work or storage exceeds")
                && error.contains("IL_"),
            "{error}"
        );
    } else {
        let linked = koh_link::link(&[result.unwrap()], &Default::default()).unwrap();
        assert_eq!(
            execute_mode(host, directory.path(), &linked, "__koh_exit", "--native"),
            "00 0000002A D000 OK"
        );
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn closed_types_preserve_layouts_and_static_initialization(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ClosedTypes");
    let read = |name: &str, extension: &str| {
        std::fs::read(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/{name}.{extension}")),
        )
        .unwrap()
    };
    let library = read("GenericLibrary", "dll");
    let pdb = read("ClosedTypes", "pdb");
    let library_pdb = read("GenericLibrary", "pdb");
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    for entry in [
        "Values",
        "Inheritance",
        "ConstrainedDispatch",
        "InterfaceDispatch",
        "InstantiatedInterfaces",
        "GenericReferenceMethods",
        "Objects",
        "Statics",
        "TypeOnly",
        "References",
        "Construction",
        "CallbackWarm",
        "CallbackCold",
        "Arrays",
        "CallbackOwned",
    ] {
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), None, Some(&library_pdb)],
        )
        .unwrap_or_else(|error| panic!("{entry}: {error}"));
        let fresh = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
        let repeated = compile::object_with_debug(
            &fresh,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), None, Some(&library_pdb)],
        )
        .unwrap();
        assert_eq!(
            koh_objects::write_elf(&object).unwrap(),
            koh_objects::write_elf(&repeated).unwrap(),
            "{entry} resolver history"
        );
        let reference_mapping = if entry == "Objects" {
            let alias = object
                .symbols
                .iter()
                .find(|symbol| {
                    symbol.name.contains("RemoteCell") && symbol.name.contains("::.ctor [")
                })
                .expect("Specialized referenced constructor symbol");
            let source = alias
                .source
                .as_ref()
                .expect("Referenced constructor source");
            assert!(source.file.ends_with("Library.cs") && source.line > 0);
            let koh_objects::SymbolDefinition::Label { section, offset } = alias.definition else {
                panic!("Constructor code label");
            };
            let code = &object.sections[section.0];
            let start = usize::try_from(offset).unwrap();
            let end = usize::try_from(code.size).unwrap();
            assert!(
                code.lines
                    .iter()
                    .any(|line| line.file.ends_with("Library.cs")
                        && line.line > 0
                        && line.offset >= start
                        && line.offset < end)
            );
            Some((alias.name.clone(), code.size - offset))
        } else {
            None
        };
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
        if let Some((name, bytes)) = reference_mapping {
            let symbol = linked
                .symbols
                .iter()
                .find(|symbol| symbol.name == name)
                .unwrap();
            let start = u32::try_from(symbol.value).unwrap();
            assert!(
                linked
                    .lines
                    .iter()
                    .any(|line| Some(line.bank) == symbol.bank
                        && line.address >= start
                        && line.address < start + bytes
                        && line.file.ends_with("Library.cs")
                        && line.line > 0)
            );
        }
        assert!(linked.dwarf().unwrap().starts_with(b"\x7fELF"));
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let explicit_value = data
        .type_defs
        .iter()
        .position(|row| data.strings.get(row.type_name).unwrap() == "ExplicitInterfaceValue`1")
        .unwrap() as u32
        + 1;
    let mapping = data
        .method_impls
        .iter()
        .position(|row| {
            row.class == explicit_value
                && image
                    .method_name(0x06000000 | row.method_body.row)
                    .unwrap()
                    .ends_with("::IClosedRead.Read")
        })
        .unwrap();
    let wrong = (1..=data.method_defs.len())
        .map(|row| 0x06000000 | row as u32)
        .find(|&token| image.method_name(token).unwrap() == "InterfaceValue`1::Read")
        .unwrap();
    let mut bytes = images[0].clone();
    let (offset, context) = metadata::table_row(&bytes, clrmeta::TableId::MethodImpl, mapping);
    let offset = offset + context.table_index_size(clrmeta::TableId::TypeDef);
    let width = context.coded_index_size(clrmeta::CodedIndexKind::MethodDefOrRef);
    bytes[offset..offset + width]
        .copy_from_slice(&((wrong & 0xffffff) << 1).to_le_bytes()[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::ConstrainedDispatch"))
        .err()
        .expect("MethodImpl cannot borrow a body from another generic value definition");
    assert!(
        error.contains("Constrained MethodImpl body must belong") && error.contains("IL_"),
        "{error}"
    );
    let base = data
        .type_defs
        .iter()
        .position(|row| data.strings.get(row.type_name).unwrap() == "BaseCell`1")
        .unwrap();
    let mut sealed = images[0].clone();
    let (offset, _) = metadata::table_row(&sealed, clrmeta::TableId::TypeDef, base);
    let flags = data.type_defs[base].flags | 0x100;
    sealed[offset..offset + 4].copy_from_slice(&flags.to_le_bytes());
    let changed = Resolver::new(&[&sealed, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::Inheritance"))
        .err()
        .expect("Closed base still obeys sealed metadata");
    assert!(
        error.contains("sealed base") && error.contains("IL_"),
        "{error}"
    );
    let error = compile::object(&resolver, Some("Program::InvalidExplicit"))
        .err()
        .expect("ECMA-335 forbids generic explicit layouts even when T is unused");
    assert!(
        error.contains("Generic types cannot use explicit layout") && error.contains("IL_"),
        "{error}"
    );
    let owner = data
        .type_defs
        .iter()
        .position(|row| data.strings.get(row.type_name).unwrap() == "Cell`1")
        .unwrap() as u32
        + 1;
    let parameter = data
        .generic_params
        .iter()
        .position(|row| {
            row.owner.table == Some(clrmeta::TableId::TypeDef) && row.owner.row == owner
        })
        .unwrap();
    for (flags, expected) in [
        (4u16, "special constraints"),
        (32, "AllowByRefLike"),
        (1, "parameter flags"),
    ] {
        let mut bytes = images[0].clone();
        let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::GenericParam, parameter);
        bytes[offset + 2..offset + 4].copy_from_slice(&flags.to_le_bytes());
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let result = compile::object(&changed, Some("Program::Objects"));
        if flags == 32 {
            let linked = koh_link::link(&[result.unwrap()], &Default::default()).unwrap();
            let report = execute_mode(
                host,
                directory.path(),
                &linked,
                "__koh_exit",
                "--native-heap",
            );
            let fields: Vec<_> = report.split_whitespace().collect();
            assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
            continue;
        }
        let error = result.unwrap_err();
        assert!(
            error.contains(expected) && error.contains("IL_"),
            "{flags}: {error}"
        );
    }
    let specification = data.type_specs.iter().find(|row| {
        let ty = koh_compile::signature::type_spec(data.blobs.get(row.signature).unwrap()).unwrap();
        matches!(ty, clrmeta::TypeSig::GenericInst { is_value_type: false, type_ref, ref type_args }
            if type_ref == owner << 2 && matches!(type_args.as_slice(), [clrmeta::TypeSig::ValueType(_)]))
    }).unwrap();
    let heap = data.blobs.data();
    let locations: Vec<_> = images[0]
        .windows(heap.len())
        .enumerate()
        .filter_map(|(offset, bytes)| (bytes == heap).then_some(offset))
        .collect();
    assert_eq!(locations.len(), 1);
    let signature = data.blobs.get(specification.signature).unwrap();
    assert_eq!(signature.len(), 6);
    assert_eq!(heap[specification.signature as usize], 6);
    let mut bytes = images[0].clone();
    let offset = locations[0] + specification.signature as usize + 1 + 4;
    bytes[offset..offset + 2].copy_from_slice(&[0x0f, 5]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::TypeOnly")).unwrap_err();
    assert!(
        error.contains("Generic type argument") && error.contains("IL_"),
        "{error}"
    );
}
