use clrmeta::{ElementType, TypeSig};
use koh_compile::{
    cil::{Operand, decode},
    resolve::Resolver,
};
use rstest::{fixture, rstest};
use std::{path::Path, process::Command};

fn table_row(
    bytes: &[u8],
    table: clrmeta::TableId,
    index: usize,
) -> (usize, clrmeta::tables::TableContext) {
    let file = object::read::pe::PeFile32::parse(bytes).unwrap();
    let sections = file.section_table();
    let cli = file
        .data_directory(object::pe::IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR)
        .unwrap()
        .data(bytes, &sections)
        .unwrap();
    let rva = u32::from_le_bytes(cli[8..12].try_into().unwrap());
    let (start, _) = sections.pe_file_range_at(rva).unwrap();
    let root = clrmeta::MetadataRoot::parse(&bytes[start as usize..]).unwrap();
    let stream = root.tables_stream().unwrap();
    let start = start as usize + stream.offset as usize;
    let mut reader = clrmeta::reader::Reader::new(&bytes[start..start + stream.size as usize]);
    let header = clrmeta::TablesHeader::parse(&mut reader, false).unwrap();
    let context = header.context();
    let mut offset = start + reader.position();
    for (current, count) in header.tables() {
        if count == 0 {
            continue;
        }
        let size = if current == clrmeta::TableId::Module {
            2 + context.string_index_size() + 3 * context.guid_index_size()
        } else {
            context.row_size(current)
        };
        assert!(size != 0, "Test mutation does not support {current:?}");
        if current == table {
            assert!(index < count as usize);
            return (offset + index * size, context);
        }
        offset += count as usize * size;
    }
    panic!("Missing fixture table {table:?}")
}

fn write_index(bytes: &mut [u8], offset: usize, width: usize, value: u32) {
    if width == 2 {
        assert!(value <= u16::MAX as u32);
    }
    bytes[offset..offset + width].copy_from_slice(&value.to_le_bytes()[..width]);
}

struct Images {
    configurations: [[Vec<u8>; 3]; 2],
    wrong_version: Vec<u8>,
    facade: Vec<u8>,
    framework: Vec<u8>,
}

