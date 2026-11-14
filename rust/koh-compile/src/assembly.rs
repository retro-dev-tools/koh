use clrmeta::{Metadata, MetadataRoot, TablesHeader, reader::Reader};
use object::{FileKind, pe, read::pe::SectionTable};
use std::collections::HashSet;

const MAX_IMAGE: usize = 64 * 1024 * 1024;
const MAX_ROWS: usize = 262_144;

#[derive(Debug)]
pub struct Assembly<'a> {
    metadata: Metadata,
    pub entry_point: u32,
    bytes: &'a [u8],
    sections: SectionTable<'a>,
}

#[derive(Debug)]
pub struct MethodBody<'a> {
    pub il: &'a [u8],
    pub max_stack: u16,
    pub local_signature: u32,
    pub init_locals: bool,
    pub extra_sections: bool,
}

fn range(bytes: &[u8], offset: usize, length: usize) -> Result<&[u8], String> {
    offset
        .checked_add(length)
        .and_then(|end| bytes.get(offset..end))
        .ok_or_else(|| "Truncated or overflowing image range".into())
}

fn u16_at(bytes: &[u8], offset: usize) -> Result<u16, String> {
    let data = range(bytes, offset, 2)?;
    Ok(u16::from_le_bytes([data[0], data[1]]))
}

fn u32_at(bytes: &[u8], offset: usize) -> Result<u32, String> {
    let data = range(bytes, offset, 4)?;
    Ok(u32::from_le_bytes([data[0], data[1], data[2], data[3]]))
}

fn metadata(bytes: &[u8]) -> Result<Metadata, String> {
    range(bytes, 16, u32_at(bytes, 12)? as usize)?;
    let mut root_reader = Reader::new(bytes);
    let root =
        MetadataRoot::parse_from_reader(&mut root_reader).map_err(|error| error.to_string())?;
    let mut names = HashSet::new();
    let mut ranges = Vec::new();
    let header_size = root_reader.position();
    for stream in &root.streams {
        if !names.insert(&stream.name) {
            return Err(format!("Duplicate metadata stream {}", stream.name));
        }
        let start = stream.offset as usize;
        let data = range(bytes, start, stream.size as usize)?;
        if start < header_size || start % 4 != 0 {
            return Err(format!("Invalid metadata stream position: {}", stream.name));
        }
        let end = start + data.len();
        ranges.push((start, end));
    }
    ranges.sort_unstable();
    if ranges.windows(2).any(|pair| pair[0].1 > pair[1].0) {
        return Err("Overlapping metadata streams".into());
    }
    let tables = root.tables_stream().ok_or("Missing metadata tables")?;
    if names.contains(&"#~".to_string()) && names.contains(&"#-".to_string()) {
        return Err("Multiple metadata tables streams".into());
    }
    if tables.name != "#~" {
        return Err("Uncompressed metadata tables (#-) are not supported".into());
    }
    let data = range(bytes, tables.offset as usize, tables.size as usize)?;
    let mut reader = Reader::new(data);
    let header = TablesHeader::parse(&mut reader, false).map_err(|e| e.to_string())?;
    if header.valid >> 45 != 0 || header.heap_sizes & !7 != 0 {
        return Err("Unsupported metadata table or heap flags".into());
    }
    let context = header.context();
    let mut rows = 0usize;
    let mut size = 0usize;
    for (table, count) in header.tables() {
        if count != 0 && context.row_size(table) == 0 {
            return Err(format!(
                "Metadata table {table:?} has no supported row layout"
            ));
        }
        rows = rows
            .checked_add(count as usize)
            .ok_or("Metadata row overflow")?;
        size = size
            .checked_add(
                context
                    .row_size(table)
                    .checked_mul(count as usize)
                    .ok_or("Metadata table size overflow")?,
            )
            .ok_or("Metadata table size overflow")?;
    }
    if rows > MAX_ROWS || size > reader.remaining() {
        return Err("Metadata tables exceed input or 262144-row limit".into());
    }
    let result = Metadata::parse(bytes).map_err(|e| e.to_string())?;
    if result.assemblies.len() != 1 || result.modules.len() != 1 {
        return Err("Expected one assembly and one module".into());
    }
    for ty in &result.type_defs {
        result
            .strings
            .get(ty.type_name)
            .map_err(|e| e.to_string())?;
        result
            .strings
            .get(ty.type_namespace)
            .map_err(|e| e.to_string())?;
    }
    for method in &result.method_defs {
        result.strings.get(method.name).map_err(|e| e.to_string())?;
        result
            .blobs
            .get(method.signature)
            .map_err(|e| e.to_string())?;
    }
    let mut previous = 1;
    for ty in &result.type_defs {
        if ty.method_list < previous || ty.method_list as usize > result.method_defs.len() + 1 {
            return Err("Invalid TypeDef method range".into());
        }
        previous = ty.method_list;
    }
    if !result.method_defs.is_empty()
        && result
            .type_defs
            .first()
            .is_none_or(|ty| ty.method_list != 1)
    {
        return Err("Method definitions have no declaring type".into());
    }
    Ok(result)
}

