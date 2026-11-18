use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
use std::path::Path;
mod support;
use support::{Host, dotnet};

static ROSLYN: std::sync::Mutex<()> = std::sync::Mutex::new(());

#[rstest::fixture]
#[once]
fn cube_host() -> Host {
    support::build_fixture_host("CubeHost")
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn shared_renderer_sweeps_every_phase(#[case] configuration: &str) {
    let directory = tempfile::tempdir().unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/CubePhases");
    for name in ["CubePhases.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(name), directory.path().join(name)).unwrap();
    }
    let guard = ROSLYN.lock().unwrap();
    let output = dotnet()
        .arg("build")
        .arg(directory.path().join("CubePhases.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    drop(guard);
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let output = dotnet()
        .arg(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/CubePhases.dll")),
        )
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let mut bytes = output.stdout.as_slice();
    let mut scenarios = 0;
    for (width, height) in [(64, 64), (96, 80), (128, 120)] {
        assert_eq!(bytes.get(..2), Some([width as u8, height as u8].as_slice()));
        bytes = &bytes[2..];
        for phase in 0..256 {
            assert_eq!(bytes.first().copied(), Some(phase as u8));
            let frame = bytes
                .get(1..1 + width * height)
                .expect("truncated phase frame");
            check_phase(frame, width, height)
                .unwrap_or_else(|e| panic!("{configuration} {width}x{height} phase{phase}: {e}"));
            bytes = &bytes[1 + width * height..];
            scenarios += 1;
        }
    }
    assert!(bytes.is_empty(), "unexpected phase records");
    assert_eq!(scenarios, 768);
}

fn check_phase(frame: &[u8], width: usize, height: usize) -> Result<(), String> {
    if width < 5 || height < 5 || width.checked_mul(height) != Some(frame.len()) {
        return Err("invalid phase dimensions".into());
    }
    let (mut min_x, mut min_y, mut max_x, mut max_y) = (width, height, 0, 0);
    let mut lit = false;
    for (index, &value) in frame.iter().enumerate() {
        if value > 3 {
            return Err("invalid phase palette".into());
        }
        if value == 0 {
            continue;
        }
        lit = true;
        let (x, y) = (index % width, index / width);
        min_x = min_x.min(x);
        min_y = min_y.min(y);
        max_x = max_x.max(x);
        max_y = max_y.max(y);
    }
    if !lit {
        return Err("empty phase".into());
    }
    if min_x < 2 || min_y < 2 || max_x > width - 3 || max_y > height - 3 {
        return Err("phase touches viewport edge".into());
    }
    let area = (max_x - min_x + 1) * (max_y - min_y + 1);
    if area * 100 < frame.len() * 10 || area * 100 > frame.len() * 75 {
        return Err("phase bounding area outside 10..75 percent".into());
    }
    Ok(())
}

#[rstest]
#[case(0, None)]
#[case(1, Some("empty"))]
#[case(2, Some("dimensions"))]
#[case(3, Some("palette"))]
#[case(4, Some("edge"))]
#[case(5, Some("area"))]
#[case(6, Some("area"))]
#[case(7, Some("dimensions"))]
fn phase_checks_reject_invalid_frames(
    #[case] damage: u8,
    #[case] error: Option<&str>,
    #[values((64, 64), (96, 80), (128, 120))] geometry: (usize, usize),
) {
    let (width, height) = geometry;
    let mut frame = vec![0; width * height];
    let (cx, cy) = (width / 2, height / 2);
    for y in cy - 16..cy + 16 {
        frame[y * width + cx - 24..y * width + cx + 24].fill(2);
    }
    match damage {
        1 => frame.fill(0),
        2 => {
            frame.pop();
        }
        3 => frame[cy * width + cx] = 4,
        4 => frame[width + cx] = 2,
        5 => {
            frame.fill(0);
            frame[cy * width + cx] = 2;
        }
        6 => {
            for y in 2..height - 2 {
                frame[y * width + 2..y * width + width - 2].fill(2);
            }
        }
        7 => frame.push(0),
        _ => {}
    }
    let result = check_phase(&frame, width, height);
    match error {
        None => result.unwrap(),
        Some(expected) => assert!(result.unwrap_err().contains(expected)),
    }
}

#[rstest]
#[case::double_release_cgb("double-buffered", "Release", true)]
#[case::double_debug_cgb("double-buffered", "Debug", true)]
#[case::double_release_dmg("double-buffered", "Release", false)]
#[case::double_debug_dmg("double-buffered", "Debug", false)]
#[case::full_release_cgb("full-frame", "Release", true)]
#[case::full_debug_cgb("full-frame", "Debug", true)]
#[case::full_release_dmg("full-frame", "Release", false)]
#[case::full_debug_dmg("full-frame", "Debug", false)]
#[case::racing_release_cgb("racing-beam", "Release", true)]
#[case::racing_debug_cgb("racing-beam", "Debug", true)]
#[case::racing_release_dmg("racing-beam", "Release", false)]
#[case::racing_debug_dmg("racing-beam", "Debug", false)]
fn cube_boots_and_animates(
    cube_host: &Host,
    #[case] variant: &str,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::tempdir_in(cube_host.directory.path()).unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let project = directory.path().join("Cube.csproj");
    std::fs::copy(
        Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Cube/Cube.csproj"),
        &project,
    )
    .unwrap();
    let build_guard = ROSLYN.lock().unwrap();
    let output = dotnet()
        .arg("build")
        .arg(project)
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .arg(format!("-p:KohCubeVariant={variant}"))
        .output()
        .unwrap();
    drop(build_guard);
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let images =
        ["Cube.dll", "Koh.GameBoy.dll"].map(|name| std::fs::read(output.join(name)).unwrap());
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(&resolver, None).unwrap();
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
    let symbol = |name: &str| {
        linked
            .symbols
            .iter()
            .find(|s| s.name == name)
            .unwrap()
            .value
    };
    let (first, last, viewport) = match (variant, color) {
        ("double-buffered", true) => (200, 500, (32, 32, 96, 80)),
        ("double-buffered", false) => (1100, 2000, (32, 32, 96, 80)),
        ("full-frame", true) => (300, 500, (16, 8, 128, 120)),
        ("full-frame", false) => (450, 750, (16, 8, 128, 120)),
        ("racing-beam", true) => (300, 450, (43, 40, 70, 64)),
        ("racing-beam", false) => (400, 700, (43, 40, 70, 64)),
        _ => unreachable!(),
    };
    let first = if variant == "full-frame" && !color && configuration == "Release" {
        250
    } else {
        first
    };
    let rom = directory.path().join("cube.gb");
    std::fs::write(&rom, &linked.rom).unwrap();
    let output = dotnet()
        .arg(&cube_host.dll)
        .arg(&rom)
        .args([
            first.to_string(),
            last.to_string(),
            format!("{:x}", symbol("__koh_abort")),
            format!("{:x}", symbol("__koh_heap_cursor")),
            format!("{:x}", symbol("__koh_raw_used")),
        ])
        .arg(variant)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let report = String::from_utf8(output.stdout).unwrap();
    if let Ok(profile) = std::fs::read(rom.with_extension("gb.profile")) {
        assert_eq!(profile.len() % 12, 0);
        let mut methods = std::collections::BTreeMap::new();
        for s in &linked.symbols {
            let Some((assembly, token)) = s
                .name
                .strip_prefix("__koh_a")
                .and_then(|s| s.split_once("_m"))
            else {
                continue;
            };
            if token.contains('.') {
                continue;
            }
            let name = resolver
                .assembly(assembly.parse().unwrap())
                .unwrap()
                .method_name(u32::from_str_radix(token, 16).unwrap())
                .unwrap_or_else(|_| s.name.clone());
            methods.insert((s.bank.unwrap_or(0), s.value), name);
        }
        let mut costs = std::collections::BTreeMap::<String, u64>::new();
        for record in profile.chunks_exact(12) {
            let key = u32::from_le_bytes(record[..4].try_into().unwrap());
            let cycles = u64::from_le_bytes(record[4..].try_into().unwrap());
            let (bank, pc) = (key >> 16, (key & 0xffff) as i32);
            let name = methods
                .range((bank, 0)..=(bank, pc))
                .next_back()
                .map(|(_, name)| name.as_str())
                .unwrap_or("<startup>");
            *costs.entry(name.into()).or_default() += cycles;
        }
        let mut costs: Vec<_> = costs.into_iter().collect();
        costs.sort_by_key(|(_, cycles)| std::cmp::Reverse(*cycles));
        for (name, cycles) in costs.into_iter().take(15) {
            eprintln!("profile {cycles}T {name}");
        }
    }
    let fields: Vec<_> = report.split_whitespace().collect();
    assert_eq!(fields.len(), 12, "{report}");
    assert_eq!(
        fields[1].parse::<u32>().unwrap(),
        last,
        "game stopped before capture: {report}"
    );
    assert!(fields[0].parse::<u32>().unwrap() < 600, "{report}");
    assert_eq!(fields[8], "0", "VRAM mode3 writes: {report}");
    let starts = std::fs::read(rom.with_extension("gb.gdma")).unwrap();
    assert_eq!(starts.len() % 4, 0);
    assert_eq!(
        !starts.is_empty(),
        color && variant != "racing-beam",
        "LCD-on GDMA path"
    );
    for start in starts.chunks_exact(4) {
        let line = u32::from(start[0]);
        let dot = u32::from(u16::from_le_bytes([start[1], start[2]]));
        let control = start[3];
        let cost = (u32::from(control) + 1) * 32 + 8;
        assert!(
            gdma_fits_vblank(line, dot, control),
            "GDMA overruns VBlank: LY={line} dot={dot} cost={cost}; {report}"
        );
    }
    let first = std::fs::read(rom.with_extension(format!("gb.{first}.rgba"))).unwrap();
    let last = std::fs::read(rom.with_extension(format!("gb.{last}.rgba"))).unwrap();
    if first == last || check_cube(&first, color, viewport).is_err() {
        let pc = i32::from_str_radix(fields[10], 16).unwrap();
        let bank = fields[11].parse::<u32>().unwrap();
        let owner = linked
            .symbols
            .iter()
            .filter(|s| {
                s.bank == Some(bank)
                    && s.value <= pc
                    && s.name.starts_with("__koh_a")
                    && !s.name.contains('.')
            })
            .max_by_key(|s| s.value)
            .unwrap();
        let (assembly, token) = owner
            .name
            .strip_prefix("__koh_a")
            .unwrap()
            .split_once("_m")
            .unwrap();
        let name = resolver
            .assembly(assembly.parse().unwrap())
            .unwrap()
            .method_name(u32::from_str_radix(token, 16).unwrap())
            .unwrap();
        eprintln!("active {name}: {}+{}", owner.name, pc - owner.value);
    }
    check_cube(&first, color, viewport).unwrap_or_else(|e| panic!("first capture: {e}; {report}"));
    check_cube(&last, color, viewport).unwrap_or_else(|e| panic!("last capture: {e}; {report}"));
    assert!(
        first != last,
        "cube must animate: {report}; {}",
        String::from_utf8_lossy(&output.stderr)
    );
    if variant == "racing-beam" {
        let lines = std::fs::read(rom.with_extension("gb.scroll-lines")).unwrap();
        let writes = std::fs::read(rom.with_extension("gb.scroll-writes")).unwrap();
        check_raster(&lines, &writes).unwrap();
    }
}

fn check_raster(lines: &[u8], writes: &[u8]) -> Result<(), String> {
    if lines.len() % 10 != 0 || writes.len() % 13 != 0 {
        return Err("malformed scanline/write trace".into());
    }
    for write in writes.chunks_exact(13) {
        let (line, mode, value) = (write[8], write[11], write[12]);
        if mode > 1 || (value != 0 && (mode != 0 || !(39..=102).contains(&line))) {
            return Err(format!(
                "SCX write outside raster window: LY={line} dot={} mode={mode} value={value}",
                u16::from_le_bytes([write[9], write[10]])
            ));
        }
    }
    let wave: Vec<u8> = (0..64)
        .map(|row| {
            let phase = row * 4;
            let ramp = (phase & 63) * 2;
            let sin = match phase >> 6 {
                0 => ramp,
                1 => 126 - ramp,
                2 => -ramp,
                _ => ramp - 126,
            };
            (sin / 32 + 2) as u8
        })
        .collect();
    let lines: Vec<_> = lines
        .chunks_exact(10)
        .map(|r| (u64::from_le_bytes(r[..8].try_into().unwrap()), r[8], r[9]))
        .collect();
    let mut matched = 0;
    for frame in lines.windows(144).filter(|frame| frame[0].1 == 0) {
        if !frame.iter().enumerate().all(|(row, r)| r.1 as usize == row)
            || !frame
                .windows(2)
                .all(|r| r[1].0 > r[0].0 && r[1].0 - r[0].0 < 912)
        {
            continue;
        }
        if frame[..40].iter().all(|r| r.2 == 0)
            && frame[40..104].iter().map(|r| r.2).eq(wave.iter().copied())
        {
            matched += 1;
        }
    }
    if matched < 2 {
        return Err(format!(
            "only {matched} complete raster frames; require sequential SCX wave on rows40..103"
        ));
    }
    Ok(())
}

fn gdma_fits_vblank(line: u32, dot: u32, control: u8) -> bool {
    control & 0x80 == 0
        && (144..154).contains(&line)
        && dot < 456
        && (u32::from(control) + 1) * 32 + 8 < (154 - line) * 456 - dot
}

#[rstest]
#[case::complete(0, true)]
#[case::drawing_write(1, false)]
#[case::wrong_line_write(2, false)]
#[case::wrong_wave(3, false)]
#[case::missing_row(4, false)]
#[case::spliced_frames(5, false)]
#[case::one_frame(6, false)]
#[case::truncated_lines(7, false)]
#[case::truncated_writes(8, false)]
fn raster_trace_checks_physical_rows(#[case] damage: u8, #[case] valid: bool) {
    let mut lines = Vec::new();
    for frame in 0..2u64 {
        for row in 0..144u8 {
            if damage == 4 && row == 70 {
                continue;
            }
            let value = if (40..104).contains(&row) {
                let position = i32::from(row - 40);
                let triangle = match position {
                    0..16 => position * 8,
                    16..32 => 254 - position * 8,
                    32..48 => 256 - position * 8,
                    _ => position * 8 - 510,
                };
                (triangle / 32 + 2) as u8
            } else {
                0
            };
            let gap = if damage == 5 && row >= 70 { 70224 } else { 0 };
            lines.extend_from_slice(&(frame * 70224 + u64::from(row) * 456 + gap).to_le_bytes());
            lines.extend_from_slice(&[row, if damage == 3 && row == 70 { 99 } else { value }]);
        }
    }
    let mut writes = Vec::new();
    writes.extend_from_slice(&0u64.to_le_bytes());
    writes.push(if damage == 2 { 38 } else { 39 });
    writes.extend_from_slice(&300u16.to_le_bytes());
    writes.extend_from_slice(&[if damage == 1 { 3 } else { 0 }, 2]);
    match damage {
        6 => lines.truncate(144 * 10),
        7 => {
            lines.pop();
        }
        8 => {
            writes.pop();
        }
        _ => {}
    }
    assert_eq!(check_raster(&lines, &writes).is_ok(), valid);
}

#[rstest]
#[case(144, 0, 119, true)]
#[case(144, 455, 127, true)]
#[case(145, 0, 127, false)]
#[case(145, 255, 119, true)]
#[case(145, 256, 119, false)]
#[case(153, 0, 0, true)]
#[case(153, 416, 0, false)]
#[case(143, 0, 0, false)]
#[case(154, 0, 0, false)]
#[case(144, 456, 0, false)]
#[case(144, 0, 128, false)]
fn gdma_window_checks_cost_and_mode(
    #[case] line: u32,
    #[case] dot: u32,
    #[case] control: u8,
    #[case] valid: bool,
) {
    assert_eq!(gdma_fits_vblank(line, dot, control), valid);
}

fn check_cube(
    frame: &[u8],
    color: bool,
    viewport: (usize, usize, usize, usize),
) -> Result<(), String> {
    let (left, top, width, height) = viewport;
    if frame.len() != 160 * 144 * 4 {
        return Err("invalid RGBA dimensions".into());
    }
    let shades = if color {
        [255, 181, 99, 33]
    } else {
        [255, 170, 85, 0]
    };
    let mut lit = vec![false; 160 * 144];
    let mut used = std::collections::BTreeSet::new();
    let (mut min_x, mut min_y, mut max_x, mut max_y) = (160, 144, 0, 0);
    for (index, pixel) in frame.chunks_exact(4).enumerate() {
        if pixel[3] != 255
            || pixel[0] != pixel[1]
            || pixel[1] != pixel[2]
            || !shades.contains(&pixel[0])
        {
            return Err(format!(
                "unexpected palette/alpha at pixel {index}: {pixel:?}"
            ));
        }
        if pixel[0] == 255 {
            continue;
        }
        let (x, y) = (index % 160, index / 160);
        if !(left..left + width).contains(&x) || !(top..top + height).contains(&y) {
            return Err(format!("pixel outside {viewport:?} viewport: {x},{y}"));
        }
        lit[index] = true;
        used.insert(pixel[0]);
        min_x = min_x.min(x);
        max_x = max_x.max(x);
        min_y = min_y.min(y);
        max_y = max_y.max(y);
    }
    let area = lit.iter().filter(|&&v| v).count();
    if used.len() < 2 || !(200..6000).contains(&area) {
        return Err(format!(
            "cube needs shaded faces: {} shades, {area} pixels",
            used.len()
        ));
    }
    if max_x - min_x < 24
        || max_y - min_y < 24
        || !(min_x..=max_x).contains(&(left + width / 2))
        || !(min_y..=max_y).contains(&(top + height / 2))
    {
        return Err(format!(
            "implausible centered projection: {min_x},{min_y}..{max_x},{max_y}"
        ));
    }
    let start = lit.iter().position(|&v| v).unwrap();
    let mut pending = vec![start];
    lit[start] = false;
    while let Some(index) = pending.pop() {
        let (x, y) = (index % 160, index / 160);
        for dy in -1isize..=1 {
            for dx in -1isize..=1 {
                let (nx, ny) = (x as isize + dx, y as isize + dy);
                if !(0..160).contains(&nx) || !(0..144).contains(&ny) {
                    continue;
                }
                let next = ny as usize * 160 + nx as usize;
                if lit[next] {
                    lit[next] = false;
                    pending.push(next);
                }
            }
        }
    }
    if lit.contains(&true) {
        return Err("disconnected cube silhouette".into());
    }
    Ok(())
}

#[rstest]
#[case(0, true)]
#[case(1, false)]
#[case(2, false)]
#[case(3, false)]
#[case(4, false)]
#[case(5, false)]
fn cube_capture_checks_reject_invalid_images(
    #[case] damage: u8,
    #[case] valid: bool,
    #[values((32, 32, 96, 80), (16, 8, 128, 120), (43, 40, 70, 64))] viewport: (
        usize,
        usize,
        usize,
        usize,
    ),
) {
    let mut frame = vec![255; 160 * 144 * 4];
    let (cx, cy) = (viewport.0 + viewport.2 / 2, viewport.1 + viewport.3 / 2);
    for y in cy - 16..cy + 16 {
        for x in cx - 24..cx + 24 {
            let shade = if x < cx { 181 } else { 99 };
            frame[(y * 160 + x) * 4..(y * 160 + x) * 4 + 3].fill(shade);
        }
    }
    match damage {
        1 => frame.fill(255),
        2 => frame[(cy * 160 + cx) * 4 + 3] = 0,
        3 => frame[(cy * 160 + cx) * 4] = 77,
        4 => frame[(10 * 160 + 10) * 4..(10 * 160 + 10) * 4 + 3].fill(33),
        5 => {
            frame[((viewport.1 + 3) * 160 + cx) * 4..((viewport.1 + 3) * 160 + cx) * 4 + 3].fill(33)
        }
        _ => {}
    }
    assert_eq!(check_cube(&frame, true, viewport).is_ok(), valid);
}
