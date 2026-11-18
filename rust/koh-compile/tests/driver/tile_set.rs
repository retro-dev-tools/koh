use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn tile_subranges_rows_and_clear_preserve_exact_vram(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "TileSetContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "Subrange",
        "AlignedSubrange",
        "OnSubrange",
        "OnAlignedSubrange",
        "EmptySubrange",
        "Rows",
        "BankOneRows",
    ] {
        let rows = matches!(entry, "Rows" | "BankOneRows");
        let bank_one = color && entry == "BankOneRows";
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
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
        eprintln!("Execute {entry}");
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-canvas",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut vram = vec![0xa7; 8192];
        vram[0x1800..].fill(0);
        let mut other = vec![0xb8; 8192];
        let target = if bank_one { &mut other } else { &mut vram };
        if rows {
            for tile in [9, 16, 255] {
                target[tile * 16..tile * 16 + 16].fill(0);
                target[tile * 16 + 6..tile * 16 + 8].copy_from_slice(&[0x12, 0x34]);
            }
            target[255 * 16 + 14..255 * 16 + 16].copy_from_slice(&[0x56, 0x78]);
        } else if entry != "EmptySubrange" {
            target[160..176].fill(0x22);
            target[176..192].fill(0x33);
        }
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, entry);
        if color {
            assert_heap_snapshot(fields[7], &other, entry);
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
        let oam = if color { 9 } else { 7 };
        assert_heap_snapshot(fields[oam], &[0; 160], entry);
        assert_eq!(
            fields[oam + 1],
            if entry.starts_with("On") {
                "91E4E4E4"
            } else {
                "11E4E4E4"
            },
            "{entry}"
        );
        let mut packet = [0x5a; 256];
        if rows {
            packet[0] = u8::from(bank_one);
        }
        assert_heap_snapshot(&fields[4][0xf00 * 2..], &packet, entry);
        let (_, after) = fields[oam - 1].split_once(':').unwrap();
        assert_eq!(&after[8..], "000000", "{entry}: completed DMA");
        let transfers: u8 = fields[oam + 2].parse().unwrap();
        if color && matches!(entry, "AlignedSubrange" | "OnAlignedSubrange") {
            assert_eq!(transfers, 1, "{entry}: aligned GDMA request");
        } else if !color || rows || entry == "EmptySubrange" {
            assert_eq!(transfers, 0, "{entry}: no GDMA request");
        } else {
            assert!(transfers <= 1, "{entry}: single subrange transfer");
        }
    }
}
