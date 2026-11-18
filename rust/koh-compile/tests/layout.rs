use koh_compile::{
    layout,
    resolve::{Definition, Resolver},
};
use rstest::{fixture, rstest};
use std::path::Path;
#[path = "support/image_cache.rs"]
mod image_cache;

#[fixture]
#[once]
fn class_images() -> [[Vec<u8>; 2]; 2] {
    let [a, b, c, d] = image_cache::images("ClassLayouts", || {
        let [[a, b], [c, d]] = build_class_images();
        [a, b, c, d]
    });
    [[a, b], [c, d]]
}

fn build_class_images() -> [[Vec<u8>; 2]; 2] {
    let directory = tempfile::tempdir().unwrap();
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/ClassLayouts");
    std::fs::create_dir(directory.path().join("Base")).unwrap();
    for name in [
        "ClassLayouts.csproj",
        "Types.cs",
        "Base/ClassBase.csproj",
        "Base/Types.cs",
    ] {
        std::fs::copy(source.join(name), directory.path().join(name)).unwrap();
    }
    ["Debug", "Release"].map(|configuration| {
        let dotnet = std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into());
        let output = std::process::Command::new(&dotnet)
            .arg("build")
            .arg(directory.path().join("ClassLayouts.csproj"))
            .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        let path = directory
            .path()
            .join(format!("bin/{configuration}/net10.0/ClassLayouts.dll"));
        let probe = std::process::Command::new(&dotnet)
            .arg(&path)
            .output()
            .unwrap();
        assert_eq!(
            probe.status.code(),
            Some(42),
            "{}",
            String::from_utf8_lossy(&probe.stderr)
        );
        ["ClassLayouts.dll", "ClassBase.dll"].map(|name| {
            std::fs::read(
                directory
                    .path()
                    .join(format!("bin/{configuration}/net10.0/{name}")),
            )
            .unwrap()
        })
    })
}

#[rstest]
#[case("Empty", 2, &[])]
#[case("Root", 17, &[2,3,5,9])]
#[case("Child", 30, &[2,3,5,9,17,18,20,22,24,26,28])]
#[case("Self", 5, &[2,4])]
#[case("WithBody", 8, &[2,3,7])]
#[case("Abstract", 6, &[2])]
#[case("CrossAssembly", 12, &[2,3,5,9,10])]
#[case("Numbers", 20, &[2,3,5,7,11,19])]
#[case("ArrayFields", 6, &[2,4])]
fn reference_classes_preserve_inherited_storage(
    class_images: &[[Vec<u8>; 2]; 2],
    #[case] name: &str,
    #[case] bytes: u16,
    #[case] offsets: &[u16],
) {
    for image in class_images {
        let resolver = Resolver::new(&[&image[0], &image[1]]).unwrap();
        let identity = definition(&resolver, name);
        let layout = layout::class(&resolver, identity).unwrap();
        assert_eq!(layout.identity, identity);
        assert_eq!(layout.bytes, bytes, "{name}");
        assert_eq!(
            layout.fields.iter().map(|f| f.offset).collect::<Vec<_>>(),
            offsets,
            "{name}"
        );
        assert!(std::sync::Arc::ptr_eq(
            &layout,
            &layout::class(&resolver, identity).unwrap()
        ));
        if name == "Child" {
            assert_eq!(layout.base, Some(definition(&resolver, "Root")));
            assert_eq!(layout.fields[0].identity.assembly, 0);
            assert_ne!(layout.fields[0].identity, layout.fields[4].identity);
            let base = layout::class(&resolver, definition(&resolver, "Root")).unwrap();
            assert!(std::ptr::eq(
                &base.fields[0].signature,
                &layout.fields[0].signature
            ));
        }
        if name == "CrossAssembly" {
            assert_eq!(layout.base.unwrap().assembly, 1);
            assert_eq!(
                layout
                    .fields
                    .iter()
                    .map(|f| f.identity.assembly)
                    .collect::<Vec<_>>(),
                [1, 1, 1, 0, 0]
            );
        }
    }
}

