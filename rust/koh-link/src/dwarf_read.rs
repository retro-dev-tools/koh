use crate::{LinkedLine, LinkedSymbol};
use gimli::{AttributeValue, EndianSlice, LittleEndian};
use koh_objects::Provenance;
use object::read::elf::FileHeader as _;
use object::{Endianness, Object as _, ObjectSection as _, elf};

type Input<'a> = EndianSlice<'a, LittleEndian>;

#[derive(Debug, Default, serde::Serialize)]
pub struct DwarfDebugInfo {
    pub symbols: Vec<LinkedSymbol>,
    pub lines: Vec<LinkedLine>,
}

#[derive(Default)]
struct Budget(usize);
impl Budget {
    fn charge(&mut self, bytes: usize) -> Result<(), String> {
        self.0 = self
            .0
            .checked_add(bytes)
            .filter(|n| *n <= 64 * 1024 * 1024)
            .ok_or("DWARF decoded data exceeds 64 MiB budget")?;
        Ok(())
    }
    fn text(&mut self, value: Input<'_>) -> Result<String, String> {
        self.charge(value.slice().len())?;
        let value = std::str::from_utf8(value.slice()).map_err(|_| "Invalid UTF-8 in DWARF")?;
        if value.is_empty() {
            return Err("Empty DWARF name".into());
        }
        Ok(value.to_owned())
    }
}

fn error(error: impl std::fmt::Display) -> String {
    format!("Invalid DWARF: {error}")
}

fn path(
    dwarf: &gimli::Dwarf<Input<'_>>,
    unit: &gimli::Unit<Input<'_>>,
    index: u64,
    budget: &mut Budget,
) -> Result<String, String> {
    let header = unit
        .line_program
        .as_ref()
        .ok_or("Missing DWARF line program")?
        .header();
    let file = header.file(index).ok_or("Invalid DWARF file index")?;
    let name = budget.text(dwarf.attr_string(unit, file.path_name()).map_err(error)?)?;
    if absolute(&name) {
        return Ok(name);
    }
    let directory = file
        .directory(header)
        .ok_or("Invalid DWARF directory index")?;
    let directory = budget.text(dwarf.attr_string(unit, directory).map_err(error)?)?;
    let directory = if absolute(&directory) {
        directory
    } else {
        let base = unit
            .comp_dir
            .as_ref()
            .map(|dir| budget.text(*dir))
            .transpose()?
            .unwrap_or_else(|| ".".into());
        if directory == "." {
            base
        } else if base == "." {
            directory
        } else {
            budget.charge(base.len() + directory.len() + 1)?;
            format!("{}/{directory}", base.trim_end_matches(['/', '\\']))
        }
    };
    if directory == "." {
        return Ok(name);
    }
    budget.charge(directory.len() + name.len() + 1)?;
    Ok(format!(
        "{}/{name}",
        directory.trim_end_matches(['/', '\\'])
    ))
}

fn absolute(path: &str) -> bool {
    path.starts_with(['/', '\\']) || path.as_bytes().get(1) == Some(&b':')
}

fn split(address: u64) -> Result<(u32, u32), String> {
    let address = u32::try_from(address).map_err(|_| "DWARF address exceeds 32 bits")?;
    Ok((address >> 16, address & 0xffff))
}

