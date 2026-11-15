use koh_compile::{
    layout,
    resolve::{Definition, Resolver},
};
use rstest::{fixture, rstest};
use std::path::Path;

#[fixture]
#[once]
fn images() -> [Vec<u8>; 2] {
    let directory = tempfile::tempdir().unwrap();
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/StructLayouts");
    for name in ["StructLayouts.csproj", "Types.cs"] {
        std::fs::copy(source.join(name), directory.path().join(name)).unwrap();
    }
    ["Debug", "Release"].map(|configuration| {
        let result =
            std::process::Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
                .arg("build")
                .arg(directory.path().join("StructLayouts.csproj"))
                .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
                .output()
                .unwrap();
        assert!(
            result.status.success(),
            "{}{}",
            String::from_utf8_lossy(&result.stdout),
            String::from_utf8_lossy(&result.stderr)
        );
        let path = directory
            .path()
            .join(format!("bin/{configuration}/net10.0/StructLayouts.dll"));
        let probe =
            std::process::Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
                .arg(&path)
                .output()
                .unwrap();
        assert!(
            probe.status.success(),
            "{}",
            String::from_utf8_lossy(&probe.stderr)
        );
        assert_eq!(
            String::from_utf8(probe.stdout)
                .unwrap()
                .lines()
                .collect::<Vec<_>>(),
            ["5", "5"]
        );
        std::fs::read(path).unwrap()
    })
}

fn definition(resolver: &Resolver<'_>, name: &str) -> Definition {
    let metadata = resolver.assembly(0).unwrap().metadata();
    let index = metadata
        .type_defs
        .iter()
        .position(|ty| metadata.strings.get(ty.type_name).unwrap() == name)
        .unwrap();
    Definition {
        assembly: 0,
        token: 0x02000001 + index as u32,
    }
}

#[rstest]
#[case("Empty", 1, 1, &[])]
#[case("Plain", 16, 8, &[0,2,4,8])]
#[case("Packed", 15, 1, &[0,1,3,7])]
#[case("PackedTwo", 16, 2, &[0,2,4,8])]
#[case("Overlay", 16, 4, &[0,0,3])]
#[case("Large", 64, 1, &[0])]
#[case("Nested", 32, 8, &[0,8,24])]
#[case("WithEnum", 4, 2, &[0,2])]
#[case("OddSequential", 5, 4, &[0])]
#[case("OddExplicit", 5, 4, &[0])]
fn roslyn_unmanaged_layouts_have_independent_offsets(
    images: &[Vec<u8>; 2],
    #[case] name: &str,
    #[case] bytes: u16,
    #[case] alignment: u8,
    #[case] offsets: &[u16],
) {
    for image in images {
        let resolver = Resolver::new(&[image]).unwrap();
        let identity = definition(&resolver, name);
        let value = layout::value(&resolver, identity).unwrap();
        assert_eq!(value.identity, identity);
        assert_eq!((value.bytes, value.alignment), (bytes, alignment), "{name}");
        assert_eq!(
            value
                .fields
                .iter()
                .map(|field| field.offset)
                .collect::<Vec<_>>(),
            offsets,
            "{name}"
        );
        assert!(std::sync::Arc::ptr_eq(
            &value,
            &layout::value(&resolver, identity).unwrap()
        ));
    }
}

#[rstest]
#[case("WithReference", "reference")]
#[case("Generic`1", "generic")]
#[case("Automatic", "AutoLayout")]
#[case("Inline", "InlineArray")]
fn unsupported_struct_shapes_diagnose(
    images: &[Vec<u8>; 2],
    #[case] name: &str,
    #[case] message: &str,
) {
    for image in images {
        let resolver = Resolver::new(&[image]).unwrap();
        let error = layout::value(&resolver, definition(&resolver, name)).unwrap_err();
        assert!(error.contains(message), "{name}: {error}");
    }
}

