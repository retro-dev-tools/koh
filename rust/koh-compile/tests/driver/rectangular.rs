use super::{Host, compile, execute_mode, gameboy_images, host};
use koh_compile::resolve::Resolver;
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn rectangular_arrays_keep_independent_dimensions_and_mutable_aliases(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
    #[values(
        "Program::Main",
        "NullShapes::Main",
        "EmptyShapes::Main",
        "BorrowedElements::Main",
        "WideElements::Main",
        "AggregateShapes::Main"
    )]
    entry: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "RectangularArrays");
    let desktop = super::dotnet()
        .arg(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/RectangularArrays.dll")),
        )
        .output()
        .unwrap();
    assert_eq!(
        desktop.status.code(),
        Some(42),
        "{}",
        String::from_utf8_lossy(&desktop.stderr)
    );
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(&resolver, Some(entry)).unwrap();
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
    let result = execute_mode(
        host,
        directory.path(),
        &linked,
        "__koh_exit",
        "--native-heap",
    );
    let fields: Vec<_> = result.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{result}");
    if color {
        super::assert_heap_snapshot(
            fields[5],
            &[0x6b; 4096],
            "rectangular arrays preserve bank2",
        );
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn rectangular_arrays_reject_invalid_dimensions_before_truncation(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "RectangularArrays");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (method, fault) in [
        ("NegativeRow", "06"),
        ("NegativeColumn", "06"),
        ("RowOverflow", "06"),
        ("ColumnOverflow", "06"),
        ("TruncatedIndex", "06"),
        ("InvalidDimension", "06"),
        ("NegativeDimension", "06"),
        ("NegativeSize", "05"),
        ("TruncatedSize", "05"),
        ("Capacity", "00"),
        ("ProductOverflow", "05"),
        ("NullIndex", "04"),
        ("NullDimension", "04"),
    ] {
        let entry = format!("InvalidShapes::{method}");
        if method == "ProductOverflow" {
            let error = compile::object(&resolver, Some(&entry)).unwrap_err();
            assert!(
                error.contains("insufficient physical WRAM") && error.contains("IL_"),
                "{error}"
            );
            continue;
        }
        let object = compile::object(&resolver, Some(&entry)).unwrap();
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
        let result = execute_mode(
            host,
            directory.path(),
            &linked,
            if method == "Capacity" {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            if method == "Capacity" {
                "--native-region:CF00"
            } else {
                "--native-heap"
            },
        );
        let fields: Vec<_> = result.split_whitespace().collect();
        assert_eq!(fields[0], fault, "{entry}: {result}");
        assert_eq!(fields[3], "OK", "{entry}: static guard");
        if method == "Capacity" {
            assert_eq!(&fields[1..3], &["00001081", "CF00"]);
            let mut expected = vec![0x5a; 4352];
            expected[..4231].fill(0);
            expected[..6].copy_from_slice(&[0x81, 0x10, 65, 0, 65, 0]);
            super::assert_heap_snapshot(fields[4], &expected, &entry);
        }
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], &entry);
        }
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn rectangular_runtime_members_require_exact_signatures(host: &Host, #[case] configuration: &str) {
    let (_directory, images) = gameboy_images(host, configuration, "RectangularArrays");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let metadata = resolver.assembly(0).unwrap().metadata();
    let heap = metadata.blobs.data();
    let positions: Vec<_> = images[0]
        .windows(heap.len())
        .enumerate()
        .filter_map(|(offset, bytes)| (bytes == heap).then_some(offset))
        .collect();
    assert_eq!(positions.len(), 1);
    for (name, entry) in [
        (".ctor", "Program::Main"),
        ("Get", "Program::Main"),
        ("Set", "Program::Main"),
        ("Address", "BorrowedElements::Main"),
    ] {
        let expected: &[u8] = match name {
            ".ctor" => &[0x20, 2, 1, 8, 8],
            "Get" => &[0x20, 2, 5, 8, 8],
            "Set" => &[0x20, 3, 1, 8, 8, 5],
            "Address" => &[0x20, 2, 0x10, 8, 8, 8],
            _ => unreachable!(),
        };
        let members: Vec<_> = metadata
            .member_refs
            .iter()
            .filter(|member| {
                member.class.table == Some(clrmeta::TableId::TypeSpec)
                    && metadata.strings.get(member.name).unwrap() == name
                    && metadata.blobs.get(member.signature).unwrap() == expected
            })
            .collect();
        assert!(!members.is_empty(), "{name}");
        for mutation in ["static", "wrong-dimension"] {
            let mut image = images[0].clone();
            for member in &members {
                let signature = metadata.blobs.get(member.signature).unwrap();
                assert!(signature.len() < 128);
                assert_eq!(&signature[..2], &[0x20, if name == "Set" { 3 } else { 2 }]);
                let offset = positions[0] + member.signature as usize + 1;
                if mutation == "static" {
                    image[offset] = 0;
                } else {
                    assert_eq!(
                        signature[signature.len() - if name == "Set" { 2 } else { 1 }],
                        8
                    );
                    image[offset + signature.len() - if name == "Set" { 2 } else { 1 }] = 10;
                }
            }
            let resolver = Resolver::new(&[&image, &images[1]]).unwrap();
            let error = compile::object(&resolver, Some(entry)).unwrap_err();
            assert!(
                error.contains("Invalid rectangular array")
                    && error.contains("signature")
                    && error.contains("IL_"),
                "{name}/{mutation}: {error}"
            );
        }
    }
    let shape = metadata
        .type_specs
        .iter()
        .find(|shape| metadata.blobs.get(shape.signature).unwrap() == [0x14, 5, 2, 0, 2, 0, 0])
        .unwrap();
    let shape_offset = positions[0] + shape.signature as usize + 1;
    for (name, replacement) in [
        ("lower-bound", [0x14, 5, 2, 0, 2, 0, 2]),
        ("explicit-size", [0x14, 5, 2, 1, 3, 1, 0]),
    ] {
        let mut image = images[0].clone();
        image[shape_offset..shape_offset + 7].copy_from_slice(&replacement);
        let resolver = Resolver::new(&[&image, &images[1]]).unwrap();
        let error = compile::object(&resolver, Some("Program::Main")).unwrap_err();
        assert!(
            error.contains("unsized zero-based rank2") && error.contains("IL_"),
            "{name}: {error}"
        );
    }
    let query = metadata
        .member_refs
        .iter()
        .find(|member| metadata.strings.get(member.name).unwrap() == "GetLength")
        .unwrap();
    assert_eq!(
        metadata.blobs.get(query.signature).unwrap(),
        &[0x20, 1, 8, 8]
    );
    for (name, offset, byte) in [
        ("static", 0, 0),
        ("wide-dimension", 3, 10),
        ("wide-result", 2, 10),
    ] {
        let mut image = images[0].clone();
        image[positions[0] + query.signature as usize + 1 + offset] = byte;
        let resolver = Resolver::new(&[&image, &images[1]]).unwrap();
        let error = compile::object(&resolver, Some("Program::Main")).unwrap_err();
        assert!(
            error.contains("Invalid System.Array GetLength signature") && error.contains("IL_"),
            "{name}: {error}"
        );
    }
}
