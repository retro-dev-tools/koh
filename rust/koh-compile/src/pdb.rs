use clrmeta::reader::Reader;

#[derive(Debug)]
pub struct PortablePdb {
    pub id: [u8; 20],
    pub documents: Vec<String>,
    pub methods: Vec<Vec<SequencePoint>>,
}

impl PortablePdb {
    pub fn parse(bytes: &[u8], methods: usize) -> Result<Self, String> {
        parse_metadata(bytes, methods, 64 * 1024 * 1024).map_err(|e| format!("Portable PDB: {e}"))
    }

    pub fn source(&self, method: u32, offset: u32) -> Option<koh_objects::Provenance> {
        if method >> 24 != 6 {
            return None;
        }
        let points = self
            .methods
            .get((method & 0xffffff).checked_sub(1)? as usize)?;
        let point = points.get(
            points
                .partition_point(|p| p.il_offset <= offset)
                .checked_sub(1)?,
        )?;
        let source = point.source.as_ref()?;
        Some(koh_objects::Provenance {
            file: self
                .documents
                .get(source.document.checked_sub(1)? as usize)?
                .clone(),
            line: source.line,
            column: source.column,
            expansion: vec![],
        })
    }
}

type Error = Box<dyn std::error::Error>;

fn blob(heap: &[u8], index: u32) -> Result<&[u8], Error> {
    let mut reader = Reader::new(
        heap.get(index as usize..)
            .ok_or("Blob index is out of range")?,
    );
    let count = reader.read_compressed_uint()? as usize;
    Ok(reader.read_bytes(count)?)
}

fn document_name(heap: &[u8], index: u32) -> Result<String, Error> {
    if index == 0 {
        return Err("Document name must not be nil".into());
    }
    let bytes = blob(heap, index)?;
    let length = match bytes.first().ok_or("Empty document name blob")? {
        0..=0x7f => 1,
        0xc2..=0xdf => 2,
        0xe0..=0xef => 3,
        0xf0..=0xf4 => 4,
        _ => return Err("Invalid document separator".into()),
    };
    let separator =
        std::str::from_utf8(bytes.get(..length).ok_or("Truncated document separator")?)?;
    let separator = if separator == "\0" { "" } else { separator };
    let parts = bytes
        .get(length..)
        .filter(|p| !p.is_empty())
        .ok_or("Missing document components")?;
    let mut reader = Reader::new(parts);
    let mut result = String::new();
    let mut first = true;
    while reader.remaining() != 0 {
        let part = reader.read_compressed_uint()?;
        let text = if part == 0 {
            ""
        } else {
            std::str::from_utf8(blob(heap, part)?)?
        };
        if result.len() + text.len() + separator.len() > 65_535 || text.contains('\0') {
            return Err("Document name exceeds limit or contains NUL".into());
        }
        if !first {
            result.push_str(separator);
        }
        result.push_str(text);
        first = false;
    }
    Ok(result)
}