#[rstest]
#[case("packing", "Packed", "packing")]
#[case("explicit-packing", "Overlay", "packing")]
#[case("capacity", "Large", "capacity")]
#[case("duplicate-class", "Packed", "Duplicate ClassLayout")]
#[case("duplicate-field", "Overlay", "Duplicate FieldLayout")]
#[case("offset-overflow", "Overlay", "overflow")]
#[case("missing-offset", "Overlay", "missing FieldLayout")]
#[case("cycle", "Nested", "cycle")]
#[case("constructor-zero", "Inline", "constructor")]
fn malformed_struct_metadata_diagnoses_without_panics(
    images: &[Vec<u8>; 2],
    #[case] mutation: &str,
    #[case] name: &str,
    #[case] message: &str,
) {
    for image in images {
        let resolver = Resolver::new(&[image]).unwrap();
        let identity = definition(&resolver, name);
        let metadata = resolver.assembly(0).unwrap().metadata();
        assert!(metadata.type_defs.len() < 32 && metadata.fields.len() < 65536);
        let class_row = |name: &str| {
            let rid = definition(&resolver, name).token & 0xffffff;
            metadata
                .class_layouts
                .iter()
                .find(|row| row.parent == rid)
                .unwrap()
        };
        let field_row = || {
            let start = metadata.type_defs[(identity.token & 0xffffff) as usize - 1].field_list;
            metadata
                .field_layouts
                .iter()
                .find(|row| row.field == start)
                .unwrap()
        };
        let (needle, at, replacement) = match mutation {
            "packing" | "explicit-packing" | "capacity" | "duplicate-class" => {
                let row = class_row(if mutation == "duplicate-class" {
                    "PackedTwo"
                } else {
                    name
                });
                let mut needle = row.packing_size.to_le_bytes().to_vec();
                needle.extend(row.class_size.to_le_bytes());
                needle.extend((row.parent as u16).to_le_bytes());
                let (at, replacement) = match mutation {
                    "packing" => (0, 3u16.to_le_bytes().to_vec()),
                    "explicit-packing" => (0, 1u16.to_le_bytes().to_vec()),
                    "capacity" => (2, 4097u32.to_le_bytes().to_vec()),
                    _ => (
                        6,
                        ((identity.token & 0xffffff) as u16).to_le_bytes().to_vec(),
                    ),
                };
                (needle, at, replacement)
            }
            "duplicate-field" | "offset-overflow" | "missing-offset" => {
                let row = field_row();
                let row = if mutation == "duplicate-field" {
                    metadata
                        .field_layouts
                        .iter()
                        .find(|other| other.field == row.field + 1)
                        .unwrap()
                } else {
                    row
                };
                let mut needle = row.offset.to_le_bytes().to_vec();
                needle.extend((row.field as u16).to_le_bytes());
                let (at, replacement) = match mutation {
                    "offset-overflow" => (0, u32::MAX.to_le_bytes().to_vec()),
                    "missing-offset" => (4, 0u16.to_le_bytes().to_vec()),
                    _ => (4, ((row.field - 1) as u16).to_le_bytes().to_vec()),
                };
                (needle, at, replacement)
            }
            "constructor-zero" => {
                use clrmeta::{CodedIndexKind, TableId};
                let attribute = metadata
                    .custom_attributes
                    .iter()
                    .find(|a| {
                        a.parent.table == Some(TableId::TypeDef)
                            && a.parent.row == identity.token & 0xffffff
                    })
                    .unwrap();
                let mut needle = (attribute.parent.encode(CodedIndexKind::HasCustomAttribute)
                    as u16)
                    .to_le_bytes()
                    .to_vec();
                needle.extend(
                    (attribute
                        .attr_type
                        .encode(CodedIndexKind::CustomAttributeType) as u16)
                        .to_le_bytes(),
                );
                needle.extend((attribute.value as u16).to_le_bytes());
                (needle, 2, 3u16.to_le_bytes().to_vec())
            }
            _ => {
                let start = metadata.type_defs[(identity.token & 0xffffff) as usize - 1].field_list;
                let signature = metadata
                    .blobs
                    .get(metadata.fields[start as usize].signature)
                    .unwrap();
                assert_eq!(signature.len(), 3);
                let mut needle = vec![3];
                needle.extend(signature);
                (needle, 3, vec![((identity.token & 0xffffff) << 2) as u8])
            }
        };
        let positions: Vec<_> = image
            .windows(needle.len())
            .enumerate()
            .filter_map(|(at, bytes)| (bytes == needle).then_some(at))
            .collect();
        assert_eq!(positions.len(), 1, "{mutation}: unique metadata row");
        let mut malformed = image.clone();
        let at = positions[0] + at;
        malformed[at..at + replacement.len()].copy_from_slice(&replacement);
        let result = std::panic::catch_unwind(|| {
            let resolver = Resolver::new(&[&malformed])?;
            layout::value(&resolver, identity).map(|_| ())
        });
        let error = result
            .expect("Malformed struct metadata must not panic")
            .unwrap_err();
        assert!(error.contains(message), "{mutation}: {error}");
    }
}
