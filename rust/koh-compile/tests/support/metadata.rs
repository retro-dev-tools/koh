pub fn table_row(
    bytes: &[u8],
    table: clrmeta::TableId,
    index: usize,
) -> (usize, clrmeta::tables::TableContext) {
    let file = object::read::pe::PeFile32::parse(bytes).unwrap();
    let sections = file.section_table();
    let cli = file
        .data_directory(object::pe::IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR)
        .unwrap()
        .data(bytes, &sections)
        .unwrap();
    let rva = u32::from_le_bytes(cli[8..12].try_into().unwrap());
    let (start, _) = sections.pe_file_range_at(rva).unwrap();
    let root = clrmeta::MetadataRoot::parse(&bytes[start as usize..]).unwrap();
    let stream = root.tables_stream().unwrap();
    let start = start as usize + stream.offset as usize;
    let mut reader = clrmeta::reader::Reader::new(&bytes[start..start + stream.size as usize]);
    let header = clrmeta::TablesHeader::parse(&mut reader, false).unwrap();
    let context = header.context();
    let mut offset = start + reader.position();
    for (current, count) in header.tables() {
        if count == 0 {
            continue;
        }
        let size = if current == clrmeta::TableId::Module {
            2 + context.string_index_size() + 3 * context.guid_index_size()
        } else {
            context.row_size(current)
        };
        assert!(size != 0, "Test mutation does not support {current:?}");
        if current == table {
            assert!(index < count as usize);
            return (offset + index * size, context);
        }
        offset += count as usize * size;
    }
    panic!("Missing fixture table {table:?}")
}
