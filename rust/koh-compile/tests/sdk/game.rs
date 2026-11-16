use super::{build, report, support};
use koh_compile::{
    layout,
    resolve::{Definition, Resolver},
    sm83,
};
use rstest::rstest;
use std::path::Path;

fn definition(resolver: &Resolver<'_>, assembly: usize, name: &str) -> Definition {
    let metadata = resolver.assembly(assembly).unwrap().metadata();
    let index = metadata
        .type_defs
        .iter()
        .position(|ty| metadata.strings.get(ty.type_name).unwrap() == name)
        .unwrap();
    Definition {
        assembly,
        token: 0x02000001 + index as u32,
    }
}

fn field(resolver: &Resolver<'_>, owner: Definition, name: &str) -> Definition {
    let metadata = resolver.assembly(owner.assembly).unwrap().metadata();
    let index = (owner.token & 0xffffff) as usize - 1;
    let start = metadata.type_defs[index].field_list as usize - 1;
    let end = metadata
        .type_defs
        .get(index + 1)
        .map_or(metadata.fields.len(), |ty| ty.field_list as usize - 1);
    let index = (start..end)
        .find(|&index| metadata.strings.get(metadata.fields[index].name).unwrap() == name)
        .unwrap();
    Definition {
        assembly: owner.assembly,
        token: 0x04000001 + index as u32,
    }
}

fn offset(resolver: &Resolver<'_>, owner: Definition, name: &str) -> usize {
    let identity = field(resolver, owner, name);
    usize::from(
        layout::class(resolver, owner)
            .unwrap()
            .fields
            .iter()
            .find(|field| field.identity == identity)
            .unwrap()
            .offset,
    )
}

fn word(memory: &[u8], address: usize) -> usize {
    assert!(
        (0xc000..0xdfff).contains(&address),
        "WRAM address {address:04x}"
    );
    usize::from(u16::from_le_bytes(
        memory[address - 0xc000..address - 0xc000 + 2]
            .try_into()
            .unwrap(),
    ))
}

fn slide(cells: [u8; 16], direction: &str) -> ([u8; 16], usize) {
    let mut result = [0; 16];
    let mut score = 0;
    for line in 0usize..4 {
        let index = |position: usize| match direction {
            "Left" => line * 4 + position,
            "Right" => line * 4 + 3 - position,
            "Up" => position * 4 + line,
            "Down" => (3 - position) * 4 + line,
            _ => panic!("Unknown direction {direction}"),
        };
        let values: Vec<_> = (0..4)
            .map(|position| cells[index(position)])
            .filter(|&value| value != 0)
            .collect();
        let mut read = 0;
        let mut write = 0;
        while read < values.len() {
            let mut value = values[read];
            read += 1;
            if values.get(read) == Some(&value) {
                value += 1;
                score += 1usize << value;
                read += 1;
            }
            result[index(write)] = value;
            write += 1;
        }
    }
    (result, score)
}

#[rstest]
#[case([1,1,1,1], [2,2,0,0], 8)]
#[case([1,1,2,0], [2,2,0,0], 4)]
#[case([0,2,0,2], [3,0,0,0], 8)]
#[case([1,2,3,4], [1,2,3,4], 0)]
fn slide_reference_merges_each_tile_once(
    #[case] line: [u8; 4],
    #[case] expected: [u8; 4],
    #[case] score: usize,
) {
    let mut cells = [0; 16];
    cells[..4].copy_from_slice(&line);
    let (actual, gained) = slide(cells, "Left");
    assert_eq!(&actual[..4], &expected);
    assert_eq!(&actual[4..], &[0; 12]);
    assert_eq!(gained, score);
}

