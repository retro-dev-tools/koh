use crate::LinkResult;
use gimli::write::{
    Address, AttributeValue, DwarfUnit, EndianVec, LineProgram, LineString, Sections, Unit,
    UnitEntryId,
};
use gimli::{Encoding, Format, LittleEndian};
use object::write::elf::{FileHeader, SectionHeader, Writer};
use object::{Endianness, elf};

pub(super) const MACRO_EXPANSION: gimli::DwTag = gimli::DwTag(0x4b00);
pub(super) const SOURCE_RANGE: gimli::DwAt = gimli::DwAt(0x2b00);
pub(super) const MAX_MACRO_DEPTH: usize = 256;

fn expansion(
    unit: &mut Unit,
    program: &mut LineProgram,
    mut parent: UnitEntryId,
    frames: &[koh_objects::SourceLocation],
) -> Result<Option<UnitEntryId>, String> {
    if frames.len() > MAX_MACRO_DEPTH {
        return Err("DWARF macro nesting exceeds 256 frames".into());
    }
    let mut first = None;
    for frame in frames {
        let file = program.add_file(
            LineString::String(text(&frame.file)?),
            program.default_directory(),
            None,
        );
        let id = unit.add(parent, MACRO_EXPANSION);
        let entry = unit.get_mut(id);
        entry.set(
            gimli::DW_AT_call_file,
            AttributeValue::FileIndex(Some(file)),
        );
        entry.set(
            gimli::DW_AT_call_line,
            AttributeValue::Udata(frame.line.into()),
        );
        entry.set(
            gimli::DW_AT_call_column,
            AttributeValue::Udata(frame.column.into()),
        );
        first.get_or_insert(id);
        parent = id;
    }
    Ok(first)
}

fn address(bank: u32, value: u32) -> Result<u64, String> {
    if bank > u16::MAX.into() || value > u16::MAX.into() {
        return Err("DWARF address exceeds bank:16/address:16 encoding".into());
    }
    Ok((u64::from(bank) << 16) | u64::from(value))
}

fn text(value: &str) -> Result<Vec<u8>, String> {
    if value.is_empty() || value.contains('\0') {
        return Err("DWARF names must be nonempty and contain no NUL".into());
    }
    Ok(value.as_bytes().to_vec())
}