#[fixture]
#[once]
fn images() -> Images {
    let directory = tempfile::Builder::new()
        .prefix("koh references ")
        .tempdir()
        .unwrap();
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/References");
    for (project, file) in [
        ("Entry", "Program.cs"),
        ("Bridge", "Api.cs"),
        ("Library", "Arithmetic.cs"),
        ("Facade", "Forwarders.cs"),
    ] {
        let target = directory.path().join(project);
        std::fs::create_dir(&target).unwrap();
        for name in [format!("{project}.csproj"), file.to_owned()] {
            std::fs::copy(source.join(project).join(&name), target.join(name)).unwrap();
        }
    }
    let build = |project: &str, configuration: &str, extra: &[String]| {
        let output = Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
            .arg("build")
            .arg(directory.path().join(format!("{project}/{project}.csproj")))
            .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
            .args(extra)
            .output()
            .expect("Reference tests require a .NET 10 SDK");
        assert!(
            output.status.success(),
            "{}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
    };
    let configurations = ["Debug", "Release"].map(|configuration| {
        build("Entry", configuration, &[]);
        ["Entry", "Game.Bridge", "Game.Runtime"].map(|name| {
            std::fs::read(
                directory
                    .path()
                    .join(format!("Entry/bin/{configuration}/net10.0/{name}.dll")),
            )
            .unwrap()
        })
    });
    build("Facade", "Release", &[]);
    let facade = std::fs::read(
        directory
            .path()
            .join("Facade/bin/Release/net10.0/Game.Facade.dll"),
    )
    .unwrap();
    let paths = std::fs::read_to_string(
        directory
            .path()
            .join("Entry/obj/Release/net10.0/reference-paths.txt"),
    )
    .unwrap();
    let framework = std::fs::read(
        paths
            .lines()
            .find(|path| {
                Path::new(path)
                    .file_name()
                    .is_some_and(|name| name == "System.Runtime.dll")
            })
            .unwrap(),
    )
    .unwrap();
    let output = directory.path().join("wrong version");
    build(
        "Library",
        "Release",
        &[
            "-p:AssemblyVersion=9.0.0.0".into(),
            "-o".into(),
            output.to_str().unwrap().to_owned(),
        ],
    );
    let wrong_version = std::fs::read(output.join("Game.Runtime.dll")).unwrap();
    Images {
        configurations,
        wrong_version,
        facade,
        framework,
    }
}

fn method_token(resolver: &Resolver<'_>, assembly: usize, name: &str) -> u32 {
    let image = resolver.assembly(assembly).unwrap();
    (1..=image.metadata().method_defs.len())
        .map(|row| 0x06000000 | row as u32)
        .find(|&token| {
            image
                .method_name(token)
                .unwrap()
                .ends_with(&format!("::{name}"))
        })
        .unwrap()
}

fn operands(resolver: &Resolver<'_>, assembly: usize, method: &str, opcode: u16) -> Vec<u32> {
    let image = resolver.assembly(assembly).unwrap();
    decode(
        image
            .method_body(method_token(resolver, assembly, method))
            .unwrap()
            .il,
    )
    .unwrap()
    .into_iter()
    .filter_map(|instruction| match instruction.operand {
        Operand::Token(token) if instruction.opcode == opcode => Some(token),
        _ => None,
    })
    .collect()
}

#[rstest]
#[case(0)]
#[case(1)]
fn binds_transitive_calls_by_assembly_and_full_signature(
    images: &Images,
    #[case] configuration: usize,
) {
    let resolver = Resolver::new(
        &images.configurations[configuration]
            .iter()
            .map(Vec::as_slice)
            .collect::<Vec<_>>(),
    )
    .unwrap();
    let call = operands(&resolver, 0, "Main", 0x28)[0];
    let compute = resolver.resolve_method(0, call).unwrap();
    assert_eq!(compute.definition.assembly, 1);
    assert_eq!(
        resolver
            .assembly(1)
            .unwrap()
            .method_name(compute.definition.token)
            .unwrap(),
        "Game.Bridge.Api::Compute"
    );
    for token in operands(&resolver, 1, "Compute", 0x28) {
        let method = resolver.resolve_method(1, token).unwrap();
        assert_eq!(method.definition.assembly, 2);
        let signature = resolver
            .assembly(2)
            .unwrap()
            .method_signature(method.definition.token)
            .unwrap();
        let name = resolver
            .assembly(2)
            .unwrap()
            .method_name(method.definition.token)
            .unwrap();
        if name.ends_with("::Add") {
            assert_eq!(
                signature.params,
                vec![TypeSig::Primitive(ElementType::I4); 2]
            );
        } else {
            assert!(name.ends_with("::Identity"));
            assert_eq!(
                method.method_arguments,
                vec![TypeSig::Primitive(ElementType::I4)]
            );
            assert_eq!(method.argument_assembly, 1);
        }
    }
    let field = resolver
        .resolve_field(1, operands(&resolver, 1, "Compute", 0x80)[0])
        .unwrap();
    assert_eq!(field.definition.assembly, 2);
    assert_eq!(
        resolver
            .assembly(2)
            .unwrap()
            .field_signature(field.definition.token)
            .unwrap(),
        TypeSig::Primitive(ElementType::I4)
    );
}

#[rstest]
#[case(0)]
#[case(1)]
fn binds_named_and_instantiated_types_across_assemblies(
    images: &Images,
    #[case] configuration: usize,
) {
    let resolver = Resolver::new(
        &images.configurations[configuration]
            .iter()
            .map(Vec::as_slice)
            .collect::<Vec<_>>(),
    )
    .unwrap();
    let pass = resolver
        .resolve_method(1, operands(&resolver, 1, "Pass", 0x28)[0])
        .unwrap();
    assert_eq!(pass.definition.assembly, 2);
    let echo = resolver
        .resolve_method(1, operands(&resolver, 1, "Echo", 0x6f)[0])
        .unwrap();
    assert_eq!(
        echo.type_arguments,
        vec![TypeSig::Primitive(ElementType::I4)]
    );
    let field = resolver
        .resolve_field(1, operands(&resolver, 1, "Echo", 0x7b)[0])
        .unwrap();
    assert_eq!(field.type_arguments, echo.type_arguments);
    let metadata = resolver.assembly(1).unwrap().metadata();
    let nested = metadata
        .type_refs
        .iter()
        .position(|row| metadata.strings.get(row.type_name).unwrap() == "Inner")
        .unwrap();
    let definition = resolver
        .resolve_type(1, 0x01000001 + nested as u32)
        .unwrap();
    assert_eq!(definition.assembly, 2);
}

#[rstest]
fn missing_or_wrong_assembly_versions_are_diagnostic(images: &Images) {
    let [entry, bridge, _] = &images.configurations[0];
    for inputs in [
        vec![&entry[..], &bridge[..]],
        vec![&entry[..], &bridge[..], &images.wrong_version[..]],
    ] {
        let resolver = Resolver::new(&inputs).unwrap();
        let error = resolver
            .resolve_method(1, operands(&resolver, 1, "Compute", 0x28)[0])
            .unwrap_err();
        assert!(
            error.contains("Game.Runtime") && error.contains("2.3.4.5"),
            "{error}"
        );
    }
}

#[rstest]
fn duplicate_identities_are_rejected(images: &Images) {
    let entry = &images.configurations[0][0];
    assert!(Resolver::new(&[entry, entry]).is_err());
}

#[rstest]
fn follows_explicit_type_forwarders(images: &Images) {
    let library = &images.configurations[0][2];
    let resolver = Resolver::new(&[&images.facade, library]).unwrap();
    let definition = resolver.resolve_type(0, 0x27000001).unwrap();
    assert_eq!(definition.assembly, 1);
    let metadata = resolver.assembly(1).unwrap().metadata();
    assert_eq!(
        metadata
            .strings
            .get(metadata.type_defs[(definition.token & 0x00ff_ffff) as usize - 1].type_name)
            .unwrap(),
        "Payload"
    );
}

#[rstest]
fn binds_strong_named_framework_metadata_without_running_it(images: &Images) {
    let library = &images.configurations[0][2];
    let resolver = Resolver::new(&[library, &images.framework]).unwrap();
    let metadata = resolver.assembly(0).unwrap().metadata();
    let constructor = metadata
        .member_refs
        .iter()
        .enumerate()
        .find(|(_, member)| {
            if metadata.strings.get(member.name).unwrap() != ".ctor"
                || member.class.table != Some(clrmeta::TableId::TypeRef)
            {
                return false;
            }
            let ty = &metadata.type_refs[member.class.row as usize - 1];
            metadata.strings.get(ty.type_name).unwrap() == "Object"
        })
        .unwrap()
        .0;
    let method = resolver
        .resolve_method(0, 0x0a000001 + constructor as u32)
        .unwrap();
    assert_eq!(method.definition.assembly, 1);
    assert_eq!(
        resolver
            .assembly(1)
            .unwrap()
            .method_name(method.definition.token)
            .unwrap(),
        "System.Object::.ctor"
    );
}

#[rstest]
fn nonzero_zero_row_scopes_never_bind_a_local_type(images: &Images) {
    let source = &images.configurations[0][2];
    let image = koh_compile::assembly::Assembly::parse(source).unwrap();
    let metadata = image.metadata();
    let payload = metadata
        .type_defs
        .iter()
        .find(|ty| metadata.strings.get(ty.type_name).unwrap() == "Payload")
        .unwrap();
    let reference = metadata
        .type_refs
        .iter()
        .position(|ty| metadata.strings.get(ty.type_name).unwrap() == "Object")
        .unwrap();
    let (offset, context) = table_row(source, clrmeta::TableId::TypeRef, reference);
    let scope_width = context.coded_index_size(clrmeta::CodedIndexKind::ResolutionScope);
    for raw in 1..=3 {
        let mut bytes = source.clone();
        write_index(&mut bytes, offset, scope_width, raw);
        write_index(
            &mut bytes,
            offset + scope_width,
            context.string_index_size(),
            payload.type_name,
        );
        write_index(
            &mut bytes,
            offset + scope_width + context.string_index_size(),
            context.string_index_size(),
            payload.type_namespace,
        );
        let resolver = Resolver::new(&[&bytes]).unwrap();
        assert!(
            resolver
                .resolve_type(0, 0x01000001 + reference as u32)
                .is_err(),
            "raw scope {raw} bound locally"
        );
    }
}

#[rstest]
fn validates_nested_generic_instances_in_signatures_and_method_arguments(images: &Images) {
    let [entry, bridge, library] = &images.configurations[0];
    let image = koh_compile::assembly::Assembly::parse(library).unwrap();
    let metadata = image.metadata();
    let holder = metadata
        .type_defs
        .iter()
        .position(|ty| metadata.strings.get(ty.type_name).unwrap() == "Holder`1")
        .unwrap() as u32
        + 1;
    let extra = metadata
        .type_defs
        .iter()
        .position(|ty| metadata.strings.get(ty.type_name).unwrap() == "Extra`1")
        .unwrap() as u32
        + 1;
    let parameter = metadata
        .generic_params
        .iter()
        .position(|p| p.owner.table == Some(clrmeta::TableId::TypeDef) && p.owner.row == extra)
        .unwrap();
    let (offset, context) = table_row(library, clrmeta::TableId::GenericParam, parameter);
    let mut invalid = library.clone();
    write_index(&mut invalid, offset, 2, 1);
    write_index(
        &mut invalid,
        offset + 4,
        context.coded_index_size(clrmeta::CodedIndexKind::TypeOrMethodDef),
        holder << 1,
    );
    let resolver = Resolver::new(&[entry, bridge, &invalid]).unwrap();
    for name in ["Wrapped", "GenericWrap"] {
        let token = operands(&resolver, 1, name, 0x28)[0];
        let error = resolver.resolve_method(1, token).unwrap_err();
        assert!(error.contains("arity"), "{name}: {error}");
    }
}
