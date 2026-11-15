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
#[case("double-buffered", "Release", true)]
#[case("double-buffered", "Debug", true)]
#[case("double-buffered", "Release", false)]
#[case("double-buffered", "Debug", false)]
#[case("full-frame", "Release", true)]
#[case("full-frame", "Debug", true)]
#[case("full-frame", "Release", false)]
#[case("full-frame", "Debug", false)]
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
        _ => unreachable!(),
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
    assert_eq!(!starts.is_empty(), color, "LCD-on GDMA path");
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
}

fn gdma_fits_vblank(line: u32, dot: u32, control: u8) -> bool {
    control & 0x80 == 0
        && (144..154).contains(&line)
        && dot < 456
        && (u32::from(control) + 1) * 32 + 8 < (154 - line) * 456 - dot
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
    #[values((32, 32, 96, 80), (16, 8, 128, 120))] viewport: (usize, usize, usize, usize),
) {
    let mut frame = vec![255; 160 * 144 * 4];
    for y in 48..96 {
        for x in 40..120 {
            let shade = if x < 80 { 181 } else { 99 };
            frame[(y * 160 + x) * 4..(y * 160 + x) * 4 + 3].fill(shade);
        }
    }
    match damage {
        1 => frame.fill(255),
        2 => frame[(60 * 160 + 60) * 4 + 3] = 0,
        3 => frame[(60 * 160 + 60) * 4] = 77,
        4 => frame[(10 * 160 + 10) * 4..(10 * 160 + 10) * 4 + 3].fill(33),
        5 => frame[(40 * 160 + 80) * 4..(40 * 160 + 80) * 4 + 3].fill(33),
        _ => {}
    }
    assert_eq!(check_cube(&frame, true, viewport).is_ok(), valid);
}
