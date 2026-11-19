use std::{collections::HashMap, fmt::Write, io::Cursor, path::Path};

struct Sheet {
    name: String,
    tiles: Vec<u8>,
    palette: [u16; 4],
}

fn convert(path: &Path) -> Result<Sheet, String> {
    let bytes = super::read(path, 64 * 1024 * 1024)?;
    let fail = |message: String| format!("'{}': {message}", path.display());
    let mut decoder = png::Decoder::new(Cursor::new(bytes));
    decoder.set_transformations(png::Transformations::IDENTITY);
    let mut reader = decoder.read_info().map_err(|e| fail(e.to_string()))?;
    let info = reader.info();
    if info.bit_depth != png::BitDepth::Eight {
        return Err(fail(format!(
            "bit depth {} is unsupported (8 only).",
            info.bit_depth as u8
        )));
    }
    if !matches!(
        info.color_type,
        png::ColorType::Rgb | png::ColorType::Indexed | png::ColorType::Rgba
    ) {
        return Err(fail(format!(
            "color type {} is unsupported (RGB, indexed, RGBA).",
            info.color_type as u8
        )));
    }
    if info.interlaced {
        return Err(fail("interlaced PNGs are unsupported.".into()));
    }
    let (width, height) = (info.width as usize, info.height as usize);
    if !width.is_multiple_of(8) || !height.is_multiple_of(8) {
        return Err(fail(format!(
            "dimensions {width}x{height} are not a multiple of 8."
        )));
    }
    let palette = info.palette.clone();
    let size = reader
        .output_buffer_size()
        .ok_or_else(|| fail("image is too large.".into()))?;
    if size > 64 * 1024 * 1024 {
        return Err(fail("image is too large.".into()));
    }
    let mut data = vec![0; size];
    let frame = reader
        .next_frame(&mut data)
        .map_err(|e| fail(e.to_string()))?;
    reader.finish().map_err(|e| fail(e.to_string()))?;
    let mut pixels = Vec::with_capacity(width * height);
    for pixel in data[..frame.buffer_size()].chunks_exact(frame.color_type.samples()) {
        let rgba = match frame.color_type {
            png::ColorType::Rgb => [pixel[0], pixel[1], pixel[2], 255],
            png::ColorType::Rgba => [pixel[0], pixel[1], pixel[2], pixel[3]],
            _ => {
                let p = palette
                    .as_ref()
                    .ok_or_else(|| fail("indexed PNG without PLTE.".into()))?;
                let i = usize::from(pixel[0]) * 3;
                let rgb = p
                    .get(i..i + 3)
                    .ok_or_else(|| fail("invalid palette index.".into()))?;
                [rgb[0], rgb[1], rgb[2], 255]
            }
        };
        pixels.push(rgba);
    }
    let mut colors = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for pixel in &pixels {
        if seen.insert(*pixel) {
            colors.push(*pixel);
        }
    }
    if colors.len() > 4 {
        return Err(fail(format!(
            "{} distinct colors (a tile sheet is one 4-color CGB palette).",
            colors.len()
        )));
    }
    let luminance = |p: &[u8; 4]| {
        0.2126 * f64::from(p[0]) + 0.7152 * f64::from(p[1]) + 0.0722 * f64::from(p[2])
    };
    colors.sort_by(|a, b| luminance(b).total_cmp(&luminance(a)));
    let indices: HashMap<_, _> = colors.iter().enumerate().map(|(i, p)| (*p, i)).collect();
    let mut tiles = Vec::with_capacity(width * height / 4);
    for ty in (0..height).step_by(8) {
        for tx in (0..width).step_by(8) {
            for y in 0..8 {
                let (mut low, mut high) = (0, 0);
                for x in 0..8 {
                    let index = indices[&pixels[(ty + y) * width + tx + x]];
                    low |= ((index & 1) as u8) << (7 - x);
                    high |= (((index >> 1) & 1) as u8) << (7 - x);
                }
                tiles.extend([low, high]);
            }
        }
    }
    let palette = std::array::from_fn(|i| {
        let p = colors[i.min(colors.len() - 1)];
        let scale = |v| (u16::from(v) * 31 + 127) / 255;
        scale(p[0]) | (scale(p[1]) << 5) | (scale(p[2]) << 10)
    });
    let file_name = path
        .file_name()
        .and_then(|s| s.to_str())
        .ok_or_else(|| fail("file name must be UTF-8.".into()))?;
    let stem = file_name
        .rsplit_once('.')
        .map_or(file_name, |(stem, _)| stem);
    let mut name = String::new();
    for part in stem.split(['-', '_', ' ', '.']).filter(|p| !p.is_empty()) {
        let mut chars = part.chars();
        if let Some(first) = chars.next() {
            name.push(managed_upper(first));
            name.extend(chars);
        }
    }
    if name.is_empty() || name.starts_with(managed_digit) {
        name.insert(0, '_');
    }
    Ok(Sheet {
        name,
        tiles,
        palette,
    })
}