#[rstest]
#[case("Generic`1", "generic")]
#[case("Sequential", "AutoLayout")]
#[case("Explicit", "AutoLayout")]
#[case("Body", "reference class")]
#[case("TooLarge", "capacity")]
fn unsupported_class_layouts_diagnose(
    class_images: &[[Vec<u8>; 2]; 2],
    #[case] name: &str,
    #[case] message: &str,
) {
    for image in class_images {
        let resolver = Resolver::new(&[&image[0], &image[1]]).unwrap();
        let error = layout::class(&resolver, definition(&resolver, name)).unwrap_err();
        assert!(error.contains(message), "{name}: {error}");
    }
}

#[rstest]
fn interface_views_have_identity_without_instance_storage(class_images: &[[Vec<u8>; 2]; 2]) {
    for image in class_images {
        let resolver = Resolver::new(&[&image[0], &image[1]]).unwrap();
        let identity = definition(&resolver, "IPeer");
        let view = layout::class(&resolver, identity).unwrap();
        assert_eq!(view.identity, identity);
        assert!(view.interface);
        assert_eq!(view.bytes, 2);
        assert_eq!(view.base, None);
        assert!(view.fields.is_empty());
        assert!(view.interfaces.is_empty());
    }
}

#[rstest]
#[case("cycle", "Root", "cycle")]
#[case("sealed-base", "Root", "sealed")]
#[case("zero-fields", "Root", "field range")]
#[case("backward-fields", "Child", "field range")]
#[case("past-fields", "Root", "field range")]
fn malformed_reference_class_metadata_is_bounded(
    class_images: &[[Vec<u8>; 2]; 2],
    #[case] mutation: &str,
    #[case] name: &str,
    #[case] message: &str,
) {
    for image in class_images {
        let resolver = Resolver::new(&[&image[0], &image[1]]).unwrap();
        let identity = definition(&resolver, name);
        let metadata = resolver.assembly(0).unwrap().metadata();
        let row = &metadata.type_defs[(identity.token & 0xffffff) as usize - 1];
        let mut needle = row.flags.to_le_bytes().to_vec();
        for value in [
            row.type_name,
            row.type_namespace,
            row.extends.encode(clrmeta::CodedIndexKind::TypeDefOrRef),
            row.field_list,
            row.method_list,
        ] {
            needle.extend(u16::try_from(value).unwrap().to_le_bytes());
        }
        let positions: Vec<_> = image[0]
            .windows(needle.len())
            .enumerate()
            .filter_map(|(at, bytes)| (bytes == needle).then_some(at))
            .collect();
        assert_eq!(positions.len(), 1, "unique fixture TypeDef row");
        let (offset, bytes) = match mutation {
            "cycle" => (
                8,
                (((definition(&resolver, "Child").token & 0xffffff) << 2) as u16)
                    .to_le_bytes()
                    .to_vec(),
            ),
            "sealed-base" => (0, (row.flags | 0x100).to_le_bytes().to_vec()),
            "zero-fields" => (10, 0u16.to_le_bytes().to_vec()),
            "backward-fields" => (10, 1u16.to_le_bytes().to_vec()),
            _ => (
                10,
                u16::try_from(metadata.fields.len() + 2)
                    .unwrap()
                    .to_le_bytes()
                    .to_vec(),
            ),
        };
        let mut changed = image[0].clone();
        let start = positions[0] + offset;
        changed[start..start + bytes.len()].copy_from_slice(&bytes);
        let outcome = std::panic::catch_unwind(|| {
            let resolver = Resolver::new(&[&changed, &image[1]])?;
            layout::class(&resolver, definition(&resolver, "Child")).map(|_| ())
        });
        let error = outcome
            .expect("malformed class metadata must not panic")
            .unwrap_err();
        assert!(error.contains(message), "{mutation}: {error}");
    }
}

#[rstest]
fn class_layouts_require_external_base_images(class_images: &[[Vec<u8>; 2]; 2]) {
    for images in class_images {
        let resolver = Resolver::new(&[&images[0]]).unwrap();
        let error = layout::class(&resolver, definition(&resolver, "CrossAssembly")).unwrap_err();
        assert!(
            error.starts_with("Missing assembly ClassBase, version 1.0.0.0"),
            "{error}"
        );
        assert_eq!(
            layout::class(&resolver, definition(&resolver, "Child"))
                .unwrap()
                .bytes,
            30
        );
    }
}

