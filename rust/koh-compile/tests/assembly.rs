use koh_compile::assembly::Assembly;
use rstest::{fixture, rstest};
use std::{path::Path, process::Command};

struct Assemblies {
    bytes: [Vec<u8>; 3],
}

#[fixture]
#[once]
fn assemblies() -> Assemblies {
    let directory = tempfile::Builder::new()
        .prefix("koh compiler fixtures ")
        .tempdir()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Reader");
    for name in ["Reader.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(name), directory.path().join(name)).unwrap();
    }
    let bytes = [
        ("Debug", "AnyCPU"),
        ("Release", "AnyCPU"),
        ("Release", "x64"),
    ]
    .map(|(configuration, platform)| {
        let output = Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
            .arg("build")
            .arg(directory.path().join("Reader.csproj"))
            .args(["-c", configuration, "--nologo", "-v:q"])
            .arg(format!("-p:PlatformTarget={platform}"))
            .output()
            .expect("The compiler tests require a .NET 10 SDK (KOH_DOTNET may select it)");
        assert!(
            output.status.success(),
            "Roslyn {configuration} fixture build failed: {}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        std::fs::read(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/Reader.dll")),
        )
        .unwrap()
    });
    Assemblies { bytes }
}

#[rstest]
#[case(0)]
#[case(1)]
#[case(2)]
fn reads_roslyn_assembly(assemblies: &Assemblies, #[case] configuration: usize) {
    let assembly = Assembly::parse(&assemblies.bytes[configuration]).unwrap();
    assert_eq!(assembly.name().unwrap(), "Reader");
    assert_eq!(
        assembly.method_name(assembly.entry_point).unwrap(),
        "Program::Main"
    );
    let body = assembly.method_body(assembly.entry_point).unwrap();
    assert!(!body.il.is_empty());
    assert_eq!(body.il.last(), Some(&0x2a));
    assert!(!body.extra_sections);
    assert!(assembly.metadata().assembly_refs.iter().any(|reference| {
        assembly.metadata().strings.get(reference.name).unwrap() == "System.Runtime"
    }));
    let names: Vec<_> = (1..=assembly.metadata().method_defs.len())
        .map(|index| assembly.method_name(0x06000000 | index as u32).unwrap())
        .collect();
    let add = names
        .iter()
        .position(|name| name == "Program::Add")
        .unwrap();
    let add = assembly.method_body(0x06000001 + add as u32).unwrap();
    assert!(add.il.contains(&0x58));
    for method in 1..=assembly.metadata().method_defs.len() {
        let token = 0x06000000 | method as u32;
        let body = assembly.method_body(token).unwrap();
        koh_compile::cil::decode(body.il).unwrap();
        assembly.method_signature(token).unwrap();
        assembly.local_types(token).unwrap();
    }
    for field in 1..=assembly.metadata().fields.len() {
        assembly.field_signature(0x04000000 | field as u32).unwrap();
    }
    let echo = names
        .iter()
        .position(|name| name == "Program::Echo")
        .unwrap();
    let echo = assembly.method_signature(0x06000001 + echo as u32).unwrap();
    assert_eq!(echo.generic_param_count, 1);
    assert_eq!(echo.params, vec![clrmeta::TypeSig::MVar(0)]);
    assert_eq!(echo.return_type, clrmeta::TypeSig::MVar(0));
    let update = names
        .iter()
        .position(|name| name == "Program::Update")
        .unwrap();
    assert_eq!(
        assembly
            .method_signature(0x06000001 + update as u32)
            .unwrap()
            .params,
        vec![
            clrmeta::TypeSig::ByRef(Box::new(clrmeta::TypeSig::Primitive(
                clrmeta::ElementType::I4
            ))),
            clrmeta::TypeSig::ByRef(Box::new(clrmeta::TypeSig::Primitive(
                clrmeta::ElementType::I8
            ))),
            clrmeta::TypeSig::Ptr(Box::new(clrmeta::TypeSig::Primitive(
                clrmeta::ElementType::U1
            ))),
        ]
    );
    let fields: Vec<_> = (1..=assembly.metadata().fields.len())
        .map(|index| assembly.field_signature(0x04000000 | index as u32).unwrap())
        .collect();
    assert!(fields.contains(&clrmeta::TypeSig::ByRef(Box::new(
        clrmeta::TypeSig::Primitive(clrmeta::ElementType::I4)
    ))));
    assert!(
        fields
            .iter()
            .any(|field| matches!(field, clrmeta::TypeSig::Array { rank: 2, .. }))
    );
    assert!(
        fields
            .iter()
            .any(|field| matches!(field, clrmeta::TypeSig::FnPtr(_)))
    );
    let exception = names
        .iter()
        .position(|name| name == "Program::WithException")
        .unwrap();
    assert!(
        assembly
            .method_body(0x06000001 + exception as u32)
            .unwrap()
            .extra_sections
    );
    for token in [0, 0x06000000, 0x0600ffff, 0x0a000001] {
        assert!(assembly.method(token).is_err(), "token {token:08x}");
    }
}

