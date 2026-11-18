use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn font_pixels_text_layers_and_decimal_layout_match_authored_inputs(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MapTransfers");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let font = std::fs::read_to_string(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../src/Compiler/Koh.GameBoy/Graphics/Font.cs"),
    )
    .unwrap();
    let glyphs: Vec<u8> = font
        .split_once("private static readonly byte[] Glyphs =")
        .unwrap()
        .1
        .lines()
        .filter_map(|line| line.trim().strip_prefix("0x"))
        .map(|line| u8::from_str_radix(line.split(',').next().unwrap(), 16).unwrap())
        .collect();
    assert_eq!(glyphs.len(), 96 * 8);
    assert_eq!(&glyphs[usize::from(b'S' - 0x20) * 8..][..2], &[0x3c, 0x40]);
    for entry in [
        "FontScore",
        "FontWindow",
        "FontRight",
        "FontLeft",
        "FontBytes",
        "FontOffset",
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
        let base = if entry == "FontOffset" { 7u8 } else { 0 };
        let mut vram = vec![0xa7; 8192];
        vram[0x1800..].fill(0);
        for (row, &bitmap) in glyphs.iter().enumerate() {
            vram[usize::from(base) * 16 + row * 2..usize::from(base) * 16 + row * 2 + 2]
                .fill(bitmap);
        }
        let mut draw = |map: usize, col: usize, row: usize, ascii: &[u8]| {
            for (i, &ch) in ascii.iter().enumerate() {
                vram[map + row * 32 + col + i] = base + ch - 0x20;
            }
        };
        match entry {
            "FontScore" => draw(0x1800, 1, 0, b"SCORE"),
            "FontWindow" => draw(0x1c00, 2, 3, b"HI"),
            "FontRight" => draw(0x1800, 0, 0, b" 1234"),
            "FontLeft" => {
                draw(0x1800, 0, 0, b"42");
                draw(0x1800, 0, 1, b"0");
            }
            "FontBytes" => draw(0x1800, 4, 2, b"HI!"),
            "FontOffset" => {
                draw(0x1800, 1, 0, b"SCORE");
                draw(0x1800, 0, 1, b"65535");
                draw(0x1c00, 0, 2, b"  0");
            }
            _ => unreachable!(),
        }
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, entry);
        if color {
            assert_heap_snapshot(fields[7], &[0xb8; 8192], "untouched attribute bank");
        }
        assert_heap_snapshot(
            fields[if color { 9 } else { 7 }],
            &[0; 160],
            "untouched OAM",
        );
        assert_eq!(fields[if color { 10 } else { 8 }], "11E4E4E4");
    }
}