#[rstest]
#[case("Left", [1,1,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0], [2,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0])]
#[case("Right", [1,1,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0], [0,0,0,2, 0,0,0,0, 0,0,0,0, 0,0,0,0])]
#[case("Up", [1,0,0,0, 1,0,0,0, 0,0,0,0, 0,0,0,0], [2,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0])]
#[case("Down", [1,0,0,0, 1,0,0,0, 0,0,0,0, 0,0,0,0], [0,0,0,0, 0,0,0,0, 0,0,0,0, 2,0,0,0])]
fn slide_reference_respects_direction(
    #[case] direction: &str,
    #[case] input: [u8; 16],
    #[case] expected: [u8; 16],
) {
    assert_eq!(slide(input, direction), (expected, 4));
}

fn text(path: &Path, column: usize, row: usize, value: &str) {
    let vram = std::fs::read(path.with_extension("vram")).unwrap();
    for (index, byte) in value.bytes().enumerate() {
        assert_eq!(
            vram[0x1800 + row * 32 + column + index],
            12 + byte - b' ',
            "{} expected {value:?} at ({column},{row})",
            path.display()
        );
    }
}

fn graphics(path: &Path, cells: &[u8; 16], cgb: bool) {
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
    let color = |value, x, y| {
        if value == 0 {
            0
        } else if x % 8 == 0 || x % 8 == 7 || y % 8 == 0 || y % 8 == 7 {
            3
        } else {
            match value % 3 {
                1 => 2,
                2 => 3,
                _ => 1,
            }
        }
    };
    for row in 0..4 {
        for col in 0..4 {
            for y in 0..16 {
                for x in 0..16 {
                    let pixel = ((24 + row * 24 + y) * 160 + 24 + col * 32 + x) * 4;
                    assert_eq!(
                        &rgba[pixel..pixel + 4],
                        &shades[color(cells[row * 4 + col], x, y)],
                        "{} cell({col},{row}) pixel({x},{y})",
                        path.display()
                    );
                }
            }
        }
    }
    let expected: Vec<u8> = (0..12)
        .flat_map(|value| {
            (0..8).flat_map(move |y| {
                let mut planes = [0; 2];
                for x in 0..8 {
                    let shade = color(value, x, y);
                    for bit in 0..2 {
                        planes[bit] |= (((shade >> bit) & 1) as u8) << (7 - x);
                    }
                }
                planes
            })
        })
        .collect();
    let vram = std::fs::read(path.with_extension("vram")).unwrap();
    assert_eq!(&vram[..192], &expected);
    if cgb {
        let palette = std::fs::read(path.with_extension("palette")).unwrap();
        assert_eq!(&palette[..8], &[0xff, 0x7f, 0x34, 0x53, 0xc8, 0x21, 0, 0]);
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn sdk_2048_scene_game_obeys_rules_and_draws_tiles(#[case] configuration: &str, #[case] cgb: bool) {
    let directory = tempfile::Builder::new()
        .prefix("koh SDK 2048 ")
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
<ItemGroup><Compile Include="$(KohRoot)samples/csharp/gb-2048-v2/*.cs" /></ItemGroup>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.targets" />
</Project>"#,
    )
    .unwrap();
    let rom = directory.path().join("game.gb");
    let object = directory.path().join("game.o");
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
    let elf = koh_objects::read_elf(&std::fs::read(&object).unwrap()).unwrap();
    let linked = koh_link::link(
        &[elf],
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
    let symbol = |name: &str| {
        linked
            .symbols
            .iter()
            .find(|symbol| symbol.name == name)
            .unwrap()
            .value as usize
    };
    let host = support::build_fixture_host("GameHost");
    let script = directory.path().join("input.txt");
    let directions = ["Left", "Up", "Right", "Down", "Left", "Up", "Right", "Down"];
    let mut actions =
        String::from("1200:dump:title\n1300:press:Start\n1310:release:Start\n1600:dump:initial\n");
    for (index, direction) in directions.iter().enumerate() {
        let frame = 1700 + 400 * index;
        actions.push_str(&format!(
            "{frame}:press:{direction}\n{}:release:{direction}\n{}:dump:move{index}\n",
            frame + 10,
            frame + 300
        ));
    }
    std::fs::write(&script, actions).unwrap();
    let executed = support::dotnet()
        .arg(&host.dll)
        .arg(&rom)
        .arg(format!("{:x}", symbol("__koh_abort")))
        .arg(&script)
        .output()
        .unwrap();
    assert!(executed.status.success(), "{}", report(&executed));
    let output = String::from_utf8(executed.stdout).unwrap();
    let fields: Vec<_> = output.split_whitespace().collect();
    assert_eq!(fields.len(), 4, "{output}");
    assert!(fields[0].parse::<usize>().unwrap() < 600, "{output}");
    assert_eq!(
        &fields[1..],
        &["4801", "0", if cgb { "Cgb" } else { "Dmg" }]
    );
    let bin = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let game = std::fs::read(bin.join("Game.dll")).unwrap();
    let library = std::fs::read(bin.join("Koh.GameBoy.dll")).unwrap();
    let resolver = Resolver::new(&[&game, &library]).unwrap();
    let play = definition(&resolver, 0, "PlayScene");
    let board = definition(&resolver, 0, "Board");
    let current = field(&resolver, definition(&resolver, 1, "Game"), "_current");
    let current_address = symbol(&sm83::static_symbol(current));
    let board_offset = offset(&resolver, play, "_board");
    let cells_offset = offset(&resolver, board, "_cells");
    let score_offset = offset(&resolver, board, "<Score>k__BackingField");
    let title = directory.path().join("game.gb.title.dump");
    let memory = std::fs::read(title.with_extension("wram")).unwrap();
    assert_eq!(
        word(&memory, word(&memory, current_address)),
        symbol(&sm83::class_symbol(Some(definition(
            &resolver,
            0,
            "TitleScene"
        ))))
    );
    text(&title, 8, 7, "2048");
    text(&title, 4, 10, "PRESS START");
    let mut previous = [0; 16];
    let mut previous_score = 0;
    let labels =
        std::iter::once(String::from("initial")).chain((0..8).map(|index| format!("move{index}")));
    for (step, label) in labels.enumerate() {
        let path = directory.path().join(format!("game.gb.{label}.dump"));
        let memory = std::fs::read(path.with_extension("wram")).unwrap();
        let scene = word(&memory, current_address);
        assert_eq!(
            word(&memory, scene),
            symbol(&sm83::class_symbol(Some(play)))
        );
        let board_address = word(&memory, scene + board_offset);
        assert_eq!(
            word(&memory, board_address),
            symbol(&sm83::class_symbol(Some(board)))
        );
        let array = word(&memory, board_address + cells_offset);
        assert_eq!(word(&memory, array), 16);
        let cells: [u8; 16] = memory[array - 0xc000 + 2..array - 0xc000 + 18]
            .try_into()
            .unwrap();
        let score = word(&memory, board_address + score_offset);
        if step == 0 {
            assert_eq!(score, 0);
            assert_eq!(cells.iter().filter(|&&value| value != 0).count(), 2);
            assert!(cells.iter().all(|&value| value <= 2));
        } else {
            let (expected, gained) = slide(previous, directions[step - 1]);
            assert_eq!(score, previous_score + gained);
            let differences: Vec<_> = (0..16)
                .filter(|&index| expected[index] != cells[index])
                .collect();
            if expected == previous {
                assert!(differences.is_empty());
            } else {
                assert_eq!(
                    differences.len(),
                    1,
                    "spawn after {label}: {expected:?} -> {cells:?}"
                );
                let index = differences[0];
                assert_eq!(expected[index], 0);
                assert!((1..=2).contains(&cells[index]));
            }
        }
        graphics(&path, &cells, cgb);
        text(&path, 1, 0, "SCORE");
        text(&path, 7, 0, &format!("{score:>5}"));
        previous = cells;
        previous_score = score;
    }
    assert!(previous.iter().filter(|&&value| value != 0).count() > 2);
}