#[rstest]
fn truncation_and_header_mutations_do_not_panic(assemblies: &Assemblies) {
    let bytes = &assemblies.bytes[0];
    for length in 0..bytes.len() {
        let _ = Assembly::parse(&bytes[..length]);
    }
    for index in 0..512.min(bytes.len()) {
        let mut mutated = bytes.clone();
        mutated[index] ^= 0xff;
        if let Ok(assembly) = Assembly::parse(&mutated) {
            for method in 1..=assembly.metadata().method_defs.len() {
                let _ = assembly.method_body(0x06000000 | method as u32);
            }
        }
    }
}

#[rstest]
fn rejects_invalid_local_signature_references(assemblies: &Assemblies) {
    let bytes = &assemblies.bytes[0];
    let assembly = Assembly::parse(bytes).unwrap();
    let body = assembly.method_body(assembly.entry_point).unwrap();
    assert_ne!(body.local_signature, 0);
    let il_offset = body.il.as_ptr() as usize - bytes.as_ptr() as usize;
    for token in [0x11000000u32, 0x1100ffff, 0x06000001] {
        let mut mutated = bytes.clone();
        mutated[il_offset - 4..il_offset].copy_from_slice(&token.to_le_bytes());
        let assembly = Assembly::parse(&mutated).unwrap();
        assert!(
            assembly.method_body(assembly.entry_point).is_err(),
            "local token {token:08x}"
        );
    }
}

#[rstest]
#[case(0)]
#[case(1)]
#[case(2)]
fn reads_bounded_rva_data(assemblies: &Assemblies, #[case] configuration: usize) {
    let image = Assembly::parse(&assemblies.bytes[configuration]).unwrap();
    assert_eq!(image.metadata().field_rvas.len(), 2);
    let values: Vec<_> = image
        .metadata()
        .field_rvas
        .iter()
        .map(|f| image.field_data(0x04000000 | f.field).unwrap())
        .collect();
    assert!(values.contains(&[0x12, 0x34, 0x56, 0x78, 0x9a].as_slice()));
    assert!(values.contains(&[9, 8, 7, 6, 5, 4, 3, 2, 1].as_slice()));
    for token in [0, 0x04000000, 0x0400ffff, 0x06000001] {
        assert!(image.field_data(token).is_err());
    }
}

