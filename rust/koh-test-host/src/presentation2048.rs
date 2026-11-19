use crate::common::Result;
use serde_json::Value;
use std::{
    collections::BTreeMap,
    fs::File,
    io::{BufReader, BufWriter, Write},
    path::Path,
};

fn image(path: &Path) -> Result<Vec<u8>> {
    let mut reader =
        png::Decoder::new(BufReader::new(File::open(path).map_err(|e| e.to_string())?))
            .read_info()
            .map_err(|e| e.to_string())?;
    let info = reader.info();
    if (info.width, info.height, info.color_type, info.bit_depth)
        != (160, 144, png::ColorType::Rgba, png::BitDepth::Eight)
    {
        return Err(format!("Expected 160x144 RGBA frame: {}", path.display()));
    }
    let mut bytes = vec![0; reader.output_buffer_size().ok_or("Image too large")?];
    reader.next_frame(&mut bytes).map_err(|e| e.to_string())?;
    Ok(bytes
        .as_chunks::<4>()
        .0
        .iter()
        .flat_map(|p| p[..3].iter().copied())
        .collect())
}

fn save(path: &Path, bytes: &[u8], width: usize, height: usize) -> Result {
    let mut encoder = png::Encoder::new(
        BufWriter::new(File::create(path).map_err(|e| e.to_string())?),
        width as u32,
        height as u32,
    );
    encoder.set_color(png::ColorType::Rgb);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder.write_header().map_err(|e| e.to_string())?;
    writer.write_image_data(bytes).map_err(|e| e.to_string())?;
    writer.finish().map_err(|e| e.to_string())
}

fn blit(canvas: &mut [u8], width: usize, x: usize, y: usize, rgb: &[u8], scale: usize) {
    for sy in 0..144 * scale {
        for sx in 0..160 * scale {
            let source = (sy / scale * 160 + sx / scale) * 3;
            let target = ((y + sy) * width + x + sx) * 3;
            canvas[target..target + 3].copy_from_slice(&rgb[source..source + 3]);
        }
    }
}

fn glyph(c: char) -> [u8; 7] {
    match c {
        'A' => [
            0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001,
        ],
        'B' => [
            0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110,
        ],
        'C' => [
            0b01111, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b01111,
        ],
        'D' => [
            0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110,
        ],
        'E' => [
            0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111,
        ],
        'F' => [
            0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000,
        ],
        'G' => [
            0b01111, 0b10000, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111,
        ],
        'H' => [
            0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001,
        ],
        'I' => [
            0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110,
        ],
        'K' => [
            0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001,
        ],
        'L' => [
            0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111,
        ],
        'M' => [
            0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001,
        ],
        'N' => [
            0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001,
        ],
        'O' => [
            0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110,
        ],
        'P' => [
            0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000,
        ],
        'R' => [
            0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001,
        ],
        'S' => [
            0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110,
        ],
        'T' => [
            0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100,
        ],
        'U' => [
            0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110,
        ],
        'V' => [
            0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b01010, 0b00100,
        ],
        'W' => [
            0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001,
        ],
        'Y' => [
            0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100,
        ],
        '-' => [
            0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000,
        ],
        '_' => [
            0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111,
        ],
        ' ' => [0, 0, 0, 0, 0, 0, 0],
        '0' => [
            0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110,
        ],
        '1' => [
            0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110,
        ],
        '2' => [
            0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111,
        ],
        '3' => [
            0b01110, 0b10001, 0b00001, 0b00110, 0b00001, 0b10001, 0b01110,
        ],
        '4' => [
            0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010,
        ],
        '5' => [
            0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110,
        ],
        '6' => [
            0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110,
        ],
        '7' => [
            0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000,
        ],
        '8' => [
            0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110,
        ],
        '9' => [
            0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100,
        ],
        ':' => [0, 0b00100, 0b00100, 0, 0b00100, 0b00100, 0],
        '.' => [0, 0, 0, 0, 0, 0b00100, 0b00100],
        ',' => [0, 0, 0, 0, 0, 0b00100, 0b01000],
        '(' => [
            0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010,
        ],
        ')' => [
            0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000,
        ],
        '=' => [
            0b00000, 0b00000, 0b11111, 0b00000, 0b11111, 0b00000, 0b00000,
        ],
        _ => [0; 7],
    }
}

fn grid(
    output: &Path,
    tiles: &[(String, Vec<u8>)],
    name: &str,
    columns: usize,
    padding: usize,
    label_height: usize,
) -> Result {
    let cell_width = 320 + padding * 2;
    let cell_height = 288 + padding * 2 + label_height;
    let width = columns * cell_width;
    let height = tiles.len().div_ceil(columns) * cell_height;
    let mut canvas: Vec<u8> = [24, 24, 28]
        .into_iter()
        .cycle()
        .take(width * height * 3)
        .collect();
    for (index, (label, rgb)) in tiles.iter().enumerate() {
        let x = index % columns * cell_width + padding;
        let y = index / columns * cell_height;
        blit(&mut canvas, width, x, y + padding + label_height, rgb, 2);
        let start = x + 320usize.saturating_sub(label.len() * 12) / 2;
        let top = y as isize + 4 + (label_height as isize - 16) / 2;
        for (i, c) in label.to_ascii_uppercase().chars().enumerate() {
            for (ry, row) in glyph(c).into_iter().enumerate() {
                for rx in 0..5 {
                    if row & (1 << (4 - rx)) == 0 {
                        continue;
                    }
                    for dy in 0..2 {
                        for dx in 0..2 {
                            let px = start + i * 12 + rx * 2 + dx;
                            let py = top + (ry * 2 + dy) as isize;
                            if px < width && py >= 0 && (py as usize) < height {
                                let offset = (py as usize * width + px) * 3;
                                canvas[offset..offset + 3].fill(240);
                            }
                        }
                    }
                }
            }
        }
    }
    save(&output.join(name), &canvas, width, height)
}

