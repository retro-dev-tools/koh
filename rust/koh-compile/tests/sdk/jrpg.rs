use std::{
    collections::{BTreeMap, BTreeSet},
    ffi::OsString,
    fs::File,
    io::BufReader,
    path::{Path, PathBuf},
};

pub(super) fn curated(directory: &Path) -> BTreeMap<OsString, Vec<u8>> {
    std::fs::read_dir(directory)
        .unwrap()
        .map(|entry| entry.unwrap())
        .filter(|entry| entry.path().is_file())
        .map(|entry| (entry.file_name(), std::fs::read(entry.path()).unwrap()))
        .collect()
}

struct Image {
    width: usize,
    height: usize,
    pixels: Vec<[u8; 4]>,
}

impl Image {
    fn pixel(&self, x: usize, y: usize) -> [u8; 4] {
        assert!(x < self.width && y < self.height);
        self.pixels[y * self.width + x]
    }
}

fn png(path: &Path) -> Image {
    let mut decoder = png::Decoder::new(BufReader::new(File::open(path).unwrap()));
    decoder.set_transformations(png::Transformations::EXPAND | png::Transformations::STRIP_16);
    let mut reader = decoder.read_info().unwrap();
    let size = reader.output_buffer_size().unwrap();
    assert!(size <= 160 * 144 * 4);
    let mut buffer = vec![0; size];
    let info = reader.next_frame(&mut buffer).unwrap();
    let channels = info.color_type.samples();
    let pixels = buffer[..info.buffer_size()]
        .chunks_exact(channels)
        .map(|pixel| match info.color_type {
            png::ColorType::Rgb => [pixel[0], pixel[1], pixel[2], 255],
            png::ColorType::Rgba => pixel.try_into().unwrap(),
            png::ColorType::Grayscale => [pixel[0], pixel[0], pixel[0], 255],
            png::ColorType::GrayscaleAlpha => [pixel[0], pixel[0], pixel[0], pixel[1]],
            png::ColorType::Indexed => panic!("PNG palette was not expanded"),
        })
        .collect();
    Image {
        width: info.width as usize,
        height: info.height as usize,
        pixels,
    }
}

fn rgb555(pixel: [u8; 4]) -> u16 {
    assert_eq!(pixel[3], 255);
    let nearest = |component: u8| {
        (0u16..=31)
            .min_by_key(|&level| (u16::from(component) * 31).abs_diff(level * 255))
            .unwrap()
    };
    nearest(pixel[0]) | (nearest(pixel[1]) << 5) | (nearest(pixel[2]) << 10)
}

#[rstest::rstest]
#[case([0,0,0,255],0)]
#[case([255,255,255,255],0x7fff)]
#[case([255,0,0,255],0x001f)]
#[case([0,255,0,255],0x03e0)]
#[case([0,0,255,255],0x7c00)]
#[case([160,208,96,255],0x3333)]
fn rgb555_uses_nearest_normalized_channels(#[case] input: [u8; 4], #[case] expected: u16) {
    assert_eq!(rgb555(input), expected);
}

#[test]
fn tile_planes_follow_the_hardware_bit_order() {
    let mut bytes = [0u8; 16];
    bytes[..2].copy_from_slice(&[0x3c, 0x7e]);
    let actual: Vec<_> = (0..8).map(|x| tile(&bytes, 0, x, 0)).collect();
    assert_eq!(actual, [0, 2, 3, 3, 3, 3, 2, 0]);
}

fn rgba(color: u16) -> [u8; 4] {
    let expand = |shift: u8| {
        let channel = ((color >> shift) & 31u16) as u8;
        (channel << 3) | (channel >> 2)
    };
    [expand(0), expand(5), expand(10), 255]
}

fn palette(bytes: &[u8], slot: usize, index: usize) -> u16 {
    assert_eq!(bytes.len(), 64);
    let offset = slot * 8 + index * 2;
    u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap()) & 0x7fff
}

fn tile(vram: &[u8], index: usize, x: usize, y: usize) -> usize {
    let offset = index * 16 + y * 2;
    usize::from(((vram[offset] >> (7 - x)) & 1) | (((vram[offset + 1] >> (7 - x)) & 1) << 1))
}

fn file(directory: &Path, label: &str, extension: &str) -> PathBuf {
    directory.join(format!("game.gb.{label}.{extension}"))
}