impl<'a> Assembly<'a> {
    pub fn parse(bytes: &'a [u8]) -> Result<Self, String> {
        if bytes.len() > MAX_IMAGE {
            return Err("Assembly exceeds 64 MiB limit".into());
        }
        let (sections, directory) = match FileKind::parse(bytes).map_err(|e| e.to_string())? {
            FileKind::Pe32 => {
                let file = object::read::pe::PeFile32::parse(bytes).map_err(|e| e.to_string())?;
                (
                    file.section_table(),
                    file.data_directory(pe::IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR),
                )
            }
            FileKind::Pe64 => {
                let file = object::read::pe::PeFile64::parse(bytes).map_err(|e| e.to_string())?;
                (
                    file.section_table(),
                    file.data_directory(pe::IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR),
                )
            }
            _ => return Err("Expected a PE/CLI assembly".into()),
        };
        let cli = directory
            .ok_or("PE image has no CLI header")?
            .data(bytes, &sections)
            .map_err(|e| e.to_string())?;
        if u32_at(cli, 0)? < 72 || u32_at(cli, 0)? as usize > cli.len() || cli.len() < 72 {
            return Err("Truncated CLI header".into());
        }
        let flags = u32_at(cli, 16)?;
        if flags & 1 == 0 || flags & 0x10 != 0 {
            return Err("Mixed-mode or native-entry assemblies are unsupported".into());
        }
        let metadata_rva = u32_at(cli, 8)?;
        let metadata_size = u32_at(cli, 12)? as usize;
        let data = sections
            .pe_data_at(bytes, metadata_rva)
            .ok_or("Invalid CLI metadata RVA")?;
        let metadata = metadata(range(data, 0, metadata_size)?)?;
        let entry_point = u32_at(cli, 20)?;
        let assembly = Self {
            metadata,
            entry_point,
            bytes,
            sections,
        };
        if entry_point != 0 {
            assembly.method(entry_point)?;
        }
        Ok(assembly)
    }

    pub fn name(&self) -> Result<&str, String> {
        self.metadata
            .strings
            .get(self.metadata.assemblies[0].name)
            .map_err(|e| e.to_string())
    }

    pub fn metadata(&self) -> &Metadata {
        &self.metadata
    }

    pub fn method(&self, token: u32) -> Result<&clrmeta::MethodDefRow, String> {
        if token >> 24 != 6 {
            return Err(format!("Expected MethodDef token, got 0x{token:08x}"));
        }
        (token & 0x00ff_ffff)
            .checked_sub(1)
            .and_then(|index| self.metadata.method_defs.get(index as usize))
            .ok_or_else(|| format!("Invalid MethodDef token 0x{token:08x}"))
    }

    pub fn method_name(&self, token: u32) -> Result<String, String> {
        let method = self.method(token)?;
        let (_, owner) = self
            .metadata
            .get_method_owner(token & 0x00ff_ffff)
            .ok_or("Method has no declaring type")?;
        let namespace = self
            .metadata
            .strings
            .get(owner.type_namespace)
            .map_err(|e| e.to_string())?;
        let ty = self
            .metadata
            .strings
            .get(owner.type_name)
            .map_err(|e| e.to_string())?;
        let name = self
            .metadata
            .strings
            .get(method.name)
            .map_err(|e| e.to_string())?;
        let separator = if namespace.is_empty() { "" } else { "." };
        Ok(format!("{namespace}{separator}{ty}::{name}"))
    }

