use koh_link::{LinkResult, LinkedLine, LinkedSymbol};
use object::{Object as _, ObjectSection as _};

fn fixture() -> LinkResult {
    LinkResult {
        rom: vec![],
        symbols: vec![],
        lines: vec![
            LinkedLine {
                bank: 1,
                address: 0x4000,
                count: 3,
                file: "one.cs".into(),
                line: 7,
                expansion: vec![],
            },
            LinkedLine {
                bank: 2,
                address: 0x4000,
                count: 600,
                file: "two.cs".into(),
                line: 11,
                expansion: vec![],
            },
            LinkedLine {
                bank: 1,
                address: 0x4010,
                count: 2,
                file: "one.cs".into(),
                line: 9,
                expansion: vec![],
            },
        ],
    }
}

#[test]
fn dwarf_lines_keep_banks_ranges_and_unmapped_gaps() {
    let bytes = fixture().dwarf().unwrap();
    assert_eq!(&bytes[..4], b"\x7fELF");
    let file = object::File::parse(bytes.as_slice()).unwrap();
    assert_eq!(
        file.section_by_name(".note.koh").unwrap().data().unwrap(),
        b"\x04\0\0\0\x08\0\0\0\x01\0\0\0Koh\0SM83\x01\0\0\0"
    );
    let dwarf = gimli::Dwarf::load(|id| {
        let data = file
            .section_by_name(id.name())
            .map(|s| s.data().unwrap())
            .unwrap_or(&[]);
        Ok::<_, gimli::Error>(gimli::EndianSlice::new(data, gimli::LittleEndian))
    })
    .unwrap();
    let mut units = dwarf.units();
    let header = units.next().unwrap().unwrap();
    assert_eq!(header.version(), 5);
    assert_eq!(header.address_size(), 4);
    let unit = dwarf.unit(header).unwrap();
    let mut rows = unit.line_program.clone().unwrap().rows();
    let mut actual = Vec::new();
    while let Some((header, row)) = rows.next_row().unwrap() {
        let path = header.file(row.file_index()).unwrap().path_name();
        let path = dwarf.attr_string(&unit, path).unwrap();
        actual.push((
            row.address(),
            row.end_sequence(),
            row.line().map(|l| l.get()),
            String::from_utf8(path.to_vec().to_vec()).unwrap(),
        ));
    }
    assert_eq!(
        actual.iter().map(|r| (r.0, r.1)).collect::<Vec<_>>(),
        vec![
            (0x14000, false),
            (0x14003, true),
            (0x24000, false),
            (0x24258, true),
            (0x14010, false),
            (0x14012, true),
        ]
    );
    assert_eq!((actual[0].2, actual[0].3.as_str()), (Some(7), "one.cs"));
    assert_eq!((actual[2].2, actual[2].3.as_str()), (Some(11), "two.cs"));
    assert_eq!((actual[4].2, actual[4].3.as_str()), (Some(9), "one.cs"));
}

#[test]
fn dwarf_symbols_preserve_signed_constants_and_banked_labels() {
    let mut input = fixture();
    input.symbols = vec![
        LinkedSymbol {
            name: "negative".into(),
            value: -123,
            bank: None,
            kind: 1,
            source: None,
        },
        LinkedSymbol {
            name: "entry".into(),
            value: 0x4000,
            bank: Some(256),
            kind: 0,
            source: None,
        },
        LinkedSymbol {
            name: "buffer".into(),
            value: 0xc000,
            bank: Some(1),
            kind: 2,
            source: None,
        },
    ];
    let bytes = input.dwarf().unwrap();
    let file = object::File::parse(bytes.as_slice()).unwrap();
    let dwarf = gimli::Dwarf::load(|id| {
        let data = file
            .section_by_name(id.name())
            .map(|s| s.data().unwrap())
            .unwrap_or(&[]);
        Ok::<_, gimli::Error>(gimli::EndianSlice::new(data, gimli::LittleEndian))
    })
    .unwrap();
    let unit = dwarf.unit(dwarf.units().next().unwrap().unwrap()).unwrap();
    let mut entries = unit.entries();
    let mut found = Vec::new();
    while let Some(entry) = entries.next_dfs().unwrap() {
        if entry.tag() == gimli::DW_TAG_compile_unit {
            continue;
        }
        let name = dwarf
            .attr_string(&unit, entry.attr_value(gimli::DW_AT_name).unwrap())
            .unwrap();
        found.push(String::from_utf8(name.to_vec().to_vec()).unwrap());
        match entry.tag() {
            gimli::DW_TAG_constant => assert_eq!(
                entry
                    .attr_value(gimli::DW_AT_const_value)
                    .unwrap()
                    .sdata_value(),
                Some(-123)
            ),
            gimli::DW_TAG_label => assert_eq!(
                entry.attr_value(gimli::DW_AT_low_pc),
                Some(gimli::AttributeValue::Addr(0x1004000))
            ),
            gimli::DW_TAG_variable => assert!(matches!(
                entry.attr_value(gimli::DW_AT_location),
                Some(gimli::AttributeValue::Exprloc(_))
            )),
            tag => panic!("unexpected tag {tag:?}"),
        }
    }
    assert_eq!(found, ["negative", "entry", "buffer"]);
}

#[test]
fn dwarf_rejects_invalid_addresses_names_and_unimplemented_expansions() {
    let mut input = fixture();
    input.lines[0].address = 0xffff;
    assert!(input.dwarf().unwrap_err().contains("boundary"));
    input.lines[0].address = 0x4000;
    input.lines[0].file = "bad\0name".into();
    assert!(input.dwarf().unwrap_err().contains("NUL"));
    input.lines[0].file = "one.cs".into();
    input.lines[0].expansion.push(koh_objects::SourceLocation {
        file: "macro.asm".into(),
        line: 1,
        column: 1,
    });
    assert!(input.dwarf().unwrap_err().contains("expansion"));
}
