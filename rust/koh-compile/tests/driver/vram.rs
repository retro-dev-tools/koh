use super::{Host, compile, dotnet, gameboy_images, host, support};
use koh_compile::{ir::Op, resolve::Resolver, sm83};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn vram_write_is_single_bounded_and_safe_at_each_scanline(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "VramWrite");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for name in ["RejectReturn", "RejectAddress", "RejectValue"] {
        let error = compile::object(&resolver, Some(&format!("Program::{name}"))).unwrap_err();
        assert!(
            error.contains("Invalid Koh vramwrite intrinsic contract") && error.contains("IL_"),
            "{error}"
        );
    }
    let phase_host = support::build_fixture_host("VBlankHost");
    for (method, address, value) in [
        ("Main", 0x8001, 0x6d),
        ("End", 0x9fff, 0xfe),
        ("Large", 0x8001, 0x36),
        ("BankOne", 0x8001, 0x6d),
    ] {
        for lcd_off in [false, true] {
            let selected = format!("Program::{method}");
            let entry = compile::entry(&resolver, Some(&selected)).unwrap();
            let functions = compile::reachable(&resolver, entry).unwrap();
            let root = functions
                .iter()
                .find(|function| function.identity == entry)
                .unwrap();
            let operation = root
                .blocks
                .iter()
                .flat_map(|block| &block.instructions)
                .find(|instruction| matches!(instruction.op, Op::VramWrite { .. }))
                .unwrap();
            let prefix = format!("{}.IL_{:04x}.", sm83::symbol(entry), operation.il_offset);
            let mut object = compile::object(&resolver, Some(&selected)).unwrap();
            object.sections.push(koh_objects::Section {
                name: ".test.vram.nop".into(),
                kind: 0,
                address: Some(0x3000),
                size: 3,
                data: vec![0, 0xfb, 0],
                ..Default::default()
            });
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
            let site_symbol = linked
                .symbols
                .iter()
                .filter(|symbol| symbol.name.starts_with(&prefix))
                .min_by_key(|symbol| symbol.value)
                .unwrap();
            let site = site_symbol.value;
            let end = linked
                .symbols
                .iter()
                .filter(|symbol| {
                    symbol
                        .name
                        .starts_with(&format!("{}.IL_", sm83::symbol(entry)))
                        && symbol.value > site
                })
                .min_by_key(|symbol| symbol.value)
                .unwrap()
                .value;
            if method == "Large" {
                let physical = |address: i32| {
                    if address < 0x4000 {
                        address as usize
                    } else {
                        site_symbol.bank.unwrap() as usize * 0x4000 + address as usize - 0x4000
                    }
                };
                assert!(
                    linked.rom[physical(site)..physical(end)]
                        .windows(6)
                        .any(|bytes| bytes[..3] == [0xf8, 0, 0x11]
                            && bytes[5] == 0x19
                            && u16::from_le_bytes([bytes[3], bytes[4]]) > 127),
                    "large write must exercise extended stack addressing"
                );
            }
            let stop = linked
                .symbols
                .iter()
                .find(|symbol| symbol.name == "__koh_exit")
                .unwrap()
                .value;
            let rom = directory.path().join(format!("{method}.gb"));
            std::fs::write(&rom, &linked.rom).unwrap();
            let output = dotnet()
                .arg(&phase_host.dll)
                .arg(&rom)
                .arg(format!("{stop:x}"))
                .arg(format!("{site:x}"))
                .arg(format!("{end:x}"))
                .arg(if lcd_off { "off" } else { "on" })
                .output()
                .unwrap();
            assert!(
                output.status.success(),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
            let report = String::from_utf8(output.stdout).unwrap();
            assert_eq!(report.lines().count(), 308);
            for (index, line) in report.lines().enumerate() {
                let fields: Vec<_> = line.split_whitespace().collect();
                assert_eq!(fields.len(), 11, "{line}");
                let number = |index: usize| fields[index].parse::<usize>().unwrap();
                assert_eq!(number(0), index / 2);
                assert_eq!(number(1), if index % 2 == 0 { 0 } else { 452 });
                assert_eq!(number(2), 42, "{method}: live values");
                assert_eq!(number(3), 0xd000, "{method}: SP");
                assert_eq!(number(4), if color { 2 } else { 7 }, "{method}: SVBK");
                assert_eq!(number(10), 0, "{method}: IME after write");
                assert!(number(6) < 2 * 70224, "{method}: bounded time");
                let writes: Vec<Vec<usize>> = fields[8]
                    .split(',')
                    .map(|write| {
                        write
                            .split(':')
                            .map(|value| value.parse().unwrap())
                            .collect()
                    })
                    .collect();
                assert_eq!(writes.len(), 1, "{method}: exact store count");
                assert_eq!(writes[0][..2], [address, value]);
                let ly = writes[0][2];
                let dot = writes[0][3];
                assert!(ly < 154 && dot < 456);
                assert!(
                    ly >= 144 || dot < 80 || dot >= 252,
                    "{method} phase{index}: drawing write {writes:?}"
                );
                let vram: Vec<_> = fields[7]
                    .as_bytes()
                    .chunks_exact(2)
                    .map(|pair| u8::from_str_radix(std::str::from_utf8(pair).unwrap(), 16).unwrap())
                    .collect();
                let mut expected = vec![0xa7; 16384];
                expected[address - 0x8000
                    + if color && method == "BankOne" {
                        8192
                    } else {
                        0
                    }] = value as u8;
                assert!(vram == expected, "{method} phase{index}: VRAM extents/bank");
            }
        }
    }
}
