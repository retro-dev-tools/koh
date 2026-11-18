use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn video_lifecycle_and_joypad_edges_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "VideoJoypad");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in ["Composition", "LayerTransitions", "Frames", "Held"] {
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
            "__koh_exit",
            if entry == "Held" {
                "--native-framework-input"
            } else {
                "--native-video-lifecycle"
            },
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
        let packet: Vec<_> = fields[4].as_bytes()[0xf00 * 2..]
            .chunks_exact(2)
            .map(|bytes| u8::from_str_radix(std::str::from_utf8(bytes).unwrap(), 16).unwrap())
            .collect();
        let mut expected = [0x5a; 256];
        match entry {
            "Held" => expected[..10].copy_from_slice(&[1, 0, 0, 0, 2, 0, 2, 1, 1, 0]),
            "LayerTransitions" => {
                assert_eq!(packet[3] & 0x80, 0, "stopped LCD");
                expected[..7].copy_from_slice(&[0xf7, 0xf5, 0xd5, packet[3], 0x53, 0x73, 0xf3])
            }
            "Frames" => {
                eprintln!("Frame markers {}", fields.last().unwrap());
                assert_eq!([packet[0], packet[2], packet[4]], [1, 2, 0]);
                for offset in [1, 3, 5] {
                    assert!((144..=153).contains(&packet[offset]), "{packet:?}");
                }
                expected[..6].copy_from_slice(&packet[..6]);
            }
            _ => {}
        }
        assert_eq!(packet, expected, "{entry}");
        if entry == "Held" {
            continue;
        }
        let oam = if color { 9 } else { 7 };
        assert_heap_snapshot(fields[oam], &[0; 160], entry);
        let mut vram = [0xa7; 8192];
        vram[0x1800..].fill(0);
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, entry);
        if color {
            assert_heap_snapshot(fields[7], &[0xb8; 8192], entry);
        }
        let lcdc = match entry {
            "Composition" => "F7",
            "LayerTransitions" => "F3",
            _ => "91",
        };
        assert_eq!(fields[oam + 1], format!("{lcdc}E4E4E4"));
        assert_eq!(
            fields[oam + 2],
            match entry {
                "Composition" => "1F280000",
                "LayerTransitions" => "078F0000",
                _ => "00000000",
            }
        );
        assert_eq!(fields[oam + 3], if entry == "Frames" { "1" } else { "0" });
        if entry == "Frames" {
            let markers: Vec<Vec<u64>> = fields[oam + 4]
                .split(',')
                .map(|m| m.split(':').map(|v| v.parse().unwrap()).collect())
                .collect();
            assert_eq!(markers.iter().map(|m| m[0]).collect::<Vec<_>>(), [1, 2, 0]);
            for m in &markers {
                assert!((144..=153).contains(&m[1]), "{markers:?}");
            }
            for pair in markers.windows(2) {
                assert!(
                    (65664..=74784).contains(&(pair[1][3] - pair[0][3])),
                    "{markers:?}"
                );
            }
        } else {
            assert_eq!(fields[oam + 4], "-");
        }
    }
}