fn animation(output: &Path, frames: &[Vec<u8>]) -> Result {
    let mut colors = BTreeMap::new();
    for frame in frames {
        for pixel in frame.as_chunks::<3>().0 {
            let color = [pixel[0], pixel[1], pixel[2]];
            let next = colors.len();
            colors.entry(color).or_insert(next);
        }
    }
    if colors.len() > 256 {
        return Err("Animation exceeds 256 colors".into());
    }
    let mut palette = vec![0; colors.len() * 3];
    for (color, index) in &colors {
        palette[index * 3..index * 3 + 3].copy_from_slice(color);
    }
    let file =
        BufWriter::new(File::create(output.join("shot_anim.gif")).map_err(|e| e.to_string())?);
    let mut encoder = gif::Encoder::new(file, 160, 144, &palette).map_err(|e| e.to_string())?;
    encoder
        .set_repeat(gif::Repeat::Infinite)
        .map_err(|e| e.to_string())?;
    for rgb in frames {
        let indices: Vec<u8> = rgb
            .as_chunks::<3>()
            .0
            .iter()
            .map(|p| colors[&[p[0], p[1], p[2]]] as u8)
            .collect();
        let mut frame = gif::Frame::from_indexed_pixels(160, 144, indices, None);
        frame.delay = 6;
        frame.dispose = gif::DisposalMethod::Background;
        encoder.write_frame(&frame).map_err(|e| e.to_string())?;
    }
    encoder
        .into_inner()
        .map_err(|e| e.to_string())?
        .flush()
        .map_err(|e| e.to_string())
}

pub fn render(
    output: &Path,
    sheet: &[Value],
    animations: &BTreeMap<String, Vec<String>>,
) -> Result {
    let mut tiles = Vec::new();
    for tile in sheet {
        let rgb = image(&output.join(tile["path"].as_str().ok_or("Missing capture path")?))?;
        if let Some(name) = tile["name"].as_str() {
            let mut scaled = vec![0; 480 * 432 * 3];
            blit(&mut scaled, 480, 0, 0, &rgb, 3);
            save(&output.join(format!("shot_{name}.png")), &scaled, 480, 432)?;
        }
        tiles.push((
            tile["label"]
                .as_str()
                .ok_or("Missing capture label")?
                .into(),
            rgb,
        ));
    }
    grid(output, &tiles, "shot_contact.png", 3, 12, 18)?;
    for (name, paths) in animations {
        let frames = paths
            .iter()
            .map(|p| image(&output.join(p)))
            .collect::<Result<Vec<_>>>()?;
        if frames.len() != 13 {
            return Err(format!("Expected 13 {name} frames"));
        }
        let tiles = frames
            .iter()
            .enumerate()
            .map(|(i, rgb)| (format!("F{i}"), rgb.clone()))
            .collect::<Vec<_>>();
        let filename = if name == "right" {
            "shot_anim_strip.png".into()
        } else {
            format!("shot_anim_{name}_strip.png")
        };
        grid(output, &tiles, &filename, 13, 6, 14)?;
        if name == "right" {
            animation(output, &frames)?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn writes_grid_and_exact_animation() {
        let output = std::env::temp_dir().join(format!("koh-presentation-{}", std::process::id()));
        std::fs::create_dir_all(&output).unwrap();
        let frames: Vec<Vec<u8>> = (0..13)
            .map(|i| {
                [i * 10, 30, 80]
                    .into_iter()
                    .cycle()
                    .take(160 * 144 * 3)
                    .collect()
            })
            .collect();
        let tiles = frames
            .iter()
            .enumerate()
            .map(|(i, rgb)| (format!("F{i}"), rgb.clone()))
            .collect::<Vec<_>>();
        grid(&output, &tiles, "strip.png", 13, 6, 14).unwrap();
        let mut reader = png::Decoder::new(BufReader::new(
            File::open(output.join("strip.png")).unwrap(),
        ))
        .read_info()
        .unwrap();
        assert_eq!(
            (
                reader.info().width,
                reader.info().height,
                reader.info().color_type
            ),
            (4316, 314, png::ColorType::Rgb)
        );
        let mut pixels = vec![0; reader.output_buffer_size().unwrap()];
        reader.next_frame(&mut pixels).unwrap();
        let offset = (20 * 4316 + 12 * 332 + 6) * 3;
        assert_eq!(&pixels[offset..offset + 3], &[120, 30, 80]);
        assert_eq!(&pixels[..3], &[24, 24, 28]);
        animation(&output, &frames).unwrap();
        let mut options = gif::DecodeOptions::new();
        options.set_color_output(gif::ColorOutput::RGBA);
        let mut reader = options
            .read_info(File::open(output.join("shot_anim.gif")).unwrap())
            .unwrap();
        assert_eq!((reader.width(), reader.height()), (160, 144));
        let mut count = 0;
        while let Some(frame) = reader.read_next_frame().unwrap() {
            assert_eq!(frame.delay, 6);
            assert_eq!(&frame.buffer[..4], &[count * 10, 30, 80, 255]);
            count += 1;
        }
        assert_eq!(count, 13);
        assert!(
            image(&output.join("strip.png"))
                .unwrap_err()
                .contains("160x144 RGBA")
        );
        std::fs::remove_dir_all(output).unwrap();
    }
}