fn parse_metadata(
    bytes: &[u8],
    method_count: usize,
    mut work_limit: usize,
) -> Result<PortablePdb, Error> {
    use clrmeta::{MetadataRoot, TablesHeader};
    use std::collections::BTreeSet;
    if bytes.len() > 64 * 1024 * 1024 || method_count > 262_144 {
        return Err("Image or method count exceeds limit".into());
    }
    let mut reader = Reader::new(bytes);
    let root = MetadataRoot::parse_from_reader(&mut reader)?;
    let mut names = BTreeSet::new();
    let mut ranges = Vec::new();
    for stream in &root.streams {
        let start = stream.offset as usize;
        let end = start
            .checked_add(stream.size as usize)
            .ok_or("Stream range overflow")?;
        if !names.insert(&stream.name)
            || start < reader.position()
            || start % 4 != 0
            || bytes.get(start..end).is_none()
        {
            return Err("Duplicate or invalid stream range".into());
        }
        ranges.push((start, end));
    }
    ranges.sort_unstable();
    if ranges.windows(2).any(|pair| pair[0].1 > pair[1].0) {
        return Err("Overlapping streams".into());
    }
    let stream = |name| -> Result<&[u8], Error> {
        let header = root
            .find_stream(name)
            .ok_or_else(|| format!("Missing {name} stream"))?;
        Ok(&bytes[header.offset as usize..header.offset as usize + header.size as usize])
    };
    let mut identity = Reader::new(stream("#Pdb")?);
    let id = identity.read_bytes(20)?.try_into()?;
    identity.read_u32()?;
    let referenced = identity.read_u64()?;
    if referenced >> 45 != 0 {
        return Err("Invalid referenced type-system tables".into());
    }
    let mut rows = [0u32; 64];
    for (table, count) in rows.iter_mut().enumerate() {
        if referenced & (1u64 << table) != 0 {
            *count = identity.read_u32()?;
        }
    }
    if identity.remaining() != 0 || rows[6] as usize != method_count {
        return Err("Referenced MethodDef row count does not match assembly".into());
    }
    let mut reader = Reader::new(stream("#~")?);
    let header = TablesHeader::parse(&mut reader, false)?;
    if header.valid & !(0xffu64 << 48) != 0 || header.heap_sizes & !7 != 0 {
        return Err("Unsupported debug table or heap flags".into());
    }
    let mut count = 0u64;
    for (table, &n) in header.row_counts.iter().enumerate() {
        count += u64::from(n);
        if table >= 48 {
            rows[table] = n;
        }
    }
    if count > 262_144 || (rows[49] != 0 && rows[49] as usize != method_count) {
        return Err("Debug table count exceeds limit or mismatches MethodDef".into());
    }
    let width = |table: usize| if rows[table] >= 65_536 { 4 } else { 2 };
    let heap_width = |bit| if header.heap_sizes & bit != 0 { 4 } else { 2 };
    let b = heap_width(4);
    let g = heap_width(2);
    let s = heap_width(1);
    let custom = [
        6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43, 48, 50,
        51, 52, 53,
    ]
    .iter()
    .any(|&table| rows[table] >= 2048);
    let sizes = [
        2 * b + 2 * g,
        width(48) + b,
        width(6) + width(53) + width(51) + width(52) + 8,
        4 + s,
        s + b,
        width(53) + b,
        2 * width(6),
        if custom { 4 } else { 2 } + g + b,
    ];
    let required = sizes
        .iter()
        .enumerate()
        .map(|(i, size)| u64::from(rows[48 + i]) * *size as u64)
        .sum::<u64>();
    if required > reader.remaining() as u64 {
        return Err("Truncated debug tables".into());
    }
    let heap = if root.find_stream("#Blob").is_some() {
        stream("#Blob")?
    } else {
        &[]
    };
    let mut documents = Vec::new();
    let mut document_bytes = 0usize;
    for _ in 0..rows[48] {
        let index = reader.read_index(b == 4)?;
        work_limit = work_limit
            .checked_sub(blob(heap, index)?.len())
            .ok_or("PDB decode work exceeds limit")?;
        let name = document_name(heap, index)?;
        reader.read_index(g == 4)?;
        blob(heap, reader.read_index(b == 4)?)?;
        reader.read_index(g == 4)?;
        document_bytes += name.len();
        if document_bytes > 4 * 1024 * 1024 {
            return Err("Expanded document names exceed limit".into());
        }
        documents.push(name);
    }
    let mut methods = Vec::new();
    let mut total_points = 0usize;
    for _ in 0..rows[49] {
        let document = reader.read_index(width(48) == 4)?;
        let index = reader.read_index(b == 4)?;
        if document as usize > documents.len() {
            return Err("Method document is out of range".into());
        }
        let points = if index == 0 {
            vec![]
        } else {
            let bytes = blob(heap, index)?;
            work_limit = work_limit
                .checked_sub(bytes.len())
                .ok_or("PDB decode work exceeds limit")?;
            sequence_points(bytes, document, documents.len())?
        };
        total_points += points.len();
        if total_points > 1_000_000 {
            return Err("Total sequence point count exceeds limit".into());
        }
        methods.push(points);
    }
    Ok(PortablePdb {
        id,
        documents,
        methods,
    })
}

