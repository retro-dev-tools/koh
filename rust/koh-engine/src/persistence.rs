use koh_emulator::{
    machine::Machine,
    state::{StateReader, StateWriter},
};
use std::{
    fs,
    io::{self, Read},
    path::Path,
};

pub fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let mut name = path.as_os_str().to_os_string();
    name.push(".tmp");
    let temporary = std::path::PathBuf::from(name);
    let result = fs::write(&temporary, bytes).and_then(|()| fs::rename(&temporary, path));
    if result.is_err() {
        let _ = fs::remove_file(temporary);
    }
    result
}
pub fn save_sram(machine: &Machine, path: &str) -> io::Result<()> {
    if machine.cartridge.ram.is_empty() {
        return Ok(());
    }
    let save = format!("{path}.sav");
    atomic_write(Path::new(&save), &machine.cartridge.ram)
        .map_err(|e| io::Error::other(format!("SRAM save failed ({save}): {e}")))
}
pub fn load_sram(machine: &mut Machine, path: &str) -> io::Result<()> {
    if machine.cartridge.ram.is_empty() {
        return Ok(());
    }
    let save = format!("{path}.sav");
    let input = match fs::File::open(&save) {
        Ok(input) => input,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(io::Error::other(format!("SRAM load failed ({save}): {e}"))),
    };
    let mut bytes = Vec::new();
    input
        .take(machine.cartridge.ram.len() as u64)
        .read_to_end(&mut bytes)
        .map_err(|e| io::Error::other(format!("SRAM load failed ({save}): {e}")))?;
    let count = bytes.len().min(machine.cartridge.ram.len());
    machine.cartridge.ram[..count].copy_from_slice(&bytes[..count]);
    Ok(())
}
pub fn save_state(machine: &Machine, path: &str) -> io::Result<()> {
    let mut writer = StateWriter::default();
    machine.write_state(&mut writer);
    atomic_write(Path::new(path), &writer.bytes)
        .map_err(|e| io::Error::other(format!("Save state failed: {e}")))
}
pub fn restore_state(machine: &mut Machine, path: &str) -> io::Result<()> {
    let mut bytes = Vec::new();
    fs::File::open(path)?
        .take(8 * 1024 * 1024)
        .read_to_end(&mut bytes)?;
    let mut rollback = StateWriter::default();
    machine.write_state(&mut rollback);
    if let Err(error) = machine.read_state(&mut StateReader::new(&bytes)) {
        machine
            .read_state(&mut StateReader::new(&rollback.bytes))
            .map_err(|e| io::Error::other(format!("state rollback failed: {e}")))?;
        return Err(error);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn failed_rename_preserves_destination_and_cleans_temporary_file() {
        let directory =
            std::env::temp_dir().join(format!("koh-save-rename-{}", std::process::id()));
        fs::create_dir(&directory).unwrap();
        for name in ["game.state", "game.sav"] {
            let path = directory.join(name);
            fs::create_dir(&path).unwrap();
            fs::write(path.join("previous"), b"keep").unwrap();
            assert!(atomic_write(&path, b"replacement").is_err());
            assert_eq!(fs::read(path.join("previous")).unwrap(), b"keep");
            assert!(!directory.join(format!("{name}.tmp")).exists());
        }
        fs::remove_dir_all(directory).unwrap();
    }
}
