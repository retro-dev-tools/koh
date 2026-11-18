use koh_link::{CgbMode, LinkOptions, link};
use std::{
    io::{Read, Write},
    path::{Path, PathBuf},
};

const MAX_INPUT: u64 = 64 * 1024 * 1024;
fn write(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let nonce = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(|e| e.to_string())?
        .as_nanos();
    let temporary = path.with_file_name(format!(".koh-link-{}-{nonce}.tmp", std::process::id()));
    let result = (|| {
        let mut file = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)
            .map_err(|e| e.to_string())?;
        file.write_all(bytes).map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
        drop(file);
        std::fs::rename(&temporary, path).map_err(|e| format!("{}: {e}", path.display()))
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(temporary);
    }
    result
}
fn read(path: &PathBuf) -> Result<Vec<u8>, String> {
    let file = std::fs::File::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let mut bytes = Vec::new();
    file.take(MAX_INPUT + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > MAX_INPUT {
        return Err("Input exceeds 64 MiB".into());
    }
    Ok(bytes)
}
fn identity(path: &std::path::Path) -> Result<PathBuf, String> {
    if path.exists() {
        return path.canonicalize().map_err(|e| e.to_string());
    }
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(std::path::Path::new("."));
    Ok(parent
        .canonicalize()
        .map_err(|e| e.to_string())?
        .join(path.file_name().ok_or("Invalid output path")?))
}
fn run() -> Result<(), String> {
    let arguments: Vec<_> = std::env::args().skip(1).collect();
    if arguments.first().map(String::as_str) == Some("--read-debug-info") {
        if arguments.len() != 1 {
            return Err("--read-debug-info only accepts ELF bytes on stdin".into());
        }
        let mut bytes = Vec::new();
        std::io::stdin()
            .take(MAX_INPUT + 1)
            .read_to_end(&mut bytes)
            .map_err(|e| e.to_string())?;
        let debug = koh_link::read_dwarf(&bytes)?;
        serde_json::to_writer(std::io::stdout().lock(), &debug).map_err(|e| e.to_string())?;
        return Ok(());
    }
    let mut options = LinkOptions::default();
    let mut output = None;
    let mut sym = None;
    let mut debug = None;
    let mut report = None;
    let mut inputs = Vec::new();
    let mut emit = false;
    let mut args = arguments.into_iter();
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "-o" | "--output" | "-n" | "--sym" | "-d" | "--kdbg" | "--report-json" => {
                let p = PathBuf::from(
                    args.next()
                        .ok_or_else(|| format!("Missing path after {arg}"))?,
                );
                match arg.as_str() {
                    "-o" | "--output" => output = Some(p),
                    "-n" | "--sym" => sym = Some(p),
                    "-d" | "--kdbg" => debug = Some(p),
                    _ => report = Some(p),
                }
            }
            "--raw" => options.raw = true,
            "--cgb-compatible" => {
                if options.cgb == CgbMode::Only {
                    return Err("Conflicting CGB flags".into());
                }
                options.cgb = CgbMode::Compatible
            }
            "--cgb-only" => {
                if options.cgb == CgbMode::Compatible {
                    return Err("Conflicting CGB flags".into());
                }
                options.cgb = CgbMode::Only
            }
            "--emit-elf" => emit = true,
            "--version" => {
                println!("koh-link {}", env!("CARGO_PKG_VERSION"));
                return Ok(());
            }
            "--help" | "-h" => {
                println!(
                    "koh-link -o ROM [-n SYM] [-d KDBG] [--raw] [--size=HEX] [--cgb-compatible|--cgb-only] OBJECT...\nkoh-link --emit-elf -o OBJECT < object.json\nkoh-link --read-debug-info < debug.elf"
                );
                return Ok(());
            }
            _ if arg.starts_with("--size=") => {
                let n = arg.trim_start_matches("--size=");
                options.size = Some(
                    usize::from_str_radix(n.trim_start_matches("0x").trim_start_matches('$'), 16)
                        .map_err(|_| "Invalid hexadecimal image size")?,
                );
            }
            "--" => {
                inputs.extend(args.map(PathBuf::from));
                break;
            }
            _ if arg.starts_with('-') => return Err(format!("Unknown option '{arg}'")),
            _ => inputs.push(PathBuf::from(arg)),
        }
    }
    let output = output
        .or_else(|| {
            if emit {
                None
            } else {
                inputs
                    .first()
                    .map(|p| p.with_extension(if options.raw { "bin" } else { "gb" }))
            }
        })
        .ok_or("Output path (-o) or input ELF is required")?;
    if emit {
        if !inputs.is_empty()
            || sym.is_some()
            || debug.is_some()
            || report.is_some()
            || options.raw
            || options.size.is_some()
            || options.cgb != CgbMode::Preserve
        {
            return Err("--emit-elf only accepts -o and JSON stdin".into());
        }
        let mut bytes = Vec::new();
        std::io::stdin()
            .take(MAX_INPUT + 1)
            .read_to_end(&mut bytes)
            .map_err(|e| e.to_string())?;
        if bytes.len() as u64 > MAX_INPUT {
            return Err("JSON exceeds 64 MiB".into());
        }
        let object: koh_objects::Object =
            serde_json::from_slice(&bytes).map_err(|e| format!("Invalid object JSON: {e}"))?;
        let elf = koh_objects::write_elf(&object)?;
        return write(&output, &elf);
    }
    if inputs.is_empty() {
        return Err("At least one ELF object is required".into());
    }
    let outputs: Vec<_> = [Some(&output), sym.as_ref(), debug.as_ref(), report.as_ref()]
        .into_iter()
        .flatten()
        .collect();
    let output_ids: Vec<_> = outputs
        .iter()
        .map(|p| identity(p))
        .collect::<Result<_, _>>()?;
    let input_ids: Vec<_> = inputs
        .iter()
        .map(|p| identity(p))
        .collect::<Result<_, _>>()?;
    for (i, p) in output_ids.iter().enumerate() {
        if output_ids[..i].contains(p) || input_ids.contains(p) {
            return Err("Output paths must be distinct from each other and inputs".into());
        }
    }
    let mut objects = Vec::new();
    let mut total_input = 0usize;
    for p in &inputs {
        let bytes = read(p)?;
        total_input = total_input
            .checked_add(bytes.len())
            .ok_or("Input size overflow")?;
        if total_input as u64 > MAX_INPUT {
            return Err("Combined input exceeds 64 MiB".into());
        }
        objects.push(koh_objects::read_elf(&bytes).map_err(|e| format!("{}: {e}", p.display()))?);
    }
    let result = link(&objects, &options)?;
    let kdbg = if debug.is_some() {
        Some(result.kdbg()?)
    } else {
        None
    };
    if let Some(path) = report {
        let symbols: Vec<_> = result
            .symbols
            .iter()
            .map(|s| serde_json::json!({"name":s.name,"value":s.value,"bank":s.bank,"kind":s.kind}))
            .collect();
        let lines:Vec<_>=result.lines.iter().map(|l|serde_json::json!({"bank":l.bank,"address":l.address,"count":l.count,"file":l.file,"line":l.line})).collect();
        write(
            &path,
            &serde_json::to_vec(&serde_json::json!({"symbols":symbols,"lines":lines}))
                .map_err(|e| e.to_string())?,
        )?;
    }
    if let Some(path) = sym {
        write(&path, result.sym().as_bytes())?;
    }
    if let Some(path) = debug {
        write(&path, &kdbg.unwrap())?;
    }
    write(&output, &result.rom)?;
    Ok(())
}
fn main() {
    if let Err(error) = run() {
        eprintln!("koh-link: {error}");
        std::process::exit(1);
    }
}
