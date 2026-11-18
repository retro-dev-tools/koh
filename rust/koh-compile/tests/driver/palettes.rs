use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

fn rgb(r: u16, g: u16, b: u16) -> u16 {
    (r & 31) | ((g & 31) << 5) | ((b & 31) << 10)
}
fn set(bytes: &mut [u8], slot: usize, colors: [u16; 4]) {
    for (i, color) in colors.iter().enumerate() {
        bytes[slot * 8 + i * 2..slot * 8 + i * 2 + 2].copy_from_slice(&color.to_le_bytes());
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn palette_rgb_storage_and_ports_match_authored_colors(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "PaletteContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "Combined",
        "ObjectOne",
        "ColorOne",
        "HighSlots",
        "LastSlot",
        "Packed",
        "OnColorOne",
        "DirectBounds",
        "Sweep",
        "ArmedRaw",
        "ArmedSpeed",
    ] {
        if !color && matches!(entry, "DirectBounds" | "Sweep" | "ArmedRaw" | "ArmedSpeed") {
            continue;
        }
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
            "--native-palettes",
        );
        let f: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&f[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let oam = if color { 9 } else { 7 };
        assert_heap_snapshot(f[oam], &[0; 160], entry);
        let mut bg = [0x3d; 64];
        let mut obj = [0x6c; 64];
        let gray = [0x7fff, 0x56b5, 0x294a, 0];
        set(&mut bg, 0, gray);
        set(&mut obj, 0, gray);
        let primary = [0, 0x001f, 0x03e0, 0x7c00];
        let mut bi = 7;
        let mut oi = 7;
        match entry {
            "Combined" => {
                set(&mut bg, 0, [rgb(10, 20, 30), 0x1f, 0x3e0, 0x7c00]);
                set(&mut obj, 0, primary);
            }
            "ObjectOne" => {
                set(&mut obj, 1, primary);
                oi = 15;
            }
            "ColorOne" | "OnColorOne" | "ArmedSpeed" => {
                set(&mut bg, 1, [0x7fff, rgb(20, 25, 20), rgb(8, 14, 8), 0]);
                set(&mut obj, 1, primary);
                bi = 15;
                oi = 15;
            }
            "HighSlots" => {
                for slot in [0, 2] {
                    set(&mut bg, slot, [0x7fff, 0x1f, 0x3e0, 0x7c00]);
                }
                for slot in [0, 1, 2] {
                    set(&mut obj, slot, primary);
                }
                bi = 23;
                oi = 23;
            }
            "LastSlot" => {
                set(
                    &mut bg,
                    7,
                    [rgb(3, 4, 5), rgb(6, 7, 8), rgb(9, 10, 11), rgb(12, 13, 14)],
                );
                set(&mut obj, 7, [0x1f, 0x3e0, 0x7c00, 0x1234]);
                bi = 63;
                oi = 63;
            }
            "DirectBounds" | "ArmedRaw" => {
                bg[..2].copy_from_slice(&0x1fu16.to_le_bytes());
                bg[62..].copy_from_slice(&0x1234u16.to_le_bytes());
                obj[..2].copy_from_slice(&0x3e0u16.to_le_bytes());
                obj[62..].copy_from_slice(&0x4567u16.to_le_bytes());
                bi = 63;
                oi = 63;
            }
            "Sweep" => {
                for slot in 0..8 {
                    let i = 24 + slot as u16;
                    set(
                        &mut bg,
                        slot,
                        [rgb(i, 1, 2), rgb(3, i, 4), rgb(5, 6, i), rgb(i, i, i)],
                    );
                    set(
                        &mut obj,
                        slot,
                        [
                            rgb(31 - i, i, 0),
                            rgb(0, 31 - i, i),
                            rgb(i, 0, 31 - i),
                            rgb(i, 31 - i, i),
                        ],
                    );
                }
                bi = 63;
                oi = 63;
            }
            _ => {}
        }
        let mut packet = [0x5a; 256];
        if entry == "Packed" {
            for i in 0..32 {
                let packed = rgb(
                    (i * 9) as u8 as u16,
                    (i * 17) as u8 as u16,
                    (255 - i * 3) as u16,
                );
                packet[i * 2..i * 2 + 2].copy_from_slice(&packed.to_le_bytes());
            }
            packet[64..66].copy_from_slice(&rgb(31, 20, 5).to_le_bytes());
        } else if color {
            packet[..4].copy_from_slice(&[bg[bi], obj[oi], bi as u8, oi as u8]);
        }
        if entry.starts_with("Armed") {
            packet[4] = 0xff;
            assert_eq!(
                f[oam + 9],
                "1:1",
                "{entry}: preserved double speed and armed switch"
            );
        }
        assert_heap_snapshot(&f[4][0xf00 * 2..], &packet, entry);
        if color {
            assert_heap_snapshot(f[5], &[0x6b; 4096], entry);
            assert_heap_snapshot(f[oam + 3], &obj, entry);
            assert_heap_snapshot(f[oam + 6], &bg, entry);
            assert_eq!(f[oam + 5], format!("{oi:02X}:{:02X}", obj[oi]), "{entry}");
            assert_eq!(f[oam + 7], format!("{bi:02X}:{:02X}", bg[bi]), "{entry}");
        }
        let dmg = if color {
            "E4E4E4"
        } else {
            match entry {
                "Combined" => "1BC6E4",
                "ObjectOne" => "E4E493",
                "HighSlots" => "1B1B1B",
                _ => "E4E4E4",
            }
        };
        assert_eq!(f[oam + 1], format!("91{dmg}"), "{entry}");
        assert_eq!(
            f[oam + 8],
            if entry == "Packed" { "9F16" } else { "0000" },
            "{entry}"
        );
    }
    for entry in [
        "InvalidMissingPort",
        "InvalidPort",
        "InvalidIndexType",
        "InvalidColorType",
        "InvalidReturn",
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains("Invalid Koh palettewrite intrinsic contract") && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
    for entry in if color {
        vec!["OddIndex", "HighIndex"]
    } else {
        vec!["DmgOnly"]
    } {
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
            "__koh_abort",
            "--native-palettes-trap",
        );
        let f: Vec<_> = text.split_whitespace().collect();
        assert_eq!(f[0], "09", "{entry}");
        assert_eq!(f[3], "OK", "{entry}");
        if color {
            let mut bg = [0x3d; 64];
            let mut obj = [0x6c; 64];
            let gray = [0x7fff, 0x56b5, 0x294a, 0];
            set(&mut bg, 0, gray);
            set(&mut obj, 0, gray);
            assert_heap_snapshot(f[12], &obj, entry);
            assert_heap_snapshot(f[15], &bg, entry);
            assert_eq!(f[14], "07:00", "{entry}");
            assert_eq!(f[16], "07:00", "{entry}");
            assert_heap_snapshot(f[5], &[0x6b; 4096], entry);
        }
    }
}
