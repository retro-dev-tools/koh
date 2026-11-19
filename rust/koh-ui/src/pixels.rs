pub fn nearest_bgra(rgba: &[u8], width: u32, height: u32, scale: u32) -> Option<image::RgbaImage> {
    if scale == 0
        || width == 0
        || height == 0
        || rgba.len() != usize::try_from(width.checked_mul(height)?.checked_mul(4)?).ok()?
    {
        return None;
    }
    let mut bgra = rgba.to_vec();
    for pixel in bgra.as_chunks_mut::<4>().0 {
        pixel.swap(0, 2);
    }
    let image = image::RgbaImage::from_raw(width, height, bgra)?;
    Some(image::imageops::resize(
        &image,
        width.checked_mul(scale)?,
        height.checked_mul(scale)?,
        image::imageops::FilterType::Nearest,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preserves_channels_and_duplicates_each_pixel_at_integer_scale() {
        let scaled = nearest_bgra(&[255, 0, 0, 255, 0, 0, 255, 255], 2, 1, 2).unwrap();
        assert_eq!(scaled.dimensions(), (4, 2));
        for y in 0..2 {
            assert_eq!(scaled.get_pixel(0, y).0, [0, 0, 255, 255]);
            assert_eq!(scaled.get_pixel(1, y).0, [0, 0, 255, 255]);
            assert_eq!(scaled.get_pixel(2, y).0, [255, 0, 0, 255]);
            assert_eq!(scaled.get_pixel(3, y).0, [255, 0, 0, 255]);
        }
        assert!(nearest_bgra(&[0], 1, 1, 2).is_none());
        assert!(nearest_bgra(&[0; 5], 1, 1, 1).is_none());
        assert!(nearest_bgra(&[0; 4], 1, 1, 0).is_none());
    }
}
