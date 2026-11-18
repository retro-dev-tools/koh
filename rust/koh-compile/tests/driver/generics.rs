use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn generic_method_instances_execute_transitively(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "GenericMethods");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let type_row = |assembly, name| {
        let image = resolver.assembly(assembly).unwrap();
        image
            .metadata()
            .type_defs
            .iter()
            .position(|ty| image.metadata().strings.get(ty.type_name).unwrap() == name)
            .unwrap()
    };
    assert_eq!(type_row(0, "Payload"), type_row(2, "RemotePayload"));
    assert_eq!(type_row(0, "Marker"), type_row(2, "RemoteMarker"));
    assert_eq!(type_row(0, "LocalCode"), type_row(2, "RemoteCode"));
    assert_eq!(type_row(0, "IMarker"), type_row(2, "IRemoteMarker"));
    assert_eq!(type_row(0, "Reader"), type_row(2, "RemoteReader"));
    for (assembly, name, bytes, offsets) in [
        (0, "Payload", 3, vec![0, 1]),
        (2, "RemotePayload", 12, vec![0, 4]),
    ] {
        let layout = koh_compile::layout::value(
            &resolver,
            koh_compile::resolve::Definition {
                assembly,
                token: 0x02000001 + type_row(assembly, name) as u32,
            },
        )
        .unwrap();
        assert_eq!(layout.bytes, bytes, "{name}");
        assert_eq!(
            layout
                .fields
                .iter()
                .map(|field| field.offset)
                .collect::<Vec<_>>(),
            offsets,
            "{name}"
        );
    }
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericMethods.pdb")),
    )
    .unwrap();
    let library_pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.pdb")),
    )
    .unwrap();
    for (entry, expected) in [
        ("GenericRectangles", "0000002A"),
        ("Main", "00000082"),
        ("Wide", "0000002A"),
        ("References", "0000002A"),
        ("Storage", "0000002A"),
        ("Referenced", "00000082"),
        ("PrimitiveWidths", "0000002A"),
        ("NamedTypes", "0000002A"),
        ("Constraint", "0000002A"),
        ("ConstraintShapes", "0000002A"),
        ("ConstructorEligibility", "0000002A"),
        ("GenericConstruction", "0000002A"),
        ("InstanceMethods", "0000002A"),
        ("GenericDelegates", "0000002A"),
        ("GenericBaseCalls", "0000002A"),
        ("GenericVirtualCall", "0000002A"),
        ("GenericVirtualGroup", "0000002A"),
        ("GenericInterfaces", "0000002A"),
        ("GenericReferences", "0000002A"),
        ("ConstrainedValues", "0000002A"),
        ("ConstrainedReferences", "0000002A"),
        ("ConstrainedGenerics", "0000002A"),
        ("ActivatorProbe", "00000000"),
        ("Main", "00000082"),
    ] {
        let mut object = compile::object_with_debug(
            &resolver,
            Some(&format!("Program::{entry}")),
            &[Some(&pdb), None, Some(&library_pdb)],
        )
        .unwrap_or_else(|error| panic!("{entry}: {error}"));
        assert!(
            object
                .symbols
                .iter()
                .all(|symbol| !symbol.name.contains("Unused")),
            "{entry} dead unsupported bodies"
        );
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
        assert!(
            object
                .symbols
                .iter()
                .any(|symbol| symbol.name.contains("fb0000")
                    && symbol
                        .source
                        .as_ref()
                        .is_some_and(|source| source.file.ends_with(if entry == "Referenced" {
                            "Library.cs"
                        } else {
                            "Program.cs"
                        }) && source.line > 0)),
            "{entry} instance source"
        );
        assert!(
            object
                .sections
                .iter()
                .any(|section| section.name.contains("mfb0000")
                    && section.lines.iter().any(|line| line.file.ends_with(
                        if entry == "Referenced" {
                            "Library.cs"
                        } else {
                            "Program.cs"
                        }
                    ) && line.line > 0)),
            "{entry} instance machine source ranges"
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", expected, "D000", "OK"], "{entry}");
        if entry == "ConstrainedValues" {
            super::assert_heap_snapshot(fields[4], &[0x5a; 4096], "Unboxed constrained values");
        }
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} bank2");
        }
    }
    for entry in ["GenericNullCall", "GenericNullGroup", "ConstrainedNull"] {
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
            "__koh_abort",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields[0], "04", "{entry}");
        assert_eq!(fields[3], "OK", "{entry}");
        super::assert_heap_snapshot(fields[4], &[0x5a; 4096], entry);
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry}");
        }
    }
    for (entry, expected) in [
        (
            "GenericRankThree",
            "Rectangular array rank 3 is unsupported",
        ),
        ("InvalidActivation", "Generic construction requires"),
        (
            "Expanding",
            "Managed references require primitive/pointer scalar storage",
        ),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(expected) && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let method_token = |name| {
        (1..=data.method_defs.len() as u32)
            .map(|row| 0x06000000 | row)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap()
    };
    let body = image.method_body(method_token("Program::Advance")).unwrap();
    let prefix = koh_compile::cil::decode(body.il)
        .unwrap()
        .into_iter()
        .find(|i| i.opcode == 0xfe16)
        .unwrap();
    let positions: Vec<_> = images[0]
        .windows(body.il.len())
        .enumerate()
        .filter_map(|(offset, candidate)| (candidate == body.il).then_some(offset))
        .collect();
    assert_eq!(positions.len(), 1);
    let mut bytes = images[0].clone();
    let offset = positions[0] + prefix.offset as usize + 2;
    let wrong = 0x02000001 + type_row(0, "Marker") as u32;
    bytes[offset..offset + 4].copy_from_slice(&wrong.to_le_bytes());
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::ConstrainedValues")).unwrap_err();
    assert!(
        error.contains("Constrained receiver must be a managed pointer") && error.contains("IL_"),
        "{error}"
    );

    let explicit_value = type_row(0, "ExplicitMutableValue") as u32 + 1;
    let mapping = data
        .method_impls
        .iter()
        .position(|row| row.class == explicit_value)
        .unwrap();
    let mut bytes = images[0].clone();
    let (offset, context) = metadata::table_row(&bytes, clrmeta::TableId::MethodImpl, mapping);
    let offset = offset + context.table_index_size(clrmeta::TableId::TypeDef);
    let width = context.coded_index_size(clrmeta::CodedIndexKind::MethodDefOrRef);
    let wrong = (method_token("MutableValue::Next") & 0xffffff) << 1;
    bytes[offset..offset + width].copy_from_slice(&wrong.to_le_bytes()[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::ConstrainedValues")).unwrap_err();
    assert!(
        error.contains("Constrained MethodImpl body must belong") && error.contains("IL_"),
        "{error}"
    );
    let specification = |kind, foreign: bool| {
        data.method_specs
            .iter()
            .enumerate()
            .find(|(index, _)| {
                let Ok(target) = resolver.resolve_method(0, 0x2b000001 + *index as u32) else {
                    return false;
                };
                target.method_arguments == [clrmeta::TypeSig::Primitive(kind)]
                    && (!foreign || target.definition.assembly == 2)
            })
            .unwrap()
    };
    let (unsigned, _) = specification(clrmeta::ElementType::U2, true);
    let (_, signed) = specification(clrmeta::ElementType::I2, false);
    let mut bytes = images[0].clone();
    let (offset, context) = metadata::table_row(&bytes, clrmeta::TableId::MethodSpec, unsigned);
    let offset = offset + context.coded_index_size(clrmeta::CodedIndexKind::MethodDefOrRef);
    let width = context.blob_index_size();
    bytes[offset..offset + width].copy_from_slice(&signed.instantiation.to_le_bytes()[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::GenericDelegates")).unwrap_err();
    assert!(
        error.contains("Delegate target parameter type differs") && error.contains("IL_"),
        "{error}"
    );
    let receiver_method = data
        .method_defs
        .iter()
        .enumerate()
        .find(|(index, _)| {
            image.method_name(0x06000001 + *index as u32).unwrap() == "GenericReceiver::Forward"
        })
        .unwrap()
        .0;
    for flag in [0x10, 0x40] {
        let mut bytes = images[0].clone();
        let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::MethodDef, receiver_method);
        let flags = data.method_defs[receiver_method].flags | flag;
        bytes[offset + 6..offset + 8].copy_from_slice(&flags.to_le_bytes());
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let compiled = compile::object(&changed, Some("Program::InstanceMethods"));
        if flag == 0x40 {
            let object = compiled.unwrap();
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
                assert_eq!(fields[5], "6B".repeat(4096));
            }
        } else {
            let error = compiled.unwrap_err();
            assert!(
                error.contains("static/instance signature disagrees") && error.contains("IL_"),
                "{error}"
            );
        }
    }
    let activation = data
        .member_refs
        .iter()
        .enumerate()
        .find(|(_, member)| {
            data.strings.get(member.name).unwrap() == "CreateInstance"
                && member.class.table == Some(clrmeta::TableId::TypeRef)
                && data
                    .strings
                    .get(data.type_refs[member.class.row as usize - 1].type_name)
                    .unwrap()
                    == "Activator"
        })
        .unwrap()
        .0;
    for replacement in ["Program::ConstructorType", "Marker::Read"] {
        let signature = data
            .method_defs
            .iter()
            .enumerate()
            .find(|(index, _)| {
                image.method_name(0x06000001 + *index as u32).unwrap() == replacement
            })
            .unwrap()
            .1
            .signature;
        let mut bytes = images[0].clone();
        let (offset, context) =
            metadata::table_row(&bytes, clrmeta::TableId::MemberRef, activation);
        let offset = offset
            + context.coded_index_size(clrmeta::CodedIndexKind::MemberRefParent)
            + context.string_index_size();
        let width = context.blob_index_size();
        bytes[offset..offset + width].copy_from_slice(&signature.to_le_bytes()[..width]);
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let error = compile::object(&changed, Some("Program::ActivatorProbe")).unwrap_err();
        assert!(
            error.contains("exact static") && error.contains("IL_"),
            "{error}"
        );
    }
    let value_ctor = data
        .method_defs
        .iter()
        .enumerate()
        .find(|(index, _)| {
            image.method_name(0x06000001 + *index as u32).unwrap() == "ConstructedValue::.ctor"
        })
        .unwrap()
        .0;
    let mut bytes = images[0].clone();
    let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::MethodDef, value_ctor);
    let flags = (data.method_defs[value_ctor].flags & !7) | 1;
    bytes[offset + 6..offset + 8].copy_from_slice(&flags.to_le_bytes());
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::GenericConstruction")).unwrap_err();
    assert!(
        error.contains("Nonpublic value-type constructor") && error.contains("IL_"),
        "{error}"
    );

    let mut bytes = images[0].clone();
    let (offset, context) = metadata::table_row(&bytes, clrmeta::TableId::MemberRef, activation);
    let width = context.coded_index_size(clrmeta::CodedIndexKind::MemberRefParent);
    let parent = ((type_row(0, "Program") as u32 + 1) << 3).to_le_bytes();
    bytes[offset..offset + width].copy_from_slice(&parent[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let object = compile::object(&changed, Some("Program::ActivatorProbe")).unwrap();
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
    let fields = report.split_whitespace().collect::<Vec<_>>();
    assert_eq!(
        &fields[..4],
        &["00", "0000002A", "D000", "OK"],
        "non-framework CreateInstance must execute ordinary CIL"
    );
    if color {
        assert_eq!(fields[5], "6B".repeat(4096));
    }
    let parameter = |name| {
        data.generic_params
            .iter()
            .position(|parameter| {
                parameter.owner.table == Some(clrmeta::TableId::MethodDef)
                    && image.method_name(0x06000000 | parameter.owner.row).unwrap()
                        == format!("Program::{name}")
                    && parameter.number == 0
            })
            .unwrap()
    };
    for owner in [0, data.generic_params.len() as u32 + 1] {
        let mut bytes = images[0].clone();
        let (offset, context) =
            metadata::table_row(&bytes, clrmeta::TableId::GenericParamConstraint, 0);
        let width = context.table_index_size(clrmeta::TableId::GenericParam);
        bytes[offset..offset + width].copy_from_slice(&owner.to_le_bytes()[..width]);
        assert!(
            koh_compile::assembly::Assembly::parse(&bytes).is_err(),
            "invalid GenericParamConstraint owner {owner} accepted"
        );
    }
    let combined_owner = parameter("Combined") as u32 + 1;
    let combined = data
        .generic_param_constraints
        .iter()
        .enumerate()
        .filter(|(_, row)| row.owner == combined_owner)
        .map(|(index, _)| index)
        .collect::<Vec<_>>();
    for duplicate in [false, true] {
        let mut bytes = images[0].clone();
        let (offset, context) = metadata::table_row(
            &bytes,
            clrmeta::TableId::GenericParamConstraint,
            combined[1],
        );
        let offset = offset + context.table_index_size(clrmeta::TableId::GenericParam);
        let width = context.coded_index_size(clrmeta::CodedIndexKind::TypeDefOrRef);
        let encoded = if duplicate {
            let (first, _) = metadata::table_row(
                &bytes,
                clrmeta::TableId::GenericParamConstraint,
                combined[0],
            );
            bytes[first + context.table_index_size(clrmeta::TableId::GenericParam)..][..width]
                .to_vec()
        } else {
            vec![0; width]
        };
        bytes[offset..offset + width].copy_from_slice(&encoded);
        let result = koh_compile::assembly::Assembly::parse(&bytes);
        assert!(
            result.is_err(),
            "invalid constraint target/duplicate accepted"
        );
    }
    assert_eq!(combined[1], combined[0] + 1);
    let mut bytes = images[0].clone();
    let other = (0..data.generic_param_constraints.len())
        .find(|index| !combined.contains(index))
        .unwrap();
    let (first, context) = metadata::table_row(
        &bytes,
        clrmeta::TableId::GenericParamConstraint,
        combined[1],
    );
    let (second, _) = metadata::table_row(&bytes, clrmeta::TableId::GenericParamConstraint, other);
    let size = context.row_size(clrmeta::TableId::GenericParamConstraint);
    let saved = bytes[first..first + size].to_vec();
    let swapped = bytes[second..second + size].to_vec();
    bytes[first..first + size].copy_from_slice(&swapped);
    bytes[second..second + size].copy_from_slice(&saved);
    let result = koh_compile::assembly::Assembly::parse(&bytes);
    assert!(result.is_err(), "noncontiguous constraint owner accepted");

    for (flags, entry, expected) in [
        (4u16, "Main", "special constraints"),
        (8, "References", "special constraints"),
        (16, "References", "special constraints"),
        (12, "Main", "Invalid generic method parameter flags"),
        (1, "Main", "Invalid generic method parameter flags"),
        (0x8000, "Main", "Invalid generic method parameter flags"),
    ] {
        let mut bytes = images[0].clone();
        let (offset, _) = metadata::table_row(
            &bytes,
            clrmeta::TableId::GenericParam,
            parameter("Identity"),
        );
        bytes[offset + 2..offset + 4].copy_from_slice(&flags.to_le_bytes());
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let error = compile::object(&changed, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(expected) && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
    for (name, second, expected) in [
        ("InterfaceOnly", false, "type constraint"),
        ("Combined", true, "Multiple class constraints"),
    ] {
        let owner = parameter(name) as u32 + 1;
        let rows = data
            .generic_param_constraints
            .iter()
            .enumerate()
            .filter(|(_, row)| row.owner == owner)
            .map(|(index, _)| index)
            .collect::<Vec<_>>();
        let mut bytes = images[0].clone();
        let (offset, context) = metadata::table_row(
            &bytes,
            clrmeta::TableId::GenericParamConstraint,
            rows[usize::from(second)],
        );
        let offset = offset + context.table_index_size(clrmeta::TableId::GenericParam);
        let width = context.coded_index_size(clrmeta::CodedIndexKind::TypeDefOrRef);
        let replacement = ((type_row(0, "ConstraintOther") as u32 + 1) << 2).to_le_bytes();
        bytes[offset..offset + width].copy_from_slice(&replacement[..width]);
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let error = compile::object(&changed, Some("Program::ConstraintShapes")).unwrap_err();
        assert!(
            error.contains(expected) && error.contains("IL_"),
            "{name}: {error}"
        );
    }
    let mapping = data
        .interface_impls
        .iter()
        .position(|row| row.class == type_row(0, "Payload") as u32 + 1)
        .unwrap();
    let mut bytes = images[0].clone();
    let (offset, context) = metadata::table_row(&bytes, clrmeta::TableId::InterfaceImpl, mapping);
    let offset = offset + context.table_index_size(clrmeta::TableId::TypeDef);
    let width = context.coded_index_size(clrmeta::CodedIndexKind::TypeDefOrRef);
    let encoded = ((type_row(0, "Marker") as u32 + 1) << 2).to_le_bytes();
    bytes[offset..offset + width].copy_from_slice(&encoded[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let result = compile::object(&changed, Some("Program::ConstraintShapes"));
    assert!(
        result.is_err(),
        "value InterfaceImpl accepted a class instead of an interface"
    );
    let error = result.unwrap_err();
    assert!(
        error.contains("interface") && error.contains("IL_"),
        "{error}"
    );

    let ctor = data
        .method_defs
        .iter()
        .enumerate()
        .find(|(index, _)| {
            image.method_name(0x06000001 + *index as u32).unwrap() == "Marker::.ctor"
        })
        .unwrap()
        .0;
    for access in [1u16, 4] {
        let mut bytes = images[0].clone();
        let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::MethodDef, ctor);
        let flags = (data.method_defs[ctor].flags & !7) | access;
        bytes[offset + 6..offset + 8].copy_from_slice(&flags.to_le_bytes());
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let error = compile::object(&changed, Some("Program::ConstructorEligibility")).unwrap_err();
        assert!(
            error.contains("special constraints") && error.contains("IL_"),
            "{error}"
        );
    }
    for extra in [0x40u16, 0x400] {
        let mut bytes = images[0].clone();
        let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::MethodDef, ctor);
        bytes[offset + 6..offset + 8]
            .copy_from_slice(&(data.method_defs[ctor].flags | extra).to_le_bytes());
        let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let error = compile::object(&changed, Some("Program::ConstructorEligibility")).unwrap_err();
        assert!(
            error.contains("Invalid abstract/virtual constructor") && error.contains("IL_"),
            "{error}"
        );
    }
    let value_type = data
        .type_refs
        .iter()
        .enumerate()
        .find(|(_, ty)| {
            data.strings.get(ty.type_name).unwrap() == "ValueType"
                && data.strings.get(ty.type_namespace).unwrap() == "System"
        })
        .unwrap();
    let mut bytes = images[0].clone();
    let (offset, context) = metadata::table_row(
        &bytes,
        clrmeta::TableId::TypeDef,
        type_row(0, "ConstraintBase"),
    );
    let width = context.string_index_size();
    bytes[offset + 4..offset + 4 + width]
        .copy_from_slice(&value_type.1.type_name.to_le_bytes()[..width]);
    bytes[offset + 4 + width..offset + 4 + width * 2]
        .copy_from_slice(&value_type.1.type_namespace.to_le_bytes()[..width]);
    let owner = parameter("BaseOnly") as u32 + 1;
    let constraint = data
        .generic_param_constraints
        .iter()
        .position(|row| row.owner == owner)
        .unwrap();
    let (offset, context) =
        metadata::table_row(&bytes, clrmeta::TableId::GenericParamConstraint, constraint);
    let offset = offset + context.table_index_size(clrmeta::TableId::GenericParam);
    let width = context.coded_index_size(clrmeta::CodedIndexKind::TypeDefOrRef);
    let encoded = (((value_type.0 as u32 + 1) << 2) | 1).to_le_bytes();
    bytes[offset..offset + width].copy_from_slice(&encoded[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::ConstraintShapes")).unwrap_err();
    assert!(
        error.contains("type constraint") && error.contains("IL_"),
        "forged System.ValueType: {error}"
    );
    let mut bytes = images[0].clone();
    let marker = type_row(0, "Marker");
    let (offset, _) = metadata::table_row(&bytes, clrmeta::TableId::TypeDef, marker);
    let flags = (data.type_defs[marker].flags & !0x100) | 0x80;
    bytes[offset..offset + 4].copy_from_slice(&flags.to_le_bytes());
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let error = compile::object(&changed, Some("Program::ConstructorEligibility")).unwrap_err();
    assert!(
        error.contains("special constraints") && error.contains("IL_"),
        "{error}"
    );

    let owner = parameter("Related") as u32 + 1;
    let constraint = data
        .generic_param_constraints
        .iter()
        .position(|row| row.owner == owner)
        .unwrap();
    let self_type = data
        .type_specs
        .iter()
        .position(|row| {
            koh_compile::signature::type_spec(data.blobs.get(row.signature).unwrap()).unwrap()
                == clrmeta::TypeSig::MVar(0)
        })
        .unwrap();
    let mut bytes = images[0].clone();
    let (offset, context) =
        metadata::table_row(&bytes, clrmeta::TableId::GenericParamConstraint, constraint);
    let offset = offset + context.table_index_size(clrmeta::TableId::GenericParam);
    let width = context.coded_index_size(clrmeta::CodedIndexKind::TypeDefOrRef);
    let encoded = (((self_type as u32 + 1) << 2) | 2).to_le_bytes();
    bytes[offset..offset + width].copy_from_slice(&encoded[..width]);
    let changed = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
    let object = compile::object(&changed, Some("Program::ConstraintShapes")).unwrap();
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
    let fields = report.split_whitespace().collect::<Vec<_>>();
    assert_eq!(
        &fields[..4],
        &["00", "0000002A", "D000", "OK"],
        "closed self constraint"
    );
    if color {
        assert_eq!(fields[5], "6B".repeat(4096));
    }
    let missing = Resolver::new(&[&images[0], &images[1]]).unwrap();
    compile::object(&missing, Some("Program::Main")).unwrap();
    let error = compile::object(&missing, Some("Program::Referenced")).unwrap_err();
    assert!(
        error.contains("GenericLibrary") && error.contains("IL_"),
        "{error}"
    );
}