fn managed_digit(c: char) -> bool {
    use unicode_properties::{GeneralCategory, UnicodeGeneralCategory};
    c.len_utf16() == 1 && c.general_category() == GeneralCategory::DecimalNumber
}

fn managed_upper(c: char) -> char {
    if c.len_utf16() != 1 {
        return c;
    }
    match c {
        '\u{131}' => '\u{131}',
        '\u{277}' => '\u{277}',
        '\u{27C}' => '\u{27C}',
        '\u{1F80}' => '\u{1F88}',
        '\u{1F81}' => '\u{1F89}',
        '\u{1F82}' => '\u{1F8A}',
        '\u{1F83}' => '\u{1F8B}',
        '\u{1F84}' => '\u{1F8C}',
        '\u{1F85}' => '\u{1F8D}',
        '\u{1F86}' => '\u{1F8E}',
        '\u{1F87}' => '\u{1F8F}',
        '\u{1F90}' => '\u{1F98}',
        '\u{1F91}' => '\u{1F99}',
        '\u{1F92}' => '\u{1F9A}',
        '\u{1F93}' => '\u{1F9B}',
        '\u{1F94}' => '\u{1F9C}',
        '\u{1F95}' => '\u{1F9D}',
        '\u{1F96}' => '\u{1F9E}',
        '\u{1F97}' => '\u{1F9F}',
        '\u{1FA0}' => '\u{1FA8}',
        '\u{1FA1}' => '\u{1FA9}',
        '\u{1FA2}' => '\u{1FAA}',
        '\u{1FA3}' => '\u{1FAB}',
        '\u{1FA4}' => '\u{1FAC}',
        '\u{1FA5}' => '\u{1FAD}',
        '\u{1FA6}' => '\u{1FAE}',
        '\u{1FA7}' => '\u{1FAF}',
        '\u{1FB3}' => '\u{1FBC}',
        '\u{1FC3}' => '\u{1FCC}',
        '\u{1FF3}' => '\u{1FFC}',
        '\u{AB4B}' => '\u{AB4B}',
        '\u{AB4C}' => '\u{AB4C}',
        _ => {
            let mut upper = c.to_uppercase();
            let capital = upper.next().unwrap();
            if upper.next().is_none() && capital.len_utf16() == 1 {
                capital
            } else {
                c
            }
        }
    }
}