    pub fn method_body(&self, token: u32) -> Result<MethodBody<'a>, String> {
        let method = self.method(token)?;
        let name = self.method_name(token)?;
        if method.rva == 0 || method.impl_flags & 3 != 0 {
            return Err(format!("{name} has no managed CIL body"));
        }
        let data = self
            .sections
            .pe_data_at(self.bytes, method.rva)
            .ok_or_else(|| format!("Invalid RVA for {name}"))?;
        let body = body(data).map_err(|error| format!("{name}: {error}"))?;
        let signature_row = (body.local_signature & 0x00ff_ffff) as usize;
        if body.local_signature != 0
            && (signature_row == 0 || signature_row > self.metadata.stand_alone_sigs.len())
        {
            return Err(format!(
                "{name}: invalid local signature token 0x{:08x}",
                body.local_signature
            ));
        }
        Ok(body)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clrmeta::StreamHeader;
    use rstest::rstest;

    fn root(streams: Vec<StreamHeader>, trailing: usize) -> Vec<u8> {
        let mut bytes = MetadataRoot {
            major_version: 1,
            minor_version: 1,
            reserved: 0,
            version: "v4.0.30319".into(),
            flags: 0,
            streams,
        }
        .write();
        bytes.resize(bytes.len() + trailing, 0);
        bytes
    }

    #[rstest]
    #[case(0, 24, "position")]
    #[case(44, u32::MAX, "range")]
    #[case(u32::MAX, 4, "range")]
    fn rejects_invalid_stream_ranges(
        #[case] offset: u32,
        #[case] size: u32,
        #[case] diagnostic: &str,
    ) {
        let bytes = root(
            vec![StreamHeader {
                name: "#~".into(),
                offset,
                size,
            }],
            24,
        );
        assert!(metadata(&bytes).unwrap_err().contains(diagnostic));
    }

    #[rstest]
    #[case("#~", "Duplicate")]
    #[case("#Strings", "Overlapping")]
    fn rejects_ambiguous_streams(#[case] second: &str, #[case] diagnostic: &str) {
        let bytes = root(
            vec![
                StreamHeader {
                    name: "#~".into(),
                    offset: 64,
                    size: 24,
                },
                StreamHeader {
                    name: second.into(),
                    offset: 64,
                    size: 24,
                },
            ],
            40,
        );
        assert!(metadata(&bytes).unwrap_err().contains(diagnostic));
    }

    #[test]
    fn rejects_unbounded_table_allocations_before_parsing_rows() {
        let mut bytes = root(
            vec![StreamHeader {
                name: "#~".into(),
                offset: 44,
                size: 28,
            }],
            28,
        );
        bytes[52] = 1;
        bytes[68..72].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(metadata(&bytes).unwrap_err().contains("tables exceed"));
    }

    #[rstest]
    #[case(&[0x06, 0x2a], true)]
    #[case(&[0x0a, 0x00, 0x2a], true)]
    #[case(&[], false)]
    #[case(&[0], false)]
    #[case(&[0x0a, 0x2a], false)]
    #[case(&[3, 0x30], false)]
    #[case(&[3, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], false)]
    fn validates_tiny_and_fat_headers(#[case] bytes: &[u8], #[case] valid: bool) {
        assert_eq!(body(bytes).is_ok(), valid);
    }

    #[test]
    fn preserves_fat_header_contract() {
        let bytes = [0x1b, 0x30, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0x11, 0x2a];
        let body = body(&bytes).unwrap();
        assert_eq!(body.il, &[0x2a]);
        assert_eq!(body.max_stack, 3);
        assert_eq!(body.local_signature, 0x11000001);
        assert!(body.init_locals);
        assert!(body.extra_sections);
    }
}

fn body(bytes: &[u8]) -> Result<MethodBody<'_>, String> {
    let first = *bytes.first().ok_or("Missing CIL method header")?;
    match first & 3 {
        2 => Ok(MethodBody {
            il: range(bytes, 1, (first >> 2) as usize)?,
            max_stack: 8,
            local_signature: 0,
            init_locals: false,
            extra_sections: false,
        }),
        3 => {
            let flags = u16_at(bytes, 0)?;
            let header_size = ((flags >> 12) as usize) * 4;
            if header_size != 12 || flags & 0x0fe4 != 0 {
                return Err("Invalid or unsupported fat CIL header".into());
            }
            let local_signature = u32_at(bytes, 8)?;
            if local_signature != 0 && local_signature >> 24 != 0x11 {
                return Err("Invalid local signature token".into());
            }
            Ok(MethodBody {
                il: range(bytes, header_size, u32_at(bytes, 4)? as usize)?,
                max_stack: u16_at(bytes, 2)?,
                local_signature,
                init_locals: flags & 0x10 != 0,
                extra_sections: flags & 8 != 0,
            })
        }
        _ => Err("Invalid CIL method header".into()),
    }
}
