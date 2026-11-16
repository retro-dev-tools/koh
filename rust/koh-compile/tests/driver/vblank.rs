use super::{Host, compile, dotnet, gameboy_images, host, support};
use koh_compile::{ir::Op, resolve::Resolver, sm83};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn bounded_copy_preserves_exact_prefix_at_every_scanline(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "VBlankCopy");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for rejected in ["RejectReturn", "RejectAddress"] {
        let error = compile::object(&resolver, Some(&format!("Program::{rejected}"))).unwrap_err();
        assert!(
            error.contains("Invalid Koh vblankcopy intrinsic contract") && error.contains("IL_"),
            "{error}"
        );
    }
    let phase_host = support::build_fixture_host("VBlankHost");
    let data: Vec<u8> = (0..512).map(|index| (index ^ (index >> 4)) as u8).collect();
    for (method, count) in [
        ("Zero", 0),
        ("One", 1),
        ("Seventeen", 17),
        ("Main", 257),
        ("Large", 17),
    ] {
        let selected = format!("Program::{method}");
        let entry = compile::entry(&resolver, Some(&selected)).unwrap();
        let functions = compile::reachable(&resolver, entry).unwrap();
        let root = functions
            .iter()
            .find(|function| function.identity == entry)
            .unwrap();
        if method == "Large" {
            assert!(root.locals.iter().any(
                |ty| matches!(ty, koh_compile::ir::Type::Struct(value) if value.bytes() == 200)
            ));
        }
        let operation = root
            .blocks
            .iter()
            .flat_map(|block| &block.instructions)
            .find(|instruction| matches!(instruction.op, Op::VBlankCopy { .. }))
            .unwrap();
        let prefix = format!("{}.IL_{:04x}.", sm83::symbol(entry), operation.il_offset);
        let mut object = compile::object(&resolver, Some(&selected)).unwrap();
        object.sections.extend([
            koh_objects::Section {
                name: ".test.vblank.source".into(),
                kind: 0,
                address: Some(0x2000),
                size: data.len() as u32,
                data: data.clone(),
                ..Default::default()
            },
            koh_objects::Section {
                name: ".test.vblank.nop".into(),
                kind: 0,
                address: Some(0x3000),
                size: 3,
                data: vec![0, 0xfb, 0],
                ..Default::default()
            },
        ]);
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
                "large intrinsic must execute the DE-clobbering large-offset address path"
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
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        let report = String::from_utf8(output.stdout).unwrap();
        assert_eq!(report.lines().count(), 308);
        let mut partial = false;
        for (index, line) in report.lines().enumerate() {
            let fields: Vec<_> = line.split_whitespace().collect();
            assert_eq!(fields.len(), 11);
            let number = |index: usize| fields[index].parse::<usize>().unwrap();
            let copied = number(2);
            assert_eq!(number(9), copied, "source reads match copied prefix");
            assert_eq!(
                number(10),
                usize::from(count == 0),
                "nonempty copy disables interrupts; empty copy preserves IME"
            );
            assert_eq!(number(0), index / 2);
            assert_eq!(number(1), if index % 2 == 0 { 0 } else { 452 });
            assert_eq!(number(3), 0xd000, "{method} phase{index}: stack");
            assert_eq!(number(4), if color { 2 } else { 7 });
            assert!(
                number(6) < 2 * 70224,
                "{method} phase{index}: bounded duration"
            );
            if count == 0 {
                assert_eq!(copied, 0);
                assert_eq!(number(5), 0, "zero count must not poll LY");
            } else {
                assert!(
                    (1..=count).contains(&copied),
                    "{method} phase{index}: copied={copied}"
                );
                assert!(number(5) > 0);
                if count == 1 {
                    assert_eq!(copied, count);
                }
                partial |= copied < count;
            }
            let vram: Vec<u8> = fields[7]
                .as_bytes()
                .chunks_exact(2)
                .map(|pair| u8::from_str_radix(std::str::from_utf8(pair).unwrap(), 16).unwrap())
                .collect();
            let mut expected = vec![0xa7; 16384];
            expected[1..1 + copied].copy_from_slice(&data[..copied]);
            assert!(
                vram == expected,
                "{method} phase{index}: exact prefix/guards, lengths {} vs {}, first mismatch {:?}",
                vram.len(),
                expected.len(),
                vram.iter()
                    .zip(&expected)
                    .position(|(actual, expected)| actual != expected)
            );
            let writes: Vec<Vec<usize>> = if fields[8] == "-" {
                vec![]
            } else {
                fields[8]
                    .split(',')
                    .map(|cell| {
                        cell.split(':')
                            .map(|value| value.parse().unwrap())
                            .collect()
                    })
                    .collect()
            };
            assert_eq!(writes.len(), copied);
            for (offset, write) in writes.iter().enumerate() {
                assert_eq!(write.len(), 4);
                assert_eq!(write[0], 0x8001 + offset);
                assert_eq!(write[1], usize::from(data[offset]));
                assert!(
                    (144..152).contains(&write[2]),
                    "{method} phase{index}: write outside reserved VBlank: {write:?}"
                );
                assert!(write[3] < 456);
            }
        }
        if count == 257 {
            assert!(partial, "large copies must expose partial progress");
        }
    }
}
