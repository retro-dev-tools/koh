use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn scattered_map_commit_drains_and_does_not_replay_clean_state(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MapTransfers");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, base) in [("CommitBackground", 0), ("CommitWindow", 1024)] {
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
            "__koh_exit",
            "--native-map-commit",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut expected = [0u8; 2048];
        for (index, value) in [(98, 7), (115, 9), (325, 4)] {
            expected[base + index] = value;
        }
        let markers: Vec<_> = fields.last().unwrap().split(',').collect();
        assert_eq!(markers.len(), 63, "{entry} frame observations");
        let mut landed = None;
        for (i, marker) in markers.iter().enumerate() {
            let parts: Vec<_> = marker.split(':').collect();
            assert_eq!(parts[0].parse::<usize>().unwrap(), i + 1);
            let transfers = parts[1].parse::<usize>().unwrap();
            assert_eq!(
                transfers,
                usize::from(color),
                "{entry} DMA count at frame {}",
                i + 1
            );
            if i >= 61 {
                expected[98] = 0xAA;
                expected[1024 + 98] = 0xAA;
                assert_heap_snapshot(
                    parts[2],
                    &expected,
                    "clean frame preserves hardware-side changes",
                );
            } else {
                let bytes: Vec<_> = (0..2048)
                    .map(|index| {
                        u8::from_str_radix(&parts[2][index * 2..index * 2 + 2], 16).unwrap()
                    })
                    .collect();
                for (actual, wanted) in bytes.iter().zip(expected) {
                    assert!(
                        *actual == 0 || *actual == wanted,
                        "{entry} unexpected map byte"
                    );
                }
                if bytes == expected && landed.is_none() {
                    landed = Some(i + 1);
                }
                if landed.is_some() {
                    assert_eq!(bytes, expected, "{entry} committed state persists");
                }
            }
        }
        assert!(landed.is_some(), "{entry} did not drain within 61 frames");
        if color {
            assert_eq!(landed, Some(1), "{entry} CGB single-frame commit");
        }
        let mut video = [0xA7; 8192];
        video[0x1800..].copy_from_slice(&expected);
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &video, "VRAM0");
        assert_heap_snapshot(fields[if color { 9 } else { 7 }], &[0; 160], "OAM");
        if color {
            assert_heap_snapshot(fields[7], &[0xB8; 8192], "untouched attribute bank");
            assert_heap_snapshot(fields[5], &[0x6B; 4096], "untouched WRAM2");
        }
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn cgb_frame_reads_the_pinned_shadow_bank_and_restores_the_caller(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "MapTransfers");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(&resolver, Some("Program::BankedFrame")).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(
        &[object],
        &koh_link::LinkOptions {
            cgb: koh_link::CgbMode::Compatible,
            ..Default::default()
        },
    )
    .unwrap();
    let text = execute_mode(
        host,
        directory.path(),
        &linked,
        "__koh_exit",
        "--native-sprites",
    );
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
    assert_eq!(&fields[10][2..4], "02", "caller WRAM bank restored");
    let mut caller = [0x6bu8; 4096];
    caller[0xf00] = 0xa3;
    assert_heap_snapshot(fields[5], &caller, "physical post-frame caller bank");
    assert_heap_snapshot(
        &fields[4][0xf00 * 2..],
        &[0x5a; 256],
        "post-frame store must not reach bank1",
    );
    let mut indices = [0xa7u8; 8192];
    indices[0x1800..].fill(0);
    indices[0x1800] = 42;
    indices[0x1fff] = 77;
    assert_heap_snapshot(fields[6], &indices, "bank1 shadow reaches VRAM");
    assert_heap_snapshot(fields[7], &[0xb8; 8192], "untouched VRAM1");
    assert_heap_snapshot(fields[9], &[0; 160], "bank1 OAM shadow reaches hardware");
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn background_window_attributes_and_board_match_hardware_maps(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MapTransfers");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "BackgroundContract",
        "WindowContract",
        "AttributeContract",
        "BoardContract",
    ] {
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
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-maps",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{entry}: {text}"
        );
        let mut indices = vec![0xa7; 8192];
        indices[0x1800..].fill(0);
        let mut attributes = vec![0xb8; 8192];
        match entry {
            "BackgroundContract" => {
                for row in 3..5 {
                    for column in 2..6 {
                        indices[0x1800 + row * 32 + column] = 7;
                    }
                }
                for row in 0..2 {
                    for column in 0..3 {
                        indices[0x1800 + (1 + row) * 32 + 10 + column] =
                            (10 + row * 3 + column) as u8;
                    }
                }
                indices[0x1800] = 42;
            }
            "WindowContract" => {
                for row in 6..8 {
                    for column in 5..8 {
                        indices[0x1c00 + row * 32 + column] = 9;
                    }
                }
                for row in 0..2 {
                    for column in 0..2 {
                        indices[0x1c00 + row * 32 + column] = (20 + row * 2 + column) as u8;
                    }
                }
                indices[0x1c00 + 15 * 32 + 15] = 77;
            }
            "AttributeContract" if color => {
                attributes[0x1800 + 5 * 32 + 5] = 0x23;
                for row in 0..2 {
                    for column in 0..2 {
                        attributes[0x1800 + row * 32 + column] = 0x80;
                    }
                }
                attributes[0x1c00 + 33] = 0x40;
            }
            "AttributeContract" => {}
            "BoardContract" => {
                for row in 0..4 {
                    for column in 0..4 {
                        for dy in 0..2 {
                            for dx in 0..2 {
                                indices[0x1800 + (3 + row * 3 + dy) * 32 + 3 + column * 4 + dx] = 7;
                            }
                        }
                    }
                }
            }
            _ => unreachable!(),
        }
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &indices, entry);
        if color {
            assert_heap_snapshot(fields[7], &attributes, entry);
        }
        assert_heap_snapshot(
            fields[if color { 9 } else { 7 }],
            &[0; 160],
            "untouched OAM",
        );
        assert_eq!(
            fields[if color { 10 } else { 8 }],
            if entry == "BoardContract" {
                "91E4E4E4"
            } else {
                "11E4E4E4"
            },
            "{entry}: LCD/palette state"
        );
    }
}