pub(super) fn run() -> Result<(), String> {
    let mut args = std::env::args_os().skip(2);
    let mut namespace = None;
    let mut output = None;
    let mut paths = Vec::new();
    while let Some(arg) = args.next() {
        match arg.to_str() {
            Some("--namespace") if namespace.is_none() => {
                namespace = Some(
                    args.next()
                        .ok_or("Missing namespace")?
                        .into_string()
                        .map_err(|_| "Namespace must be UTF-8")?,
                )
            }
            Some("-o" | "--output") if output.is_none() => {
                output = Some(std::path::PathBuf::from(
                    args.next().ok_or("Missing output")?,
                ))
            }
            Some("--") => {
                paths.extend(args.map(std::path::PathBuf::from));
                break;
            }
            _ => {
                return Err(format!(
                    "Unknown tile-sheet option '{}'",
                    arg.to_string_lossy()
                ));
            }
        }
    }
    let namespace = namespace.ok_or("Namespace is required")?;
    let output = output.ok_or("Output path is required")?;
    let parent = output
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    let output_id = super::identity(&output)?;
    paths.sort_by(|a, b| {
        a.to_string_lossy()
            .encode_utf16()
            .cmp(b.to_string_lossy().encode_utf16())
    });
    let mut sheets = Vec::new();
    for path in paths {
        if super::identity(&path)? == output_id {
            return Err("Output path must be distinct from input PNG files".into());
        }
        sheets.push(convert(&path)?);
    }
    let mut source = String::from("// <auto-generated/>\n");
    writeln!(source, "namespace {namespace};\n\nstatic class Art\n{{").unwrap();
    let mut count = 0;
    for sheet in &sheets {
        let name = &sheet.name;
        let tiles = sheet.tiles.len() / 16;
        count += tiles;
        writeln!(source, "    public const byte {name}TileCount = {tiles};").unwrap();
        for (i, color) in sheet.palette.iter().enumerate() {
            writeln!(
                source,
                "    public const ushort {name}Color{i} = 0x{color:04X};"
            )
            .unwrap();
        }
        writeln!(
            source,
            "    public static readonly byte[] {name}Tiles =\n    {{"
        )
        .unwrap();
        for row in sheet.tiles.chunks(16) {
            source.push_str("        ");
            for byte in row {
                write!(source, "0x{byte:02X}, ").unwrap();
            }
            source.push('\n');
        }
        source.push_str("    };\n\n");
    }
    source.push_str("}\n");
    super::write(&output, source.as_bytes())?;
    println!(
        "Koh tile sheets: {} sheet(s), {count} tile(s) -> {}",
        sheets.len(),
        output.display()
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dotnet_bmp_character_semantics() {
        let directory = tempfile::tempdir().unwrap();
        let project = directory.path().join("CharOracle.csproj");
        std::fs::write(
            &project,
            include_str!("../tests/fixtures/ManagedHost/EmulatorHost.csproj"),
        )
        .unwrap();
        std::fs::write(
            directory.path().join("Program.cs"),
            include_str!("../tests/fixtures/TileSheets/CharOracle.cs"),
        )
        .unwrap();
        let dotnet = std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into());
        let built = std::process::Command::new(&dotnet)
            .current_dir(directory.path())
            .arg("build")
            .arg(&project)
            .args(["-c", "Release", "--nologo", "-v:q", "-m:1"])
            .output()
            .unwrap();
        assert!(
            built.status.success(),
            "{}{}",
            String::from_utf8_lossy(&built.stdout),
            String::from_utf8_lossy(&built.stderr)
        );
        let result = std::process::Command::new(&dotnet)
            .current_dir(directory.path())
            .arg(directory.path().join("bin/Release/net10.0/CharOracle.dll"))
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        assert_eq!(result.stdout.len(), 65536 * 3);
        let mut checked = 0;
        for code in 0..=65535 {
            let Some(c) = char::from_u32(code) else {
                continue;
            };
            let i = code as usize * 3;
            assert_eq!(
                managed_upper(c) as u32,
                u16::from_le_bytes([result.stdout[i], result.stdout[i + 1]]) as u32,
                "uppercase U+{code:04X}"
            );
            assert_eq!(
                managed_digit(c),
                result.stdout[i + 2] != 0,
                "digit U+{code:04X}"
            );
            checked += 1;
        }
        eprintln!("Compared {checked} BMP scalar uppercase and digit results; 0 differences");
    }

    #[test]
    fn names_match_managed_utf16_casing_and_digit_categories() {
        let directory = tempfile::tempdir().unwrap();
        for (stem, expected) in [
            ("Ⅷ", "Ⅷ"),
            ("𐐨", "𐐨"),
            ("١tiles", "_١tiles"),
            ("𝟙tiles", "𝟙tiles"),
            ("ß", "ß"),
            ("ᾀ", "ᾈ"),
            ("ı", "ı"),
            ("\u{277}", "\u{277}"),
            ("\u{27C}", "\u{27C}"),
            ("\u{AB4B}", "\u{AB4B}"),
            ("\u{AB4C}", "\u{AB4C}"),
            ("---", "_"),
            ("", "_"),
            ("my-sheet", "MySheet"),
        ] {
            let path = directory.path().join(format!("{stem}.png"));
            std::fs::write(
                &path,
                include_bytes!("../tests/fixtures/TileSheets/four-colors.png"),
            )
            .unwrap();
            assert_eq!(convert(&path).unwrap().name, expected, "{stem}");
        }
    }

    #[test]
    fn png_formats_filters_alpha_and_tile_order() {
        for color in [
            png::ColorType::Rgb,
            png::ColorType::Indexed,
            png::ColorType::Rgba,
        ] {
            for filter in [
                png::Filter::NoFilter,
                png::Filter::Sub,
                png::Filter::Up,
                png::Filter::Avg,
                png::Filter::Paeth,
            ] {
                let directory = tempfile::tempdir().unwrap();
                let path = directory.path().join("2-two_tiles.png");
                let mut data = Vec::new();
                for _y in 0..8 {
                    for x in 0..16 {
                        let dark = x >= 8;
                        match color {
                            png::ColorType::Indexed => data.push(u8::from(dark)),
                            png::ColorType::Rgb => data.extend([if dark { 0 } else { 255 }; 3]),
                            _ => data.extend([255, 255, 255, if dark { 0 } else { 255 }]),
                        }
                    }
                }
                let mut encoder = png::Encoder::new(std::fs::File::create(&path).unwrap(), 16, 8);
                encoder.set_color(color);
                encoder.set_depth(png::BitDepth::Eight);
                encoder.set_filter(filter);
                if color == png::ColorType::Indexed {
                    encoder.set_palette(vec![255, 255, 255, 0, 0, 0]);
                    encoder.set_trns(vec![0, 255]);
                }
                encoder
                    .write_header()
                    .unwrap()
                    .write_image_data(&data)
                    .unwrap();
                let sheet = convert(&path).unwrap();
                assert_eq!(sheet.name, "_2TwoTiles");
                assert_eq!(&sheet.tiles[..16], &[0; 16]);
                assert_eq!(&sheet.tiles[16..], &[255, 0].repeat(8));
                assert_eq!(sheet.palette[0], 0x7fff);
                assert_eq!(
                    sheet.palette[3],
                    if color == png::ColorType::Rgba {
                        0x7fff
                    } else {
                        0
                    }
                );
            }
        }
    }

    #[test]
    fn rejects_unsupported_and_corrupt_images() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("bad.png");
        let mut encoder = png::Encoder::new(std::fs::File::create(&path).unwrap(), 8, 8);
        encoder.set_color(png::ColorType::Grayscale);
        encoder
            .write_header()
            .unwrap()
            .write_image_data(&[0; 64])
            .unwrap();
        assert!(
            convert(&path)
                .err()
                .unwrap()
                .contains("color type 0 is unsupported")
        );
        std::fs::write(&path, b"not a png").unwrap();
        assert!(convert(&path).err().unwrap().contains("bad.png"));
    }

    #[test]
    fn rejects_depth_and_dimensions() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("bad.png");
        for (width, depth, expected) in [
            (8, png::BitDepth::Sixteen, "bit depth 16"),
            (7, png::BitDepth::Eight, "dimensions 7x8"),
        ] {
            let mut encoder = png::Encoder::new(std::fs::File::create(&path).unwrap(), width, 8);
            encoder.set_color(png::ColorType::Rgb);
            encoder.set_depth(depth);
            let size = width as usize
                * 8
                * 3
                * if depth == png::BitDepth::Sixteen {
                    2
                } else {
                    1
                };
            encoder
                .write_header()
                .unwrap()
                .write_image_data(&vec![0; size])
                .unwrap();
            assert!(convert(&path).err().unwrap().contains(expected));
        }
    }
}
