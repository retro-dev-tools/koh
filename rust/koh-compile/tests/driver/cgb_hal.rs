use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use koh_objects::{read_elf, write_elf};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn referenced_cgb_hal_preserves_banks_and_palette_bytes(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "CgbHal");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "Main",
        "Bank",
        "BankRoundTrip",
        "Palette0",
        "Palette23",
        "PaletteAll",
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
        let object = read_elf(&write_elf(&object).unwrap()).unwrap();
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-cgb-hal",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        let result = if entry == "Main" {
            u32::from(color)
        } else {
            42
        };
        assert_eq!(
            &fields[..4],
            &["00", &format!("{result:08X}"), "D000", "OK"],
            "{entry}"
        );
        assert_eq!(fields[4], "5A".repeat(4096), "{entry} heap");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} bank2");
        }
        let offset = if color { 6 } else { 5 };
        let state: Vec<_> = fields[offset].split(':').collect();
        let bank = color && matches!(entry, "Main" | "Bank" | "BankRoundTrip");
        assert_eq!(state[0], if bank { "01" } else { "00" }, "{entry}");
        if color {
            assert_eq!(state[1], if bank { "FF" } else { "FE" }, "{entry}");
        }
        assert_eq!(
            state[3],
            if color && entry == "Main" { "1" } else { "0" },
            "{entry}"
        );
        let mut palette = [0x3du8; 64];
        let mut writes = Vec::new();
        let mut palette_writes = Vec::new();
        if color && matches!(entry, "Main" | "Bank" | "BankRoundTrip") {
            writes.push((0xff4fu16, 1u8));
        }
        if color && entry == "BankRoundTrip" {
            writes.extend([(0xff4f, 0), (0xff4f, 1)]);
        }
        let colors: Vec<(usize, u16)> = match entry {
            "Main" | "Palette0" => vec![(0, 0x1234)],
            "Palette23" => vec![(11, 0x7fff)],
            "PaletteAll" => (0..32).map(|i| (i, i as u16 * 0x421)).collect(),
            _ => vec![],
        };
        if color {
            for (index, value) in colors {
                let [low, high] = value.to_le_bytes();
                palette[index * 2..index * 2 + 2].copy_from_slice(&[low, high]);
                palette_writes.extend([((index * 2) as u8, low), ((index * 2 + 1) as u8, high)]);
            }
        }
        let index = if !color {
            "-"
        } else {
            match entry {
                "Main" | "Palette0" => "01",
                "Palette23" => "17",
                "PaletteAll" => "3F",
                _ => "2A",
            }
        };
        assert_eq!(state[2], index, "{entry} palette index");
        let palette: String = palette.iter().map(|v| format!("{v:02X}")).collect();
        assert_eq!(
            fields[offset + 1],
            if color { palette.as_str() } else { "-" },
            "{entry} BG palette"
        );
        assert_eq!(
            fields[offset + 2],
            if color { "6C".repeat(64) } else { "-".into() },
            "{entry} OBJ palette"
        );
        let video = match (entry, color) {
            ("Bank", true) => "A799",
            ("Bank", false) => "99",
            ("BankRoundTrip", true) => "5599",
            ("BankRoundTrip", false) => "55",
            (_, true) => "A7B8",
            (_, false) => "A7",
        };
        assert_eq!(fields[offset + 3], video, "{entry} VRAM banks");
        let observed: Vec<_> = if fields[offset + 4] == "-" {
            vec![]
        } else {
            fields[offset + 4]
                .split(',')
                .map(|write| {
                    let parts: Vec<_> = write.split(':').collect();
                    assert_eq!(
                        parts[2], "0",
                        "{entry} palette/bank write outside LCD-off precondition"
                    );
                    (
                        u16::from_str_radix(parts[0], 16).unwrap(),
                        u8::from_str_radix(parts[1], 16).unwrap(),
                    )
                })
                .collect()
        };
        let mut observed_banks = Vec::new();
        let mut observed_palette = Vec::new();
        let mut selected = 0x2au8;
        for (address, value) in observed {
            assert!(color, "{entry} CGB-only write on DMG");
            match address {
                0xff4f => observed_banks.push((address, value)),
                0xff68 => selected = value & 0xbf,
                0xff69 => {
                    observed_palette.push((selected & 63, value));
                    if selected & 128 != 0 {
                        selected = 128 | (selected.wrapping_add(1) & 63);
                    }
                }
                _ => panic!("{entry} unexpected MMIO port {address:04X}"),
            }
        }
        assert_eq!(observed_banks, writes, "{entry} bank writes");
        assert_eq!(
            observed_palette, palette_writes,
            "{entry} palette byte writes"
        );
    }
}
