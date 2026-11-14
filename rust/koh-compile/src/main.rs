use std::{
    collections::HashSet,
    io::{Read, Write},
    path::{Path, PathBuf},
    process::ExitCode,
};

fn identity(path: &Path) -> Result<PathBuf, String> {
    if path.exists() {
        return path
            .canonicalize()
            .map_err(|e| format!("{}: {e}", path.display()));
    }
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    Ok(parent
        .canonicalize()
        .map_err(|e| format!("{}: {e}", parent.display()))?
        .join(path.file_name().ok_or("Invalid output path")?))
}

fn read(path: &Path, limit: usize) -> Result<Vec<u8>, String> {
    let file = std::fs::File::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let mut bytes = Vec::new();
    file.take(limit as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| format!("{}: {e}", path.display()))?;
    if bytes.len() > limit {
        return Err(format!(
            "{}: input exceeds assembly/combined size limit",
            path.display()
        ));
    }
    Ok(bytes)
}

fn write(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let nonce = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(|e| e.to_string())?
        .as_nanos();
    let temporary = path.with_file_name(format!(".koh-compile-{}-{nonce}.tmp", std::process::id()));
    let mut file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|e| e.to_string())?;
    let result = (|| {
        file.write_all(bytes).map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
        drop(file);
        std::fs::rename(&temporary, path).map_err(|e| format!("{}: {e}", path.display()))
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
    result
}

fn run() -> Result<(), String> {
    let mut input = None;
    let mut output = None;
    let mut references = Vec::new();
    let mut entry = None;
    let mut args = std::env::args_os().skip(1);
    while let Some(arg) = args.next() {
        match arg.to_str() {
            Some(flag @ ("-o" | "--output" | "-r" | "--reference" | "--entry")) => {
                let value = args
                    .next()
                    .ok_or_else(|| format!("Missing value after {flag}"))?;
                match flag {
                    "-o" | "--output" => {
                        if output.replace(PathBuf::from(value)).is_some() {
                            return Err("Output specified more than once".into());
                        }
                    }
                    "-r" | "--reference" => references.push(PathBuf::from(value)),
                    _ => {
                        let name = value
                            .into_string()
                            .map_err(|_| "Entry name must be UTF-8")?;
                        if entry.replace(name).is_some() {
                            return Err("Entry specified more than once".into());
                        }
                    }
                }
            }
            Some("--help" | "-h") => {
                println!(
                    "koh-compile ASSEMBLY [-o OBJECT] [-r REFERENCE]... [--entry Type::Method]\nEmits canonical ELF32/SM83 for koh-link. References must contain reachable implementations."
                );
                return Ok(());
            }
            Some("--version") => {
                println!("koh-compile {}", env!("CARGO_PKG_VERSION"));
                return Ok(());
            }
            Some("--") => {
                for path in args.by_ref() {
                    if input.replace(PathBuf::from(path)).is_some() {
                        return Err("Exactly one input assembly is required".into());
                    }
                }
                break;
            }
            Some(flag) if flag.starts_with('-') => return Err(format!("Unknown option '{flag}'")),
            _ => {
                if input.replace(PathBuf::from(arg)).is_some() {
                    return Err("Exactly one input assembly is required".into());
                }
            }
        }
    }
    let input = input.ok_or("Assembly path is required")?;
    let output = output.unwrap_or_else(|| input.with_extension("o"));
    let output_id = identity(&output)?;
    let mut seen = HashSet::new();
    let mut images = Vec::new();
    let mut total = 0usize;
    for path in std::iter::once(&input).chain(&references) {
        let id = path
            .canonicalize()
            .map_err(|e| format!("{}: {e}", path.display()))?;
        if id == output_id {
            return Err("Output path must be distinct from input/reference assemblies".into());
        }
        if seen.insert(id.clone()) {
            if images.len() >= 1024 {
                return Err("Assembly count exceeds 1024".into());
            }
            let bytes = read(&id, (64 * 1024 * 1024).min(256 * 1024 * 1024 - total))?;
            total += bytes.len();
            images.push(bytes);
        }
    }
    let views: Vec<_> = images.iter().map(Vec::as_slice).collect();
    let result = (|| {
        let resolver = koh_compile::resolve::Resolver::new(&views)?;
        koh_objects::write_elf(&koh_compile::compile::object(&resolver, entry.as_deref())?)
    })()
    .map_err(|e| format!("{}: {e}", input.display()))?;
    write(&output, &result)
}

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("koh-compile: {error}");
            ExitCode::FAILURE
        }
    }
}
