use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest::fixture]
#[once]
fn managed() -> Host {
    super::support::build_fixture_host("ManagedHost")
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn palette_guard_observes_actual_unsafe_rom_writes(host: &Host, #[case] configuration: &str) {
    let (directory, images) = gameboy_images(host, configuration, "SpriteContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(&resolver, Some("Program::UnsafePalette")).unwrap();
    let linked = koh_link::link(
        &[object],
        &koh_link::LinkOptions {
            cgb: koh_link::CgbMode::Compatible,
            ..Default::default()
        },
    )
    .unwrap();
    let stop = linked
        .symbols
        .iter()
        .find(|s| s.name == "__koh_exit")
        .unwrap();
    let rom = directory.path().join("unsafe palette.gb");
    std::fs::write(&rom, &linked.rom).unwrap();
    let result = host
        .command()
        .args([
            "--native-sprites",
            &format!("{:x}:{:x}", stop.bank.unwrap_or(0), stop.value),
        ])
        .arg(&rom)
        .output()
        .unwrap();
    assert!(
        !result.status.success(),
        "unsafe ROM must trip the timing guard"
    );
    assert!(
        String::from_utf8_lossy(&result.stderr).contains("Mode-3 OBJ palette write"),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn desktop_dma_library_copies_the_actual_page(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
) {
    let (directory, _) = gameboy_images(host, configuration, "SpriteContracts");
    let result = managed
        .command()
        .arg(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/SpriteContracts.dll")),
        )
        .arg("HostDma")
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let expected: String = (1..=160u8).map(|v| format!("{v:02X}")).collect();
    assert_eq!(String::from_utf8(result.stdout).unwrap().trim(), expected);
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn referenced_sprites_flush_real_oam_and_palette_memory(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "SpriteContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "Mutate",
        "HideAll",
        "Move",
        "NoMutation",
        "CleanFrame",
        "Tile",
        "ColorPalette",
        "Registers",
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|e| panic!("{entry}: {e}"));
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
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-sprites",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut packet = [0x5au8; 256];
        if entry == "CleanFrame" {
            packet[0] = 2;
        }
        if entry == "Registers" && color {
            packet[..2].copy_from_slice(&[0, 0x2c]);
        }
        assert_heap_snapshot(&fields[4][0xf00 * 2..], &packet, entry);
        let offset = if color { 7 } else { 5 };
        let mut oam = [0u8; 160];
        match entry {
            "Mutate" => oam[..12].copy_from_slice(&[48, 24, 5, 0x20, 252, 4, 9, 0, 0, 58, 3, 0]),
            "Move" => oam[12..16].copy_from_slice(&[46, 28, 42, 0x80]),
            "Tile" => oam[..4].copy_from_slice(&[16, 8, 255, 0x50]),
            "ColorPalette" => {
                oam[..4].copy_from_slice(&[46, 28, 5, 0]);
                oam[156..].copy_from_slice(&[96, 108, 17, 0x47]);
            }
            _ => {}
        }
        assert_heap_snapshot(fields[offset + 2], &oam, entry);
        let dma: Vec<_> = fields[offset + 4].split(':').collect();
        assert_eq!(dma[0], "1", "{entry}: exactly one DMA");
        assert!(
            dma[1].parse::<u64>().unwrap() > 0,
            "{entry}: actual DMA instructions"
        );
        if color {
            let mut palette = [0x6cu8; 64];
            palette[..8].copy_from_slice(&[0xff, 0x7f, 0xb5, 0x56, 0x4a, 0x29, 0, 0]);
            if entry == "ColorPalette" {
                palette[..16].copy_from_slice(&[
                    0x1f, 0, 0xe0, 3, 0, 0x7c, 0x34, 0x12, 0xff, 0x7f, 0x21, 4, 0x4a, 0x29, 0, 0,
                ]);
                palette[56..].copy_from_slice(&[1, 0, 2, 0, 3, 0, 4, 0]);
            }
            if entry == "Registers" {
                palette[0] = 0x2c;
            }
            assert_heap_snapshot(fields[offset + 5], &palette, entry);
        } else {
            assert_eq!(fields[offset + 5], "-");
        }
        assert_eq!(
            &fields[offset + 6][2..],
            if entry == "Registers" || (entry == "ColorPalette" && !color) {
                "E41B"
            } else {
                "E4E4"
            },
            "{entry}"
        );
        if entry == "Registers" {
            assert_eq!(&fields[offset + 6][..2], "5A");
            if color {
                assert_eq!(fields[offset + 7], "00:2C");
            }
        }
    }
}
