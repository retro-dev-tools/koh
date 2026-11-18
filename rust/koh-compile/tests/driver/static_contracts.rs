use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn expanded_arena_preserves_allocation_shapes_and_shared_bounds(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "StaticContracts");
    let framework = super::delegates::framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    for entry in [
        "LargeWideArray",
        "LargeRectangle",
        "LargeAlignedArray",
        "PageAlignedFullArray",
        "LargeRawAllocation",
        "LargeSharedCollision",
        "LargeRecursiveArray",
        "LargeRecursiveOverflow",
    ] {
        let page = entry == "PageAlignedFullArray";
        let aligned = entry == "LargeAlignedArray" || page;
        let collision = entry == "LargeSharedCollision";
        let overflow = entry == "LargeRecursiveOverflow";
        let capacity = if page {
            4352
        } else if aligned {
            5376
        } else {
            5120
        };
        let top = if page {
            "CF00"
        } else if aligned {
            "CB00"
        } else {
            "CC00"
        };
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
        eprintln!("Execute {entry}");
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            if collision || overflow {
                "__koh_abort"
            } else {
                "__koh_exit"
            },
            &format!("--native-region:{top}"),
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(
            fields[0],
            if collision {
                "05"
            } else if overflow {
                "01"
            } else {
                "00"
            },
            "{entry}"
        );
        assert_eq!(fields[3], "OK", "{entry}");
        if !collision && !overflow {
            assert_eq!(&fields[1..3], &["00000010", top], "{entry}");
        }
        let mut expected = vec![0x5a; capacity];
        if entry == "LargeRawAllocation" {
            expected[120] = 7;
            expected[5119] = 9;
        } else {
            let header = if aligned { 254 } else { 0 };
            let payload = header + if entry == "LargeRectangle" { 6 } else { 2 };
            let count: u16 = if page {
                4096
            } else if entry == "LargeWideArray" {
                625
            } else {
                5000
            };
            expected[header..header + 2].copy_from_slice(&count.to_le_bytes());
            if entry == "LargeRectangle" {
                expected[2..6].copy_from_slice(&[50, 0, 100, 0]);
            }
            let bytes = if page { 4096 } else { 5000 };
            expected[payload..payload + bytes].fill(0);
            if entry == "LargeWideArray" {
                expected[payload..payload + 8].copy_from_slice(&7i64.to_le_bytes());
                expected[payload + 4992..payload + 5000].copy_from_slice(&9i64.to_le_bytes());
            } else {
                expected[payload] = 7;
                expected[payload + bytes - 1] = 9;
            }
        }
        assert_heap_snapshot(fields[4], &expected, entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for (entry, diagnostic) in [
        ("ImpossibleArena", "insufficient physical WRAM"),
        ("InsufficientStack", "including IRQ reserve"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(diagnostic) && error.contains("IL_"),
            "{error}"
        );
    }
}

#[rstest]
#[case("Debug", false, false)]
#[case("Release", false, false)]
#[case("Debug", true, false)]
#[case("Release", true, false)]
#[case("Debug", true, true)]
#[case("Release", true, true)]
fn large_static_array_executes_across_wram_boundary(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
    #[case] pinned: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "StaticContracts");
    let framework = super::delegates::framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    let entry = if pinned {
        "Program::LargePinnedArray"
    } else {
        "Program::LargeArray"
    };
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
    let text = execute_mode(
        host,
        directory.path(),
        &linked,
        "__koh_exit",
        "--native-region:CC00",
    );
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "00000010", "CC00", "OK"]);
    let mut expected = vec![0x5a; 5120];
    expected[..2].copy_from_slice(&5000u16.to_le_bytes());
    expected[2..5002].fill(0);
    expected[2] = 7;
    expected[5001] = 9;
    assert_heap_snapshot(fields[4], &expected, "5000-byte array");
    if color {
        let mut other = [0x6b; 4096];
        if pinned {
            other[0xff0] = 0xa3;
        }
        assert_heap_snapshot(fields[5], &other, "other WRAM bank");
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn static_storage_preserves_values_mutability_identity_and_initialization(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "StaticContracts");
    let framework = super::delegates::framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    for (entry, expected, lcdc, bgp) in [
        ("Counter", vec![0, 5, 0], Some(5), None),
        (
            "Table",
            vec![30, 50, 99, 10, 20, 99, 40, 50],
            Some(30),
            Some(50),
        ),
        ("Constant", vec![42], Some(42), None),
        (
            "MutableArray",
            vec![0, 0, 7, 9, 0, 0, 7, 9],
            Some(7),
            Some(9),
        ),
        (
            "CachedLambda",
            [6i32, 6, 1, 2]
                .iter()
                .flat_map(|v| v.to_le_bytes())
                .collect(),
            None,
            Some(6),
        ),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|e| panic!("{entry}: {e}"));
        if entry == "Constant" {
            assert!(
                !object.sections.iter().any(|s| s.name == ".koh.statics"),
                "literal-only entry needs no static RAM"
            );
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
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-video",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut packet = [0x5au8; 256];
        packet[..expected.len()].copy_from_slice(&expected);
        assert_heap_snapshot(&fields[4][0xf00 * 2..], &packet, entry);
        let oam = if color { 9 } else { 7 };
        assert_heap_snapshot(fields[oam], &[0xc9; 160], entry);
        if let Some(value) = lcdc {
            assert_eq!(&fields[oam + 1][..2], format!("{value:02X}"), "{entry}");
        }
        if let Some(value) = bgp {
            assert_eq!(&fields[oam + 1][2..4], format!("{value:02X}"), "{entry}");
        }
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &[0xa7; 8192], entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
            assert_heap_snapshot(fields[7], &[0xb8; 8192], entry);
        }
    }
}
