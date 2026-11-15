# Tile-sheet fixtures

Both files are synthetic8×8 RGBA8 PNGs, noninterlaced, with filter0 rows and
standard zlib, Adler32 and PNG chunk CRC32 checksums.

`four-colors.png` repeats rows of dark gray(32,32,32), white(255,255,255),
green(96,160,96), red(160,64,64), all opaque. Luminance sorting gives indices
3,0,1,2 and row planes FF/FF,00/00,FF/00,00/FF. The weighted sum of the16 plane
bytes, using weights1..16, is16320. RGB555 bookends are7FFF and1084.

`five-colors.png` uses row-major pixel index modulo5 multiplied by40 as the red
channel, with zero green/blue and alpha255. It must produce a five-color
authoring diagnostic, not silently quantize or omit the image.

Rust SDK tests drive conversion, source generation, incremental inclusion and
actual compiled-ROM consumption. These replace the two legacy compiler-owned
TileSheetConverterTests methods without moving the production converter to Rust.