#[rstest]
#[case("rva-zero")]
#[case("rva-overflow")]
#[case("layout-zero")]
#[case("layout-large")]
#[case("layout-truncated")]
#[case("flags")]
#[case("rva-missing")]
#[case("rva-duplicate")]
#[case("layout-missing")]
#[case("layout-duplicate")]
#[case("layout-class")]
fn malformed_rva_fields_are_diagnostic(assemblies: &Assemblies, #[case] mutation: &str) {
    let bytes = &assemblies.bytes[0];
    let image = Assembly::parse(bytes).unwrap();
    let metadata = image.metadata();
    assert!(metadata.fields.len() < 65536 && metadata.type_defs.len() < 65536);
    let data = metadata
        .field_rvas
        .iter()
        .find(|f| image.field_data(0x04000000 | f.field).unwrap().len() == 5)
        .unwrap();
    let other = metadata
        .field_rvas
        .iter()
        .find(|f| f.field != data.field)
        .unwrap();
    let mut token = 0x04000000 | data.field;
    let (needle, offset, replacement) = if matches!(mutation, "rva-missing" | "rva-duplicate") {
        let mut needle = data.rva.to_le_bytes().to_vec();
        needle.extend_from_slice(&(data.field as u16).to_le_bytes());
        if mutation == "rva-duplicate" {
            token = 0x04000000 | other.field;
        }
        (needle, 4, (other.field as u16).to_le_bytes().to_vec())
    } else if mutation.starts_with("rva-") {
        let mut needle = data.rva.to_le_bytes().to_vec();
        needle.extend_from_slice(&(data.field as u16).to_le_bytes());
        (
            needle,
            0,
            (if mutation == "rva-zero" {
                0u32
            } else {
                0xfffffff0
            })
            .to_le_bytes()
            .to_vec(),
        )
    } else if mutation == "flags" {
        let field = &metadata.fields[data.field as usize - 1];
        assert!(field.name < 65536 && field.signature < 65536);
        let mut needle = field.flags.to_le_bytes().to_vec();
        needle.extend_from_slice(&(field.name as u16).to_le_bytes());
        needle.extend_from_slice(&(field.signature as u16).to_le_bytes());
        (needle, 0, (field.flags & !0x100).to_le_bytes().to_vec())
    } else if mutation == "layout-class" {
        let layout = metadata
            .class_layouts
            .iter()
            .find(|l| l.class_size == 5)
            .unwrap();
        let ty = &metadata.type_defs[layout.parent as usize - 1];
        assert_eq!(ty.extends.table, Some(clrmeta::TableId::TypeRef));
        let object = metadata
            .type_refs
            .iter()
            .position(|t| {
                metadata.strings.get(t.type_namespace).unwrap() == "System"
                    && metadata.strings.get(t.type_name).unwrap() == "Object"
            })
            .unwrap() as u16
            + 1;
        let mut needle = ty.flags.to_le_bytes().to_vec();
        for value in [
            ty.type_name,
            ty.type_namespace,
            (ty.extends.row << 2) | 1,
            ty.field_list,
            ty.method_list,
        ] {
            assert!(value < 65536);
            needle.extend_from_slice(&(value as u16).to_le_bytes());
        }
        (needle, 8, ((object << 2) | 1).to_le_bytes().to_vec())
    } else {
        let layout = metadata
            .class_layouts
            .iter()
            .find(|l| l.class_size == 5)
            .unwrap();
        let mut needle = layout.packing_size.to_le_bytes().to_vec();
        needle.extend_from_slice(&layout.class_size.to_le_bytes());
        needle.extend_from_slice(&(layout.parent as u16).to_le_bytes());
        if matches!(mutation, "layout-missing" | "layout-duplicate") {
            let other_layout = metadata
                .class_layouts
                .iter()
                .find(|l| l.class_size == 9)
                .unwrap();
            if mutation == "layout-duplicate" {
                token = 0x04000000 | other.field;
            }
            (
                needle,
                6,
                (other_layout.parent as u16).to_le_bytes().to_vec(),
            )
        } else {
            (
                needle,
                2,
                (match mutation {
                    "layout-zero" => 0u32,
                    "layout-large" => 4097,
                    _ => 4096,
                })
                .to_le_bytes()
                .to_vec(),
            )
        }
    };
    let positions: Vec<_> = bytes
        .windows(needle.len())
        .enumerate()
        .filter_map(|(i, b)| (b == needle).then_some(i))
        .collect();
    assert_eq!(
        positions.len(),
        1,
        "Mutation must address a unique metadata row"
    );
    let mut mutated = bytes.clone();
    let start = positions[0] + offset;
    mutated[start..start + replacement.len()].copy_from_slice(&replacement);
    let error = match Assembly::parse(&mutated) {
        Err(error) => error,
        Ok(image) => image.field_data(token).unwrap_err(),
    };
    assert!(
        error.contains("RVA") || error.contains("Truncated") || error.contains("data"),
        "{mutation}: {error}"
    );
}

#[rstest]
#[case(b"")]
#[case(b"not a PE file")]
#[case(b"MZ")]
fn rejects_invalid_images(#[case] bytes: &[u8]) {
    assert!(Assembly::parse(bytes).is_err());
}
