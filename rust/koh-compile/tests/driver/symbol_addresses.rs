use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{
    compile,
    resolve::{Definition, Resolver},
    sm83,
};
use koh_objects::{
    PatchSemantics, PatchWidth, RelocationExpr, SymbolDefinition, read_elf, write_elf,
};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn symbols_identify_emitted_instructions_entry_and_mutable_storage(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "SymbolAddresses");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/SymbolAddresses.pdb")),
    )
    .unwrap();
    let helper = sm83::symbol(compile::entry(&resolver, Some("Program::Helper")).unwrap());
    let data = resolver.assembly(0).unwrap().metadata();
    let globals = [("Counter", 1u8), ("Evidence", 2), ("Zero", 0)].map(|(name, value)| {
        let index = data
            .fields
            .iter()
            .position(|field| data.strings.get(field.name).unwrap() == name)
            .unwrap();
        (
            sm83::static_symbol(Definition {
                assembly: 0,
                token: 0x04000001 + index as u32,
            }),
            value,
        )
    });
    for (entry, ram, startup, origin) in [
        ("First", 0xc020, 0x200, 0x4100),
        ("Second", 0xc040, 0x300, 0x4500),
    ] {
        let selected = format!("Program::{entry}");
        let main = sm83::symbol(compile::entry(&resolver, Some(&selected)).unwrap());
        for debug in [false, true] {
            let mut object = if debug {
                compile::object_with_debug(&resolver, Some(&selected), &[Some(&pdb), None])
            } else {
                compile::object(&resolver, Some(&selected))
            }
            .unwrap();
            let label = |name: &str| {
                object
                    .symbols
                    .iter()
                    .find_map(|symbol| {
                        if symbol.name != name {
                            return None;
                        }
                        if let SymbolDefinition::Label { section, offset } = symbol.definition {
                            Some((section, offset))
                        } else {
                            None
                        }
                    })
                    .unwrap()
            };
            let code_labels = [
                (main.clone(), label(&main), 1, origin),
                (helper.clone(), label(&helper), 2, origin + 0x200),
                ("__koh_start".into(), label("__koh_start"), 0, startup),
            ];
            let storage = label("__koh_statics");
            let field_labels = globals
                .iter()
                .map(|(name, value)| (name.clone(), label(name), *value))
                .collect::<Vec<_>>();
            assert_eq!(object.sections[storage.0.0].kind, 4);
            assert!(object.sections[storage.0.0].data.is_empty());
            assert_eq!(object.sections[storage.0.0].size, 3);
            object.sections[storage.0.0].address = Some(ram);
            let mut expected = [0; 4];
            for (_, (section, offset), value) in &field_labels {
                assert_eq!(*section, storage.0);
                assert!(*offset < 3);
                expected[*offset as usize] = *value;
            }
            for (_, (section, offset), bank, address) in &code_labels {
                assert!((*offset as usize) < object.sections[section.0].data.len());
                object.sections[section.0].bank = Some(*bank);
                object.sections[section.0].address = Some(*address);
            }
            let bytes = write_elf(&object).unwrap();
            let object = read_elf(&bytes).unwrap();
            assert_eq!(write_elf(&object).unwrap(), bytes);
            let linked = koh_link::link(
                &[object.clone()],
                &koh_link::LinkOptions {
                    cgb: if color {
                        koh_link::CgbMode::Compatible
                    } else {
                        koh_link::CgbMode::Preserve
                    },
                    ..Default::default()
                },
            )
            .unwrap();
            let symbol = |name: &str| {
                linked
                    .symbols
                    .iter()
                    .find(|symbol| symbol.name == name)
                    .unwrap()
            };
            let physical = |bank: u32, address: u32| {
                if address < 0x4000 {
                    address as usize
                } else {
                    bank as usize * 0x4000 + (address as usize - 0x4000)
                }
            };
            for (name, (section, offset), bank, address) in &code_labels {
                let actual = symbol(name);
                assert_eq!(actual.value, address + *offset as i32, "{name}");
                assert_eq!(actual.bank, Some(*bank as u32), "{name}");
                let section = &object.sections[section.0];
                let mut checked = 0;
                for index in *offset as usize..section.data.len() {
                    if section.patches.iter().any(|patch| {
                        (patch.offset..patch.offset + patch.width.bytes()).contains(&index)
                    }) {
                        continue;
                    }
                    let rom = physical(*bank as u32, *address as u32) + index;
                    assert_eq!(linked.rom[rom], section.data[index], "{name}+{index:x}");
                    checked += 1;
                }
                assert!(checked > 0);
            }
            let (main_section, _) = code_labels[0].1;
            let calls = object.sections[main_section.0].patches.iter().filter(|patch| {
                matches!(patch.expression, RelocationExpr::Symbol(id) if object.symbols[id.0].name == helper)
            }).collect::<Vec<_>>();
            assert!(!calls.is_empty(), "Entry must reference Helper");
            for call in calls {
                assert_eq!(call.width, PatchWidth::Word);
                assert_eq!(call.semantics, PatchSemantics::Absolute);
                let offset = physical(1, origin as u32) + call.offset;
                assert_eq!(
                    u16::from_le_bytes(linked.rom[offset..offset + 2].try_into().unwrap()) as i32,
                    symbol(&helper).value
                );
            }
            assert_eq!(&linked.rom[0x100..0x102], &[0, 0xc3]);
            assert_eq!(
                u16::from_le_bytes(linked.rom[0x102..0x104].try_into().unwrap()) as i32,
                symbol("__koh_start").value
            );
            assert_ne!(symbol(&main).value, symbol("__koh_start").value);
            assert_eq!(symbol("__koh_statics").value, ram);
            for (name, (_, offset), _) in &field_labels {
                assert_eq!(symbol(name).value, ram + *offset as i32, "{name}");
            }
            assert!(linked.dwarf().unwrap().starts_with(b"\x7fELF"));
            let report = execute_mode(
                host,
                directory.path(),
                &linked,
                "__koh_exit",
                "--native-heap",
            );
            let fields: Vec<_> = report.split_whitespace().collect();
            assert_eq!(
                &fields[..4],
                &[
                    "00",
                    &format!("{:08X}", u32::from_le_bytes(expected)),
                    "D000",
                    "OK"
                ]
            );
            super::assert_heap_snapshot(fields[4], &[0x5a; 4096], entry);
            if color {
                super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
            }
        }
    }
}