fn compile_class_layout_source(source: &str) -> Vec<u8> {
    let directory = tempfile::tempdir().unwrap();
    let template = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures/ClassLayouts/Base/ClassBase.csproj");
    let project = directory.path().join("ClassLayouts.csproj");
    std::fs::copy(template, &project).unwrap();
    std::fs::write(directory.path().join("Types.cs"), source).unwrap();
    let output =
        std::process::Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
            .arg("build")
            .arg(project)
            .args(["-c", "Release", "--nologo", "-v:q", "-m:1"])
            .output()
            .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    std::fs::read(
        directory
            .path()
            .join("bin/Release/net10.0/ClassLayouts.dll"),
    )
    .unwrap()
}

#[rstest]
#[case(false)]
#[case(true)]
fn class_depth_is_independent_of_cache_order(#[case] cached: bool) {
    let mut source = String::from("public class C0 {}\n");
    for index in 1..65 {
        source.push_str(&format!("public class C{index}: C{} {{}}\n", index - 1));
    }
    let image = compile_class_layout_source(&source);
    let resolver = Resolver::new(&[&image]).unwrap();
    if cached {
        for index in 0..64 {
            layout::class(&resolver, definition(&resolver, &format!("C{index}"))).unwrap();
        }
    }
    let result = layout::class(&resolver, definition(&resolver, "C64"));
    assert!(
        result.is_err(),
        "65-level hierarchy accepted, cached={cached}"
    );
    assert!(result.unwrap_err().contains("depth"));
    assert_eq!(
        layout::class(&resolver, definition(&resolver, "C63"))
            .unwrap()
            .bytes,
        2
    );
}

#[test]
fn repeated_large_array_signatures_have_a_byte_budget() {
    let mut source = format!(
        "[assembly: System.Reflection.AssemblyMetadata(\"reserve\", \"{}\")]\npublic class Root {{\n",
        "q".repeat(20000)
    );
    for index in 0..200 {
        source.push_str(&format!("public int[,] F{index};\n"));
    }
    source.push_str("}\n");
    let image = compile_class_layout_source(&source);
    let resolver = Resolver::new(&[&image]).unwrap();
    let metadata = resolver.assembly(0).unwrap().metadata();
    let blob = metadata
        .custom_attributes
        .iter()
        .find_map(|a| {
            let blob = metadata.blobs.get(a.value).unwrap();
            (blob.len() > 20000).then_some((a.value, blob))
        })
        .unwrap();
    let positions: Vec<_> = image
        .windows(blob.1.len())
        .enumerate()
        .filter_map(|(at, bytes)| (bytes == blob.1).then_some(at))
        .collect();
    assert_eq!(positions.len(), 1);
    let original_len = blob.1.len() as u32;
    assert!((16384..65536).contains(&original_len));
    let mut header = original_len.to_be_bytes();
    header[0] |= 0xc0;
    let start = positions[0] - 4;
    assert_eq!(&image[start..start + 4], header);
    let mut signature = vec![6];
    signature.extend(std::iter::repeat_n(0x14, 63));
    signature.push(8);
    for _ in 0..63 {
        signature.extend([32, 32]);
        for _ in 0..32 {
            signature.extend([0xdf, 0xff, 0xff, 0xff]);
        }
        signature.push(32);
        signature.extend([0; 32]);
    }
    koh_compile::signature::field(&signature).unwrap();
    assert!((128..16384).contains(&signature.len()));
    let mut changed = image.clone();
    let len = signature.len();
    changed[start] = 0x80 | (len >> 8) as u8;
    changed[start + 1] = len as u8;
    changed[start + 2..start + 2 + len].copy_from_slice(&signature);
    for field in &metadata.fields {
        let mut needle = field.flags.to_le_bytes().to_vec();
        needle.extend(u16::try_from(field.name).unwrap().to_le_bytes());
        needle.extend(u16::try_from(field.signature).unwrap().to_le_bytes());
        let positions: Vec<_> = image
            .windows(needle.len())
            .enumerate()
            .filter_map(|(at, bytes)| (bytes == needle).then_some(at))
            .collect();
        assert_eq!(positions.len(), 1);
        let at = positions[0] + 4;
        changed[at..at + 2].copy_from_slice(&u16::try_from(blob.0).unwrap().to_le_bytes());
    }
    let resolver = Resolver::new(&[&changed]).unwrap();
    match layout::class(&resolver, definition(&resolver, "Root")) {
        Err(error) => assert!(error.contains("budget"), "{error}"),
        Ok(value) => panic!(
            "signature-byte budget accepted {} repeated large fields",
            value.fields.len()
        ),
    }
}