pub fn write(result: &LinkResult) -> Result<Vec<u8>, String> {
    let mut budget = 1024usize;
    for length in result
        .lines
        .iter()
        .map(|line| line.file.len())
        .chain(result.symbols.iter().flat_map(|symbol| {
            [
                symbol.name.len(),
                symbol.source.as_ref().map_or(0, |source| source.file.len()),
            ]
        }))
        .chain(
            result
                .lines
                .iter()
                .flat_map(|line| line.expansion.iter().map(|frame| frame.file.len())),
        )
        .chain(
            result
                .symbols
                .iter()
                .filter_map(|symbol| symbol.source.as_ref())
                .flat_map(|source| source.expansion.iter().map(|frame| frame.file.len())),
        )
    {
        budget = length
            .checked_mul(2)
            .and_then(|length| length.checked_add(256))
            .and_then(|length| budget.checked_add(length))
            .filter(|budget| *budget <= 64 * 1024 * 1024)
            .ok_or("DWARF exceeds 64 MiB output budget")?;
    }
    let encoding = Encoding {
        format: Format::Dwarf32,
        version: 5,
        address_size: 4,
    };
    let mut dwarf = DwarfUnit::new(encoding);
    let mut program = LineProgram::new(
        encoding,
        gimli::LineEncoding::default(),
        LineString::String(b".".to_vec()),
        None,
        LineString::String(b"koh-link".to_vec()),
        None,
    );
    let directory = program.default_directory();
    let root = dwarf.unit.root();
    let mut line_index = 0;
    for line in &result.lines {
        let start = address(line.bank, line.address)?;
        let end = line
            .address
            .checked_add(line.count)
            .ok_or("DWARF range overflow")?;
        if end > 0x10000 {
            return Err("DWARF source range crosses a CPU address boundary".into());
        }
        if line.count == 0 {
            continue;
        }
        let file = program.add_file(LineString::String(text(&line.file)?), directory, None);
        program.begin_sequence(Some(Address::Constant(start)));
        program.row().file = file;
        program.row().line = u64::from(line.line);
        program.generate_row();
        program.end_sequence(u64::from(line.count));
        if let Some(id) = expansion(&mut dwarf.unit, &mut program, root, &line.expansion)? {
            if line.line == 0 {
                return Err("Macro ancestry requires a nonzero source line".into());
            }
            let entry = dwarf.unit.get_mut(id);
            entry.set(SOURCE_RANGE, AttributeValue::Udata(line_index));
            entry.set(
                gimli::DW_AT_low_pc,
                AttributeValue::Address(Address::Constant(start)),
            );
            entry.set(
                gimli::DW_AT_high_pc,
                AttributeValue::Udata(line.count.into()),
            );
            entry.set(
                gimli::DW_AT_decl_file,
                AttributeValue::FileIndex(Some(file)),
            );
            entry.set(
                gimli::DW_AT_decl_line,
                AttributeValue::Udata(line.line.into()),
            );
        }
        if line.line != 0 {
            line_index += 1;
        }
    }
    dwarf.unit.get_mut(root).set(
        gimli::DW_AT_name,
        AttributeValue::String(b"koh-link".to_vec()),
    );
    dwarf
        .unit
        .get_mut(root)
        .set(gimli::DW_AT_comp_dir, AttributeValue::String(b".".to_vec()));
    dwarf.unit.get_mut(root).set(
        gimli::DW_AT_producer,
        AttributeValue::String(b"koh-link".to_vec()),
    );
    dwarf
        .unit
        .get_mut(root)
        .set(gimli::DW_AT_stmt_list, AttributeValue::LineProgramRef);
    for symbol in &result.symbols {
        let tag = match symbol.kind {
            0 => gimli::DW_TAG_label,
            1 => gimli::DW_TAG_constant,
            2 => gimli::DW_TAG_variable,
            _ => return Err("Unsupported DWARF symbol kind".into()),
        };
        let id = dwarf.unit.add(root, tag);
        if let Some(source) = &symbol.source {
            let file = program.add_file(LineString::String(text(&source.file)?), directory, None);
            let entry = dwarf.unit.get_mut(id);
            entry.set(
                gimli::DW_AT_decl_file,
                AttributeValue::FileIndex(Some(file)),
            );
            entry.set(
                gimli::DW_AT_decl_line,
                AttributeValue::Udata(source.line.into()),
            );
            entry.set(
                gimli::DW_AT_decl_column,
                AttributeValue::Udata(source.column.into()),
            );
            expansion(&mut dwarf.unit, &mut program, id, &source.expansion)?;
        }
        let entry = dwarf.unit.get_mut(id);
        entry.set(
            gimli::DW_AT_name,
            AttributeValue::String(text(&symbol.name)?),
        );
        if symbol.kind == 1 {
            entry.set(
                gimli::DW_AT_const_value,
                AttributeValue::Sdata(symbol.value.into()),
            );
        } else {
            let value = u32::try_from(symbol.value).map_err(|_| "Negative DWARF label address")?;
            let value = Address::Constant(address(symbol.bank.unwrap_or(0), value)?);
            if symbol.kind == 0 {
                entry.set(gimli::DW_AT_low_pc, AttributeValue::Address(value));
            } else {
                let mut location = gimli::write::Expression::new();
                location.op_addr(value);
                entry.set(gimli::DW_AT_location, AttributeValue::Exprloc(location));
            }
        }
    }
    dwarf.unit.line_program = program;
    let mut sections = Sections::new(EndianVec::new(LittleEndian));
    dwarf.write(&mut sections).map_err(|e| e.to_string())?;
    let mut data = vec![(
        b".note.koh".to_vec(),
        b"\x04\0\0\0\x08\0\0\0\x01\0\0\0Koh\0SM83\x01\0\0\0".to_vec(),
    )];
    sections
        .for_each(|id, bytes| {
            if !bytes.slice().is_empty() {
                data.push((id.name().as_bytes().to_vec(), bytes.slice().to_vec()));
            }
            Ok::<_, gimli::write::Error>(())
        })
        .map_err(|e| e.to_string())?;
    elf_file(&data)
}

fn elf_file(sections: &[(Vec<u8>, Vec<u8>)]) -> Result<Vec<u8>, String> {
    let mut output = Vec::new();
    let mut header = Vec::new();
    {
        let mut writer = Writer::new_single_phase(Endianness::Little, false, &mut output);
        writer
            .write_file_header(&FileHeader {
                os_abi: elf::ELFOSABI_NONE,
                abi_version: 0,
                e_type: elf::ET_EXEC,
                e_machine: elf::EM_NONE,
                e_entry: 0,
                e_flags: elf::FileFlags(0x4b4f4801),
            })
            .map_err(|e| e.to_string())?;
        let mut headers = Vec::new();
        for (name, data) in sections {
            let note = name == b".note.koh";
            let alignment = if note { 4 } else { 1 };
            let name = writer.add_section_name(name);
            let offset = writer.write_align(alignment);
            writer.write(data);
            headers.push(SectionHeader {
                sh_name: writer.section_name_offset(Some(name)),
                sh_type: if note {
                    elf::SHT_NOTE
                } else {
                    elf::SHT_PROGBITS
                },
                sh_offset: offset,
                sh_size: data.len() as u64,
                sh_addralign: alignment,
                ..Default::default()
            });
        }
        writer.write_shstrtab().map_err(|e| e.to_string())?;
        writer.write_null_section_header();
        for header in &headers {
            writer.write_section_header(header);
        }
        writer.write_shstrtab_section_header();
        writer
            .write_file_header_to(&mut header)
            .map_err(|e| e.to_string())?;
    }
    output[..header.len()].copy_from_slice(&header);
    Ok(output)
}
