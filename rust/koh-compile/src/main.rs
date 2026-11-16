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
            "{}: input exceeds per-file/combined size limit",
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
    let mut pdb = None;
    let mut no_pdb = false;
    let mut args = std::env::args_os().skip(1);
    while let Some(arg) = args.next() {
        match arg.to_str() {
            Some(flag @ ("-o" | "--output" | "-r" | "--reference" | "--entry" | "--pdb")) => {
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
                    "--pdb" => {
                        if pdb.replace(PathBuf::from(value)).is_some() {
                            return Err("PDB specified more than once".into());
                        }
                    }
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
            Some("--no-pdb") => no_pdb = true,
            Some("--help" | "-h") => {
                println!(
                    "koh-compile ASSEMBLY [-o OBJECT] [-r REFERENCE]... [--entry Type::Method] [--pdb SYMBOLS | --no-pdb]\nEmits canonical ELF32/SM83 for koh-link. References must contain reachable implementations.\nMatching adjacent Portable PDBs are optional; --pdb selects the root assembly's symbols."
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
    if no_pdb && pdb.is_some() {
        return Err("--pdb and --no-pdb cannot be combined".into());
    }
    let output = output.unwrap_or_else(|| input.with_extension("o"));
    let output_id = identity(&output)?;
    let mut seen = HashSet::new();
    let mut images = Vec::new();
    let mut paths = Vec::new();
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
            paths.push(id);
        }
    }
    let mut symbols = Vec::new();
    let mut total = 0usize;
    for (index, path) in paths.iter().enumerate() {
        if no_pdb {
            symbols.push(None);
            continue;
        }
        let candidate = if index == 0 {
            pdb.clone().unwrap_or_else(|| path.with_extension("pdb"))
        } else {
            path.with_extension("pdb")
        };
        match std::fs::metadata(&candidate) {
            Ok(_) => {}
            Err(error)
                if error.kind() == std::io::ErrorKind::NotFound
                    && !(index == 0 && pdb.is_some()) =>
            {
                symbols.push(None);
                continue;
            }
            Err(error) => return Err(format!("{}: {error}", candidate.display())),
        }
        let symbol_id = candidate
            .canonicalize()
            .map_err(|error| format!("{}: {error}", candidate.display()))?;
        if symbol_id == output_id || seen.contains(&symbol_id) {
            return Err("PDB, assembly and output paths must be distinct".into());
        }
        let bytes = read(
            &symbol_id,
            (64 * 1024 * 1024).min(256 * 1024 * 1024 - total),
        )?;
        total += bytes.len();
        symbols.push(Some(bytes));
    }
    let views: Vec<_> = images.iter().map(Vec::as_slice).collect();
    let symbol_views: Vec<_> = symbols.iter().map(|bytes| bytes.as_deref()).collect();
    let result = (|| {
        let resolver = koh_compile::resolve::Resolver::new(&views)?;
        koh_objects::write_elf(&koh_compile::compile::object_with_debug(
            &resolver,
            entry.as_deref(),
            &symbol_views,
        )?)
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