fn frame(directory: &Path, label: &str) -> Image {
    let bytes = std::fs::read(file(directory, label, "rgba")).unwrap();
    assert_eq!(bytes.len(), 160 * 144 * 4);
    Image {
        width: 160,
        height: 144,
        pixels: bytes
            .chunks_exact(4)
            .map(|p| p.try_into().unwrap())
            .collect(),
    }
}

fn region(
    frame: &Image,
    origin: [usize; 2],
    size: [usize; 2],
    expected: impl Fn(usize, usize) -> u16,
) {
    for y in 0..size[1] {
        for x in 0..size[0] {
            assert_eq!(
                frame.pixel(origin[0] + x, origin[1] + y),
                rgba(expected(x, y)),
                "screen pixel ({},{})",
                origin[0] + x,
                origin[1] + y
            );
        }
    }
}

fn grass(image: &Image, column: usize, row: usize, x: usize, y: usize) -> u16 {
    let hash = ((column + x / 8) * 7 + (row + y / 8) * 13) & 7;
    let variant = if hash < 5 { 0 } else { hash - 4 };
    rgb555(image.pixel(variant * 8 + x % 8, y % 8))
}

pub(super) fn output_directory(configuration: &str) -> PathBuf {
    let base = std::env::current_exe()
        .unwrap()
        .parent()
        .unwrap()
        .join("TestResults/gb-jrpg");
    std::fs::create_dir_all(&base).unwrap();
    tempfile::Builder::new()
        .prefix(&format!("{configuration}-"))
        .tempdir_in(base)
        .unwrap()
        .keep()
}

