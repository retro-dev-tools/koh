use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

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
