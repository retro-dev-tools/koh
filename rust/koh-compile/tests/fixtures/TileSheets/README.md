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
TileSheetConverterTests methods. Conversion now runs in koh-compile; assets.rs
also checks PNG formats, filtering, alpha and output preservation.

Naming preserves the original converter's `char.ToUpperInvariant(part[0])`
and `char.IsDigit(name[0])`, which inspect one UTF-16 code unit. Non-BMP
initials therefore remain unchanged. BMP digit classification uses
unicode-properties 0.1.5's DecimalNumber category.

The 32 sparse uppercase exceptions in assets.rs were captured on 2026-11-19
from .NET SDK 10.0.115 / Microsoft.NETCore.App 10.0.15 on Linux (host ICU 78.3),
using the original converter's invariant char API. Every BMP code unit was
recorded as its little-endian UInt16 uppercase followed by its Boolean digit
classification. Capture SHA256:
`a9db593a8801d0e30d2bedff52ed9f1aea2033eba16c83c41d011b97790061e8`.
Comparison against Rust 1.100.0's single-BMP-character uppercase fallback
found 32 uppercase differences. After applying those exceptions, exhaustive
development comparison passed for all 63,488 BMP scalar values, including
digit classification. Surrogate code units remain intact as part of non-BMP
initials. Refresh that comparison when the Unicode tables change.

`dotnet_bmp_character_semantics` builds the independent `CharOracle.cs` in a
temporary directory, honoring `KOH_DOTNET`, and compares every BMP scalar's
uppercase and digit results on every run. It reuses the existing managed-host
project settings; no asset conversion or naming algorithm remains in C#.
