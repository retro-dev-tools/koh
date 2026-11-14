use std::{
    env,
    fs::{self, OpenOptions},
    io::Write,
    path::PathBuf,
    process::ExitCode,
};

fn main() -> ExitCode {
    match run(env::args().skip(1).collect()) {
        Ok(code) => ExitCode::from(code),
        Err(message) => {
            eprintln!("koh-asm: {message}");
            ExitCode::FAILURE
        }
    }
}

fn run(args: Vec<String>) -> Result<u8, String> {
    if args.as_slice() == ["--serve-json"] {
        koh_asm::service::serve(std::io::stdin().lock(), std::io::stdout().lock())
            .map_err(|e| format!("native protocol I/O: {e}"))?;
        return Ok(0);
    }
    if args.iter().any(|s| s == "--version") {
        println!("koh-asm {}", env!("CARGO_PKG_VERSION"));
        return Ok(0);
    }
    if args.is_empty() || args.iter().any(|s| s == "--help" || s == "-h") {
        let usage = "Usage: koh-asm <input.asm> [-o output]\n\nOptions:\n  -o, --output <path>    Output file path (default: input.o)\n      --version         Show version information\n  -h, --help            Show this help";
        if args.is_empty() {
            eprintln!("{usage}");
            return Ok(1);
        }
        println!("{usage}");
        return Ok(0);
    }
    let mut input = None;
    let mut output = None;
    let mut it = args.iter();
    while let Some(arg) = it.next() {
        match arg.as_str() {
            "-o" | "--output" => {
                output =
                    Some(PathBuf::from(it.next().ok_or_else(|| {
                        format!("option '{arg}' requires an argument")
                    })?))
            }
            s if s.starts_with('-') => return Err(format!("unknown option '{s}' (try --help)")),
            s => {
                if input.replace(PathBuf::from(s)).is_some() {
                    return Err(format!("unexpected argument '{s}'"));
                }
            }
        }
    }
    let input = input.ok_or("no input file specified")?;
    let output = output.unwrap_or_else(|| input.with_extension("o"));
    let absolute = input
        .canonicalize()
        .map_err(|e| format!("cannot read '{}': {e}", input.display()))?;
    if output.canonicalize().ok().as_ref() == Some(&absolute) {
        return Err("output would overwrite input source".into());
    }
    let text = fs::read_to_string(&absolute)
        .map_err(|e| format!("cannot read '{}': {e}", input.display()))?;
    let start = std::time::Instant::now();
    let report = koh_asm::assemble_report(&text, &absolute.to_string_lossy(), true);
    for warning in report.warnings {
        eprintln!(
            "{}:{}:1: warning: {}",
            warning.file, warning.line, warning.message
        );
    }
    let object = match report.result {
        Ok(model) => model,
        Err(errors) => {
            for error in &errors {
                eprintln!("{error}");
            }
            eprintln!("  {}: {} error(s)", input.display(), errors.len());
            return Ok(1);
        }
    };
    let data = koh_asm::object::write_elf(&object)?;
    let parent = output
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(std::path::Path::new("."));
    let mut temp = None;
    for n in 0..100 {
        let path = parent.join(format!(".koh-asm-{}-{n}.tmp", std::process::id()));
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => {
                temp = Some((path, file));
                break;
            }
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(format!("cannot write '{}': {e}", output.display())),
        }
    }
    let (path, mut file) = temp.ok_or("cannot create temporary output")?;
    let written = file.write_all(&data).and_then(|_| file.sync_all());
    drop(file);
    let result = written.and_then(|_| replace_output(&path, &output));
    if let Err(e) = result {
        let _ = fs::remove_file(path);
        return Err(format!("cannot write '{}': {e}", output.display()));
    }
    println!(
        "  {} -> {} [{:.0}ms]",
        input.display(),
        output.display(),
        start.elapsed().as_secs_f64() * 1000.0
    );
    Ok(0)
}

#[cfg(not(windows))]
fn replace_output(source: &std::path::Path, destination: &std::path::Path) -> std::io::Result<()> {
    fs::rename(source, destination)
}

#[cfg(windows)]
fn replace_output(source: &std::path::Path, destination: &std::path::Path) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn MoveFileExW(source: *const u16, destination: *const u16, flags: u32) -> i32;
    }
    let source: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination: Vec<u16> = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    if unsafe { MoveFileExW(source.as_ptr(), destination.as_ptr(), 9) } == 0 {
        Err(std::io::Error::last_os_error())
    } else {
        Ok(())
    }
}