pub fn read_dwarf(bytes: &[u8]) -> Result<DwarfDebugInfo, String> {
    if bytes.len() > 64 * 1024 * 1024 {
        return Err("DWARF input exceeds 64 MiB".into());
    }
    let file = object::read::elf::ElfFile32::<Endianness>::parse(bytes).map_err(error)?;
    let header = file.elf_header();
    let endian = file.endian();
    if !file.is_little_endian()
        || header.e_type(endian) != elf::ET_EXEC
        || header.e_machine(endian) != elf::EM_NONE
        || header.e_flags(endian).0 != 0x4b4f4801
        || header.e_entry(endian) != 0
        || header.e_phnum(endian) != 0
    {
        return Err("Unsupported DWARF ELF header".into());
    }
    let mut budget = Budget::default();
    let mut sections = std::collections::HashMap::new();
    for section in file.sections() {
        budget.charge(256)?;
        let name = section.name().map_err(error)?;
        if sections
            .insert(name, section.data().map_err(error)?)
            .is_some()
        {
            return Err("Duplicate DWARF ELF section".into());
        }
    }
    if sections.get(".note.koh").copied()
        != Some(b"\x04\0\0\0\x08\0\0\0\x01\0\0\0Koh\0SM83\x01\0\0\0".as_slice())
    {
        return Err("Missing or unsupported Koh SM83 note".into());
    }
    for name in [".debug_info", ".debug_abbrev"] {
        if !sections.contains_key(name) {
            return Err(format!("Missing {name}"));
        }
    }
    let dwarf = gimli::Dwarf::load(|id| {
        Ok::<_, String>(Input::new(
            sections.get(id.name()).copied().unwrap_or(&[]),
            LittleEndian,
        ))
    })?;
    let mut output = DwarfDebugInfo::default();
    let mut units = dwarf.units();
    let mut unit_count = 0;
    while let Some(header) = units.next().map_err(error)? {
        budget.charge(256)?;
        unit_count += 1;
        if header.version() != 5
            || header.address_size() != 4
            || header.format() != gimli::Format::Dwarf32
        {
            return Err("Expected DWARF 5 with 32-bit banked addresses".into());
        }
        let unit = dwarf.unit(header).map_err(error)?;
        if let Some(program) = unit.line_program.clone() {
            let mut pending: Option<(u64, String, u32)> = None;
            let mut previous_address = None;
            let mut rows = program.rows();
            while let Some((_, row)) = rows.next_row().map_err(error)? {
                budget.charge(128)?;
                if previous_address.is_some_and(|address| row.address() < address) {
                    return Err("DWARF line addresses are not ordered".into());
                }
                previous_address = if row.end_sequence() {
                    None
                } else {
                    Some(row.address())
                };
                if let Some((start, file, line)) = pending.take() {
                    let count = row
                        .address()
                        .checked_sub(start)
                        .ok_or("DWARF line addresses are not ordered")?;
                    let (bank, address) = split(start)?;
                    if count > u64::from(0x10000 - address) {
                        return Err("DWARF source range crosses a CPU address boundary".into());
                    }
                    if count > 0 {
                        output.lines.push(LinkedLine {
                            bank,
                            address,
                            count: count as u32,
                            file,
                            line,
                            expansion: vec![],
                        });
                    }
                }
                if !row.end_sequence() {
                    split(row.address())?;
                    if let Some(line) = row.line() {
                        let line = u32::try_from(line.get())
                            .map_err(|_| "DWARF line number exceeds 32 bits")?;
                        pending = Some((
                            row.address(),
                            path(&dwarf, &unit, row.file_index(), &mut budget)?,
                            line,
                        ));
                    }
                }
            }
            if previous_address.is_some() {
                return Err("Unterminated DWARF line sequence".into());
            }
        }
        let mut entries = unit.entries();
        while let Some(entry) = entries.next_dfs().map_err(error)? {
            budget.charge(128)?;
            let kind = match entry.tag() {
                gimli::DW_TAG_compile_unit if entry.depth() == 0 => continue,
                gimli::DW_TAG_label => 0,
                gimli::DW_TAG_constant => 1,
                gimli::DW_TAG_variable => 2,
                tag => return Err(format!("Unsupported DWARF symbol tag {tag:?}")),
            };
            if entry.depth() != 1 {
                return Err("Unsupported DWARF symbol scope".into());
            }
            let name = entry
                .attr_value(gimli::DW_AT_name)
                .ok_or("Missing DWARF symbol name")?;
            let name = budget.text(dwarf.attr_string(&unit, name).map_err(error)?)?;
            let (value, bank) = if kind == 1 {
                let value = entry
                    .attr_value(gimli::DW_AT_const_value)
                    .and_then(|v| v.sdata_value())
                    .ok_or("Invalid DWARF signed constant")?;
                (
                    i32::try_from(value).map_err(|_| "DWARF constant exceeds signed 32 bits")?,
                    None,
                )
            } else {
                let address = if kind == 0 {
                    match entry.attr_value(gimli::DW_AT_low_pc) {
                        Some(AttributeValue::Addr(address)) => address,
                        _ => return Err("Missing DWARF label address".into()),
                    }
                } else {
                    let Some(AttributeValue::Exprloc(expression)) =
                        entry.attr_value(gimli::DW_AT_location)
                    else {
                        return Err("Missing DWARF RAM location".into());
                    };
                    let mut operations = expression.operations(unit.encoding());
                    let Some(gimli::Operation::Address { address }) =
                        operations.next().map_err(error)?
                    else {
                        return Err("Unsupported DWARF RAM location expression".into());
                    };
                    if operations.next().map_err(error)?.is_some() {
                        return Err("Unsupported DWARF RAM location expression".into());
                    }
                    address
                };
                let (bank, address) = split(address)?;
                (address as i32, Some(bank))
            };
            let source = match entry.attr_value(gimli::DW_AT_decl_file) {
                None => None,
                Some(AttributeValue::FileIndex(index)) => {
                    let line = entry
                        .attr_value(gimli::DW_AT_decl_line)
                        .and_then(|v| v.udata_value())
                        .ok_or("Missing DWARF declaration line")?;
                    let column = entry
                        .attr_value(gimli::DW_AT_decl_column)
                        .and_then(|v| v.udata_value())
                        .unwrap_or(0);
                    Some(Provenance {
                        file: path(&dwarf, &unit, index, &mut budget)?,
                        line: u32::try_from(line).map_err(|_| "DWARF line exceeds 32 bits")?,
                        column: u32::try_from(column)
                            .map_err(|_| "DWARF column exceeds 32 bits")?,
                        expansion: vec![],
                    })
                }
                _ => return Err("Invalid DWARF declaration file".into()),
            };
            output.symbols.push(LinkedSymbol {
                name,
                value,
                bank,
                kind,
                source,
            });
        }
    }
    if unit_count == 0 {
        return Err("Missing DWARF compilation unit".into());
    }
    Ok(output)
}