#[test]
fn flattened_class_layouts_have_a_shared_resource_budget() {
    let mut source = String::from("public class Root {\n");
    for field in 0..400 {
        source.push_str(&format!("public long F{field};\n"));
    }
    source.push_str("}\n");
    for class in 0..3000 {
        source.push_str(&format!("public class C{class} : Root {{ }}\n"));
    }
    source.push_str("public static class Program { public static void Main() { } }\n");
    let image = compile_class_layout_source(&source);
    let resolver = Resolver::new(&[&image]).unwrap();
    let mut stopped = false;
    for index in 0..3000 {
        match layout::class(&resolver, definition(&resolver, &format!("C{index}"))) {
            Ok(value) => {
                assert_eq!(value.bytes, 3202);
                assert_eq!(value.fields.len(), 400);
            }
            Err(error) => {
                assert!(error.contains("budget"), "{error}");
                stopped = true;
                break;
            }
        }
    }
    assert!(
        stopped,
        "shared inherited-field copies must have a bounded budget"
    );
    assert_eq!(
        layout::class(&resolver, definition(&resolver, "Root"))
            .unwrap()
            .bytes,
        3202
    );
}

#[fixture]
#[once]
fn images() -> [Vec<u8>; 2] {
    image_cache::images("StructLayouts", build_images)
}

fn build_images() -> [Vec<u8>; 2] {
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
#[case(false)]
#[case(true)]
fn value_depth_is_independent_of_cache_order(#[case] cached: bool) {
    let mut source = String::from("public struct V0 { public object Value; }\n");
    for index in 1..65 {
        source.push_str(&format!(
            "public struct V{index} {{ public V{} Value; }}\n",
            index - 1
        ));
    }
    let image = compile_class_layout_source(&source);
    let resolver = Resolver::new(&[&image]).unwrap();
    if cached {
        for index in 0..64 {
            layout::value(&resolver, definition(&resolver, &format!("V{index}"))).unwrap();
        }
    }
    let result = layout::value(&resolver, definition(&resolver, "V64"));
    assert!(
        result.is_err(),
        "65-level value hierarchy accepted, cached={cached}"
    );
    assert!(result.unwrap_err().contains("nesting"));
    let valid = layout::value(&resolver, definition(&resolver, "V63")).unwrap();
    assert_eq!(valid.bytes, 2);
    assert!(valid.contains_references);
}

#[test]
fn managed_reference_metadata_scans_have_a_shared_budget() {
    let mut source = String::from("public class Node {}\n");
    for index in 0..1024 {
        source.push_str(&format!("public class G{index}<T> {{}}\n"));
    }
    source.push_str("public struct References {\n");
    for index in 0..1024 {
        source.push_str(&format!("public Node F{index};\n"));
    }
    source.push_str("}\n");
    let image = compile_class_layout_source(&source);
    let resolver = Resolver::new(&[&image]).unwrap();
    let result = layout::value(&resolver, definition(&resolver, "References"));
    let error = result
        .err()
        .expect("Repeated generic-table scans must exhaust the shared reference budget");
    assert!(
        error.contains("reference") && error.contains("budget"),
        "{error}"
    );
}

#[rstest]
#[case(
    "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] public struct Value { [System.Runtime.InteropServices.FieldOffset(0)] public object Reference; [System.Runtime.InteropServices.FieldOffset(0)] public int Alias; }",
    "Explicit"
)]
#[case(
    "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] public struct Value { [System.Runtime.InteropServices.FieldOffset(0)] public object Reference; [System.Runtime.InteropServices.FieldOffset(8)] public byte Tail; }",
    "Explicit"
)]
#[case("public ref struct Value { public ref int Reference; }", "byref")]
fn unsupported_managed_value_shapes_diagnose(#[case] source: &str, #[case] expected: &str) {
    let image = compile_class_layout_source(source);
    let resolver = Resolver::new(&[&image]).unwrap();
    let error = layout::value(&resolver, definition(&resolver, "Value"))
        .err()
        .expect("Unsupported reference layout must diagnose");
    assert!(error.contains(expected), "{error}");
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
#[case("WithReference", 2, 2, &[0])]
fn roslyn_value_layouts_have_independent_offsets(
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
        assert_eq!(value.contains_references, name == "WithReference");
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
