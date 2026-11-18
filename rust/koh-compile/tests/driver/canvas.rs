use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn canvas_pixels_rasterization_pages_and_dma_match_independent_planes(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MapTransfers");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "CanvasPixels",
        "CanvasRect",
        "CanvasDither",
        "CanvasLine",
        "CanvasClippedLine",
        "CanvasTriangle",
        "CanvasDouble",
        "CanvasCgb",
    ] {
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
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-canvas",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{entry}: {text}"
        );
        let large = matches!(entry, "CanvasPixels" | "CanvasLine" | "CanvasClippedLine");
        let width = if large { 2 } else { 1 };
        let on = !matches!(entry, "CanvasPixels" | "CanvasRect" | "CanvasDither");
        let mut vram = vec![0; 8192];
        vram[0x1800..0x1c00].fill(255);
        for row in 0..width {
            for column in 0..width {
                vram[0x1800 + ((18 - width) / 2 + row) * 32 + (20 - width) / 2 + column] =
                    (row * width + column) as u8;
            }
        }
        let mut pixel = |x: usize, y: usize, color: u8| {
            let offset = (y / 8 * width + x / 8) * 16 + y % 8 * 2;
            let mask = 0x80 >> (x % 8);
            if color & 1 != 0 {
                vram[offset] |= mask;
            }
            if color & 2 != 0 {
                vram[offset + 1] |= mask;
            }
        };
        match entry {
            "CanvasPixels" => {
                pixel(0, 0, 3);
                pixel(8, 3, 2);
                pixel(15, 15, 1);
            }
            "CanvasRect" => {
                for y in 0..8 {
                    for x in 0..8 {
                        pixel(x, y, 3);
                    }
                }
            }
            "CanvasDither" => {
                vram[..4].copy_from_slice(&[0x88, 0x77, 0x44, 0xbb]);
            }
            "CanvasLine" | "CanvasClippedLine" => {
                for i in 0..16 {
                    pixel(i, i, 3);
                }
            }
            "CanvasTriangle" => {
                for y in 0..8 {
                    for x in y..8 {
                        pixel(x, y, 3);
                    }
                }
            }
            "CanvasDouble" => {
                vram[0..2].fill(0x40);
                vram[0x1000..0x1002].fill(0x80);
            }
            "CanvasCgb" => pixel(0, 0, 3),
            _ => unreachable!(),
        }
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, entry);
        if color {
            assert_heap_snapshot(fields[7], &[0xb8; 8192], "untouched attributes");
        }
        assert_heap_snapshot(fields[if color { 9 } else { 7 }], &[0; 160], entry);
        assert_eq!(
            fields[if color { 10 } else { 8 }],
            if on { "91E4E4E4" } else { "11E4E4E4" },
            "{entry}"
        );
        assert_eq!(
            fields[if color { 11 } else { 9 }].parse::<usize>().unwrap(),
            if color {
                if entry == "CanvasDouble" { 2 } else { 1 }
            } else {
                0
            },
            "{entry}: GDMA starts"
        );
        if entry == "CanvasDouble" {
            assert_heap_snapshot(
                &fields[4][0xf00 * 2..0xf02 * 2],
                &[0x81, 0x91],
                "both page flips",
            );
        }
    }
}