pub(super) fn check(source: &Path, directory: &Path, configuration: &str) {
    let sheets: BTreeMap<_, _> = ["grass", "wall", "water", "tree", "chars", "monsters", "ui"]
        .into_iter()
        .map(|name| (name, png(&source.join("art").join(format!("{name}.png")))))
        .collect();
    let initial = std::fs::read(file(directory, "dialogueInitial", "vram")).unwrap();
    let colors = std::fs::read(file(directory, "dialogueInitial", "palette")).unwrap();
    for label in ["dialogueInitial", "closed", "won"] {
        let map = std::fs::read(file(directory, label, "vram")).unwrap();
        for (column, text) in [(1, "HP"), (8, "LV")] {
            for (index, character) in text.bytes().enumerate() {
                assert_eq!(
                    map[0x1800 + column + index],
                    46 + character - b' ',
                    "{label}: HUD"
                );
            }
        }
    }
    for (name, first, slot) in [
        ("grass", 0, 0),
        ("wall", 4, 1),
        ("water", 6, 2),
        ("tree", 8, 5),
        ("chars", 12, 3),
        ("monsters", 20, 4),
        ("ui", 36, 6),
    ] {
        let image = &sheets[name];
        assert_eq!((image.width % 8, image.height % 8), (0, 0));
        let expected: BTreeSet<_> = image.pixels.iter().map(|&p| rgb555(p)).collect();
        let actual: BTreeSet<_> = (0..4).map(|index| palette(&colors, slot, index)).collect();
        assert_eq!(actual, expected, "{name}: PNG palette colors");
        for y in 0..image.height {
            for x in 0..image.width {
                let index = first + (y / 8) * (image.width / 8) + x / 8;
                assert_eq!(
                    palette(&colors, slot, tile(&initial, index, x % 8, y % 8)),
                    rgb555(image.pixel(x, y)),
                    "{name}: uploaded pixel ({x},{y})"
                );
            }
        }
    }
    for (column, row, index, slot) in [
        (12, 2, 8, 5),
        (16, 6, 6, 2),
        (10, 4, 14, 3),
        (10, 5, 18, 3),
        (12, 16, 8, 5),
    ] {
        let offset = 0x1800 + row * 32 + column;
        assert_eq!(initial[offset], index);
        assert_eq!(initial[0x2000 + offset], slot);
    }
    let chars = &sheets["chars"];
    let background = rgb555(chars.pixel(0, 0));
    assert_eq!(background, rgb555(sheets["grass"].pixel(0, 0)));
    for (label, cell_x, cell_y) in [
        ("dialogueInitial", 1, 1),
        ("quickMove", 2, 1),
        ("walk3", 4, 1),
        ("closed", 4, 1),
        ("won", 3, 1),
    ] {
        let oam = std::fs::read(file(directory, label, "oam")).unwrap();
        let video = std::fs::read(file(directory, label, "video")).unwrap();
        assert_eq!(video.len(), 3);
        assert_eq!(video[0] & 0x86, 0x82, "{label}: LCD/8x8/OBJ enable");
        assert_eq!(&video[1..], &[0, 0], "{label}: scroll");
        assert_eq!(oam.len(), 160);
        for (index, (dx, dy, tile)) in [(0, 0, 12), (8, 0, 13), (0, 8, 16), (8, 8, 17)]
            .into_iter()
            .enumerate()
        {
            assert_eq!(
                &oam[index * 4..index * 4 + 4],
                &[cell_y * 16 + 32 + dy, cell_x * 16 + 8 + dx, tile, 0],
                "{label}: OBJ{index}"
            );
        }
        assert!(
            (4..40).all(|index| oam[index * 4] == 0),
            "{label}: unused OBJs hidden"
        );
        let object_colors = std::fs::read(file(directory, label, "objpalette")).unwrap();
        assert_eq!(
            palette(&object_colors, 0, 0),
            background,
            "{label}: transparent OBJ color"
        );
        let actual = frame(directory, label);
        region(
            &actual,
            [usize::from(cell_x) * 16, usize::from(cell_y) * 16 + 16],
            [16, 16],
            |x, y| {
                let color = rgb555(chars.pixel(x, y));
                if color == background {
                    grass(
                        &sheets["grass"],
                        usize::from(cell_x) * 2,
                        usize::from(cell_y) * 2 + 2,
                        x,
                        y,
                    )
                } else {
                    color
                }
            },
        );
    }
    let moved = std::fs::read(file(directory, "quickMove", "vram")).unwrap();
    for (column, row, index) in [(2, 4, 0), (3, 4, 0), (2, 5, 3), (3, 5, 2)] {
        assert_eq!(
            moved[0x1800 + row * 32 + column],
            index,
            "vacated hero BG cell"
        );
    }
    region(
        &frame(directory, "quickMove"),
        [16, 32],
        [16, 16],
        |x, y| grass(&sheets["grass"], 2, 4, x, y),
    );
    let title = std::fs::read(file(directory, "earlyTitle", "vram")).unwrap();
    for (column, row, text) in [
        (5, 5, "TINY  QUEST"),
        (3, 7, "A MILLMERE TALE"),
        (5, 12, "PRESS START"),
    ] {
        for (index, character) in text.bytes().enumerate() {
            assert_eq!(
                title[0x1800 + row * 32 + column + index],
                46 + character - b' ',
                "title deadline glyph{index}"
            );
        }
    }
    let initial_frame = frame(directory, "dialogueInitial");
    region(&initial_frame, [96, 16], [16, 16], |x, y| {
        rgb555(sheets["tree"].pixel(x, y))
    });
    region(&initial_frame, [80, 32], [16, 16], |x, y| {
        rgb555(chars.pixel(x + 16, y))
    });
    region(&initial_frame, [128, 48], [16, 16], |x, y| {
        let variant = ((x / 8) + (y / 8)) % 2;
        rgb555(sheets["water"].pixel(variant * 8 + x % 8, y % 8))
    });
    for (label, x, y) in [("earlyTitle", 72, 72), ("battle", 72, 32)] {
        let vram = std::fs::read(file(directory, label, "vram")).unwrap();
        assert_eq!(
            vram[0x1800 + (y / 8 + 1) * 32 + x / 8],
            24,
            "{label}: monster bottom row"
        );
        region(&frame(directory, label), [x, y], [16, 16], |x, y| {
            rgb555(sheets["monsters"].pixel(x, y))
        });
    }
    let output = output_directory(configuration);
    for (label, name) in [
        ("earlyTitle", "title"),
        ("dialogueInitial", "overworld"),
        ("page0", "dialogue"),
        ("battle", "battle"),
    ] {
        let original = file(directory, label, "png");
        let decoded = png(&original);
        let actual = frame(directory, label);
        assert_eq!((decoded.width, decoded.height), (160, 144));
        assert!(
            decoded.pixels == actual.pixels,
            "{name}: PNG framebuffer bytes"
        );
        std::fs::copy(original, output.join(format!("{name}.png"))).unwrap();
    }
    eprintln!("JRPG captures: {}", output.display());
}