fn signed(reader: &mut Reader<'_>) -> Result<i32, clrmeta::Error> {
    let start = reader.position();
    let value = reader.read_compressed_uint()?;
    let bits = match reader.position() - start {
        1 => 6,
        2 => 13,
        _ => 28,
    };
    Ok((value >> 1) as i32 | if value & 1 == 0 { 0 } else { -1 << bits })
}

#[derive(Debug, PartialEq, Eq)]
pub struct Position {
    pub document: u32,
    pub line: u32,
    pub column: u32,
}

#[derive(Debug, PartialEq, Eq)]
pub struct SequencePoint {
    pub il_offset: u32,
    pub source: Option<Position>,
}

fn sequence_points(
    bytes: &[u8],
    mut document: u32,
    documents: usize,
) -> Result<Vec<SequencePoint>, Box<dyn std::error::Error>> {
    let mut reader = Reader::new(bytes);
    reader.read_compressed_uint()?;
    if document == 0 {
        document = reader.read_compressed_uint()?;
    }
    let mut points = Vec::new();
    let mut offset = 0u32;
    let mut previous = None;
    loop {
        if document == 0 || document as usize > documents {
            return Err("Sequence point document is out of range".into());
        }
        let delta = reader.read_compressed_uint()?;
        if !points.is_empty() && delta == 0 {
            document = reader.read_compressed_uint()?;
            if document == 0 || document as usize > documents {
                return Err("Sequence point document is out of range".into());
            }
            if reader.remaining() == 0 {
                return Ok(points);
            }
            continue;
        }
        offset = offset
            .checked_add(delta)
            .ok_or("Sequence point IL offset overflow")?;
        if offset >= 0x2000_0000 || points.len() >= 1_000_000 {
            return Err("Sequence point offset or count exceeds limit".into());
        }
        let lines = reader.read_compressed_uint()?;
        let columns = if lines == 0 {
            i64::from(reader.read_compressed_uint()?)
        } else {
            i64::from(signed(&mut reader)?)
        };
        let source = if lines == 0 && columns == 0 {
            None
        } else {
            let (line, column) = if let Some((line, column)) = previous {
                (
                    line + i64::from(signed(&mut reader)?),
                    column + i64::from(signed(&mut reader)?),
                )
            } else {
                (
                    i64::from(reader.read_compressed_uint()?),
                    i64::from(reader.read_compressed_uint()?),
                )
            };
            let end_line = line + i64::from(lines);
            let end_column = column + columns;
            if !(0..0x2000_0000).contains(&line)
                || !(0..0x2000_0000).contains(&end_line)
                || line == 0xfeefee
                || end_line == 0xfeefee
                || !(0..0x10000).contains(&column)
                || !(0..0x10000).contains(&end_column)
            {
                return Err("Sequence point source span is out of range".into());
            }
            previous = Some((line, column));
            Some(Position {
                document,
                line: line as u32,
                column: column as u32,
            })
        };
        points.push(SequencePoint {
            il_offset: offset,
            source,
        });
        if reader.remaining() == 0 {
            return Ok(points);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rstest::rstest;

    fn pdb(methods: u32) -> Vec<u8> {
        use clrmeta::{MetadataRoot, StreamHeader};
        let mut heap = clrmeta::BlobHeap::new();
        let path = heap.add(b"game.cs");
        let name = heap.add(&[b'/', path as u8]);
        let sequence = heap.add(&[0, 0, 0, 4, 10, 2]);
        let mut tables = vec![0, 0, 0, 0, 2, 0, 0, 1];
        tables.extend((3u64 << 48).to_le_bytes());
        tables.extend(0u64.to_le_bytes());
        tables.extend(u32::from(methods != 0).to_le_bytes());
        tables.extend(methods.to_le_bytes());
        if methods != 0 {
            tables.extend((name as u16).to_le_bytes());
            tables.extend([0; 6]);
        }
        for _ in 0..methods {
            tables.extend(1u16.to_le_bytes());
            tables.extend((sequence as u16).to_le_bytes());
        }
        let mut identity = vec![0x42; 20];
        identity.extend(0u32.to_le_bytes());
        identity.extend((1u64 << 6).to_le_bytes());
        identity.extend(methods.to_le_bytes());
        let streams = [("#Pdb", identity), ("#~", tables), ("#Blob", heap.write())];
        let mut root = MetadataRoot {
            major_version: 1,
            minor_version: 1,
            reserved: 0,
            version: "PDB v1.0".into(),
            flags: 0,
            streams: streams
                .iter()
                .map(|(name, bytes)| StreamHeader {
                    name: (*name).into(),
                    offset: 0,
                    size: bytes.len() as u32,
                })
                .collect(),
        };
        let mut offset = root.header_size();
        for (header, (_, bytes)) in root.streams.iter_mut().zip(&streams) {
            header.offset = offset as u32;
            offset = (offset + bytes.len() + 3) & !3;
        }
        let mut bytes = root.write();
        for (header, (_, data)) in root.streams.iter().zip(streams) {
            bytes.resize(header.offset as usize, 0);
            bytes.extend(data);
        }
        bytes
    }

    #[test]
    fn standalone_metadata_preserves_identity_and_method_rows() {
        let parsed = PortablePdb::parse(&pdb(2), 2).unwrap();
        assert_eq!(parsed.id, [0x42; 20]);
        assert_eq!(parsed.documents, ["game.cs"]);
        assert_eq!(parsed.methods.len(), 2);
        assert_eq!(parsed.methods[1][0].source.as_ref().unwrap().line, 10);
        assert!(PortablePdb::parse(&pdb(2), 1).is_err());
    }

    #[test]
    fn empty_debug_tables_need_no_blob_heap() {
        let mut bytes = pdb(0);
        let offset = bytes.windows(6).position(|p| p == b"#Blob\0").unwrap();
        bytes[offset..offset + 5].copy_from_slice(b"#None");
        let parsed = PortablePdb::parse(&bytes, 0).unwrap();
        assert!(parsed.methods.is_empty());
        assert!(parsed.documents.is_empty());
    }

    #[test]
    fn truncated_metadata_diagnoses_without_panicking() {
        let bytes = pdb(2);
        for length in 0..bytes.len() {
            assert!(
                PortablePdb::parse(&bytes[..length], 2).is_err(),
                "length{length}"
            );
        }
    }

    #[test]
    fn repeated_blob_decoding_consumes_cumulative_work_budget() {
        assert!(parse_metadata(&pdb(1), 1, 8).is_ok());
        let error = parse_metadata(&pdb(2), 2, 13).unwrap_err();
        assert!(error.to_string().contains("decode work"), "{error}");
    }

    #[test]
    fn trailing_document_record_preserves_the_last_sequence_point() {
        let points = sequence_points(&[0, 0, 0, 1, 1, 1, 0, 1], 1, 1).unwrap();
        assert_eq!(points.len(), 1);
        assert_eq!(points[0].source.as_ref().unwrap().line, 1);
    }

    #[rstest]
    #[case(&[0xc0, 0x80, 1])]
    #[case(&[0xc2])]
    #[case(&[0xe0, 0x80, 0x80, 1])]
    #[case(&[0xed, 0xa0, 0x80, 1])]
    #[case(&[0xf4, 0x90, 0x80, 0x80, 1])]
    #[case(&[0x80, 1])]
    fn invalid_utf8_separators_diagnose(#[case] bytes: &[u8]) {
        let mut heap = clrmeta::BlobHeap::new();
        let index = heap.add(bytes);
        assert!(document_name(&heap.write(), index).is_err());
    }

    #[rstest]
    #[case("/", "a/b")]
    #[case("·", "a·b")]
    #[case("界", "a界b")]
    #[case("🦀", "a🦀b")]
    fn document_separators_are_utf8_characters(#[case] separator: &str, #[case] expected: &str) {
        let mut heap = clrmeta::BlobHeap::new();
        let a = heap.add(b"a");
        let b = heap.add(b"b");
        let mut name = separator.as_bytes().to_vec();
        name.extend([a as u8, b as u8]);
        let index = heap.add(&name);
        assert_eq!(document_name(&heap.write(), index).unwrap(), expected);
    }

    #[rstest]
    #[case(&[0], 0)]
    #[case(&[2], 1)]
    #[case(&[0x7e], 63)]
    #[case(&[1], -64)]
    #[case(&[0x7f], -1)]
    #[case(&[0x80, 1], -8192)]
    #[case(&[0xbf, 0xff], -1)]
    #[case(&[0xc0, 0, 0, 1], -268_435_456)]
    #[case(&[0xdf, 0xff, 0xff, 0xff], -1)]
    fn signed_deltas_use_encoded_width(#[case] bytes: &[u8], #[case] expected: i32) {
        let mut reader = Reader::new(bytes);
        assert_eq!(signed(&mut reader).unwrap(), expected);
        assert_eq!(reader.remaining(), 0);
    }

    #[test]
    fn sequence_points_preserve_hidden_ranges_and_document_changes() {
        let bytes = [
            0, 0, 0, 4, 10, 2, 2, 0, 0, 2, 0, 3, 0x7f, 2, 0, 2, 1, 1, 0x7f, 4, 0x7f,
        ];
        let points = sequence_points(&bytes, 1, 2).unwrap();
        assert_eq!(
            points,
            vec![
                SequencePoint {
                    il_offset: 0,
                    source: Some(Position {
                        document: 1,
                        line: 10,
                        column: 2
                    })
                },
                SequencePoint {
                    il_offset: 2,
                    source: None
                },
                SequencePoint {
                    il_offset: 4,
                    source: Some(Position {
                        document: 1,
                        line: 9,
                        column: 3
                    })
                },
                SequencePoint {
                    il_offset: 5,
                    source: Some(Position {
                        document: 2,
                        line: 11,
                        column: 2
                    })
                },
            ]
        );
        let pdb = PortablePdb {
            id: [0; 20],
            documents: vec!["a.cs".into(), "b.cs".into()],
            methods: vec![points],
        };
        assert_eq!(pdb.source(0x06000001, 1).unwrap().line, 10);
        assert!(pdb.source(0x06000001, 2).is_none());
        assert!(pdb.source(0x06000001, 3).is_none());
        assert_eq!(pdb.source(0x06000001, 5).unwrap().file, "b.cs");
        assert!(pdb.source(0x06000000, 0).is_none());
        assert!(pdb.source(0x06000002, 0).is_none());
        assert!(pdb.source(0x04000001, 0).is_none());
    }

    #[rstest]
    #[case(&[0x80], 1)]
    #[case(&[0, 0], 0)]
    #[case(&[0, 0, 0, 1, 1], 1)]
    #[case(&[0, 0, 0, 1, 1, 1], 2)]
    #[case(&[0, 0, 0, 1, 1, 1, 0, 0], 1)]
    fn malformed_sequence_points_diagnose(#[case] bytes: &[u8], #[case] document: u32) {
        assert!(sequence_points(bytes, document, 1).is_err());
    }
}
