use koh_link::{DwarfDebugInfo, LinkedLine, LinkedSymbol};
use std::path::Path;

#[derive(Default)]
pub struct DebugInfo {
    pub symbols: Vec<LinkedSymbol>,
    pub lines: Vec<LinkedLine>,
}

impl DebugInfo {
    pub fn load(&mut self, bytes: &[u8]) -> Result<(), String> {
        let data = if bytes.is_empty() {
            DwarfDebugInfo::default()
        } else {
            koh_link::read_dwarf(bytes)?
        };
        for bank in data
            .symbols
            .iter()
            .filter(|s| s.kind != 1)
            .filter_map(|s| s.bank)
            .chain(data.lines.iter().map(|l| l.bank))
        {
            if bank > 255 {
                return Err(format!(
                    "Debug bank {bank} exceeds the emulator's 255-bank limit"
                ));
            }
        }
        self.symbols = data.symbols;
        self.lines = data.lines;
        Ok(())
    }
    pub fn load_path(&mut self, path: &Path) -> Result<(), String> {
        self.load(&std::fs::read(path).map_err(|e| e.to_string())?)
    }
    pub fn symbol(&self, name: &str) -> Option<&LinkedSymbol> {
        self.symbols
            .iter()
            .rev()
            .find(|s| s.name.eq_ignore_ascii_case(name))
    }
    pub fn all_symbols(&self) -> Vec<&LinkedSymbol> {
        let mut positions = std::collections::HashMap::new();
        let mut result = Vec::new();
        for symbol in &self.symbols {
            let name = symbol.name.to_ascii_lowercase();
            if let Some(index) = positions.get(&name) {
                result[*index] = symbol;
            } else {
                positions.insert(name, result.len());
                result.push(symbol);
            }
        }
        result
    }
    pub fn line(&self, bank: u8, pc: u16) -> Option<&LinkedLine> {
        self.lines.iter().rev().find(|l| {
            l.bank == bank as u32
                && pc as u32 >= l.address
                && (pc as u32) < l.address + l.count.max(1)
        })
    }
    pub fn addresses(&self, file: &str, line: u32) -> impl Iterator<Item = (u8, u16)> + '_ {
        let file = file.to_owned();
        self.lines
            .iter()
            .filter(move |l| l.file.eq_ignore_ascii_case(&file) && l.line == line)
            .map(|l| (l.bank as u8, l.address as u16))
    }
}

pub fn kind(symbol: &LinkedSymbol) -> &'static str {
    match symbol.kind {
        0 => "Label",
        1 => "Constant",
        _ => "RamLabel",
    }
}

pub fn marker(name: &str) -> bool {
    name.starts_with("__koh_unwind_")
}
