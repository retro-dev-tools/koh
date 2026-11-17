use super::{build, game, report, support};
use koh_compile::{resolve::Resolver, sm83};
use rstest::rstest;
use std::path::Path;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn original_2048_sdk_boots_and_plays(#[case] configuration: &str, #[case] cgb: bool) {
    let directory = tempfile::Builder::new()
        .prefix("koh original 2048 ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let project = directory.path().join("Game.csproj");
    std::fs::write(
        &project,
        r#"<Project>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.props" />
<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<ItemGroup><Compile Include="$(KohRoot)samples/csharp/gb-2048-cs/*.cs" /></ItemGroup>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.targets" />
</Project>"#,
    )
    .unwrap();
    let rom = directory.path().join("original.gb");
    let object = directory.path().join("original.o");
    let properties = [
        format!("-p:KohRoot={}/", root.display()),
        format!("-p:KohRomPath={}", rom.display()),
        format!("-p:KohObjectPath={}", object.display()),
        format!("-p:KohCgbCompatible={cgb}"),
        format!(
            "-p:KohCompilerExecutable={}",
            env!("CARGO_BIN_EXE_koh-compile")
        ),
    ];
    let built = build(&project, configuration, &properties);
    assert!(built.status.success(), "{}", report(&built));
    let linked = koh_link::link(
        &[koh_objects::read_elf(&std::fs::read(object).unwrap()).unwrap()],
        &koh_link::LinkOptions {
            cgb: if cgb {
                koh_link::CgbMode::Compatible
            } else {
                koh_link::CgbMode::Preserve
            },
            ..Default::default()
        },
    )
    .unwrap();
    assert_eq!(std::fs::read(&rom).unwrap(), linked.rom);
    let logo = "CEED6666CC0D000B03730083000C000D0008111F8889000EDCCC6EE6DDDDD999BBBB67636E0EECCCDDDC999FBBB9333E";
    let logo: Vec<_> = (0..logo.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&logo[i..i + 2], 16).unwrap())
        .collect();
    assert_eq!(&linked.rom[0x104..0x134], logo);
    assert_eq!(
        linked.rom[0x14d],
        linked.rom[0x134..0x14d]
            .iter()
            .fold(0u8, |sum, byte| sum.wrapping_sub(*byte).wrapping_sub(1))
    );
    let checksum = linked
        .rom
        .iter()
        .enumerate()
        .filter(|(i, _)| !matches!(i, 0x14e | 0x14f))
        .fold(0u16, |sum, (_, byte)| sum.wrapping_add(u16::from(*byte)));
    assert_eq!(
        u16::from_be_bytes(linked.rom[0x14e..0x150].try_into().unwrap()),
        checksum
    );
    assert!(rom.with_extension("kdbg").is_file());
    let symbol = |name: &str| {
        linked
            .symbols
            .iter()
            .find(|s| s.name == name)
            .unwrap()
            .value as usize
    };
    let bin = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let assembly = std::fs::read(bin.join("Game.dll")).unwrap();
    let library = std::fs::read(bin.join("Koh.GameBoy.dll")).unwrap();
    let resolver = Resolver::new(&[&assembly, &library]).unwrap();
    let board = game::definition(&resolver, 0, "Board");
    let address = |name| symbol(&sm83::static_symbol(game::field(&resolver, board, name)));
    let directions = ["Left", "Up", "Right", "Down"];
    let mut actions = String::from("1600:dump:initial\n");
    for (index, direction) in directions.iter().enumerate() {
        let frame = 1700 + index * 400;
        actions.push_str(&format!(
            "{frame}:press:{direction}\n{}:release:{direction}\n{}:dump:move{index}\n",
            frame + 10,
            frame + 300
        ));
    }
    let script = directory.path().join("input.txt");
    std::fs::write(&script, actions).unwrap();
    let host = support::build_fixture_host("GameHost");
    let result = support::dotnet()
        .arg(&host.dll)
        .arg(&rom)
        .arg(format!("{:x}", symbol("__koh_abort")))
        .arg(script)
        .output()
        .unwrap();
    assert!(result.status.success(), "{}", report(&result));
    let output = String::from_utf8(result.stdout).unwrap();
    let fields: Vec<_> = output.split_whitespace().collect();
    assert_eq!(fields.len(), 4, "{output}");
    assert!(fields[0].parse::<usize>().unwrap() < 600);
    assert_eq!(
        &fields[1..],
        &["3201", "0", if cgb { "Cgb" } else { "Dmg" }]
    );
    let mut previous = [0; 16];
    let mut previous_score = 0;
    for (step, label) in std::iter::once("initial".to_owned())
        .chain((0..4).map(|i| format!("move{i}")))
        .enumerate()
    {
        let path = directory.path().join(format!("original.gb.{label}.dump"));
        let memory = std::fs::read(path.with_extension("wram")).unwrap();
        let pointer = game::word(&memory, address("cells"));
        assert_eq!(game::word(&memory, pointer), 16);
        let cells: [u8; 16] = memory[pointer - 0xc000 + 2..pointer - 0xc000 + 18]
            .try_into()
            .unwrap();
        let score = game::word(&memory, address("Score"));
        if step == 0 {
            assert_eq!(score, 0);
            assert_eq!(cells.iter().filter(|&&v| v != 0).count(), 2);
            assert!(cells.iter().all(|&v| v <= 2));
        } else {
            let (expected, gained) = game::slide(previous, directions[step - 1]);
            assert_eq!(score, previous_score + gained);
            let changed: Vec<_> = (0..16).filter(|&i| cells[i] != expected[i]).collect();
            if expected == previous {
                assert!(changed.is_empty());
            } else {
                assert_eq!(changed.len(), 1);
                assert_eq!(expected[changed[0]], 0);
                assert!((1..=2).contains(&cells[changed[0]]));
            }
        }
        let vram = std::fs::read(path.with_extension("vram")).unwrap();
        for (col, value) in [(1, "SCORE".to_owned()), (7, format!("{score:>5}"))] {
            for (i, ch) in value.bytes().enumerate() {
                assert_eq!(vram[0x1800 + col + i], 13 + ch - b' ');
            }
        }
        assert_eq!(vram[16], 0xff);
        for row in 0..4 {
            for col in 0..4 {
                for y in 0..2 {
                    for x in 0..2 {
                        assert_eq!(
                            vram[0x1800 + (3 + row * 3 + y) * 32 + 3 + col * 4 + x],
                            cells[row * 4 + col]
                        );
                    }
                }
            }
        }
        let row = memory[address("CursorRow") - 0xc000] as usize;
        let col = memory[address("CursorCol") - 0xc000] as usize;
        assert!(row < 4 && col < 4);
        assert_ne!(cells[row * 4 + col], 0);
        let oam = std::fs::read(path.with_extension("oam")).unwrap();
        assert_eq!(
            &oam[..4],
            &[(24 + row * 24 + 16) as u8, (24 + col * 32 + 8) as u8, 12, 0]
        );
        let rgba = std::fs::read(path.with_extension("rgba")).unwrap();
        assert_eq!(rgba.len(), 160 * 144 * 4);
        let shades = if cgb {
            [
                [255, 255, 255, 255],
                [165, 206, 165, 255],
                [66, 115, 66, 255],
                [0, 0, 0, 255],
            ]
        } else {
            [
                [255, 255, 255, 255],
                [170, 170, 170, 255],
                [85, 85, 85, 255],
                [0, 0, 0, 255],
            ]
        };
        let diamond = [0u8, 0x18, 0x3c, 0x7e, 0x7e, 0x3c, 0x18, 0];
        for r in 0..4 {
            for c in 0..4 {
                for y in 0..16 {
                    for x in 0..16 {
                        let value = cells[r * 4 + c];
                        let cursor =
                            r == row && c == col && x < 8 && y < 8 && diamond[y] & (0x80 >> x) != 0;
                        let color = if cursor {
                            3
                        } else if value == 0 {
                            0
                        } else if x % 8 == 0 || x % 8 == 7 || y % 8 == 0 || y % 8 == 7 {
                            3
                        } else {
                            match value % 3 {
                                1 => 2,
                                2 => 3,
                                _ => 1,
                            }
                        };
                        let pixel = ((24 + r * 24 + y) * 160 + 24 + c * 32 + x) * 4;
                        assert_eq!(
                            &rgba[pixel..pixel + 4],
                            &shades[color],
                            "{label} cell({c},{r}) pixel({x},{y})"
                        );
                    }
                }
            }
        }
        previous = cells;
        previous_score = score;
    }
}
