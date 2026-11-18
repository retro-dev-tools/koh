use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

fn step(state: &mut u16) -> u16 {
    *state ^= *state << 7;
    *state ^= *state >> 9;
    *state ^= *state << 8;
    *state
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn framework_rng_timer_assets_input_and_clock_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FrameworkContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in [
        "Random",
        "TimerContract",
        "Assets",
        "InputContract",
        "GameContract",
    ] {
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
        let mode = match entry {
            "InputContract" => "--native-framework-input",
            "GameContract" => "--native-framework-game",
            "Assets" => "--native-maps",
            _ => "--native-heap",
        };
        let text = execute_mode(host, directory.path(), &linked, "__koh_exit", mode);
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{entry}: {text}"
        );
        let expected: Vec<u8> = match entry {
            "Random" => {
                let mut state = 0x1234;
                let sequence: Vec<_> = (0..8).map(|_| (step(&mut state) >> 8) as u8).collect();
                let mut result = sequence.clone();
                result.extend([sequence[0], sequence[3] % 10]);
                let actual = &fields[4][(0xf00 + 10) * 2..(0xf00 + 12) * 2];
                let low = u8::from_str_radix(&actual[..2], 16).unwrap();
                let high = u8::from_str_radix(&actual[2..], 16).unwrap();
                assert_ne!([low, high], [0, 0], "zero seed must recover");
                result.extend([low, high]);
                state = 0x1234 ^ 0x5a;
                step(&mut state);
                let mixed = (step(&mut state) >> 8) as u8;
                assert_ne!(mixed, sequence[0]);
                result.push(mixed);
                step(&mut state);
                result.push(0);
                result.push(u8::from((step(&mut state) >> 8) < 255));
                result.push(0);
                result.extend(step(&mut state).to_le_bytes());
                result
            }
            "TimerContract" => vec![0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0],
            "Assets" => vec![2, 4, 4, 5, 2, 2],
            "InputContract" => vec![1, 1, 1, 0, 1, 0, 1, 1, 0, 0, 255, 1, 0, 0, 1, 0, 1],
            "GameContract" => vec![0, 1, 3, 1],
            _ => unreachable!(),
        };
        assert_heap_snapshot(
            &fields[4][0xf00 * 2..(0xf00 + expected.len()) * 2],
            &expected,
            entry,
        );
        assert_heap_snapshot(
            &fields[4][0xef0 * 2..0xf00 * 2],
            &[0x5a; 16],
            "packet prefix",
        );
        assert_heap_snapshot(
            &fields[4][(0xf00 + expected.len()) * 2..],
            &vec![0x5a; 256 - expected.len()],
            "packet suffix",
        );
        if matches!(entry, "Assets" | "GameContract") {
            let mut vram = vec![0xa7; 8192];
            vram[0x1800..].fill(0);
            if entry == "Assets" {
                vram[64..96].copy_from_slice(&[
                    0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd,
                    0xee, 0x0f, 0xf0, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9,
                    0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf,
                ]);
                for (x, y, tile) in [(3, 5, 1), (4, 5, 2), (3, 6, 2), (4, 6, 1)] {
                    vram[0x1800 + y * 32 + x] = tile;
                }
            }
            assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, entry);
            if color {
                assert_heap_snapshot(fields[7], &[0xb8; 8192], entry);
            }
            assert_heap_snapshot(fields[if color { 9 } else { 7 }], &[0; 160], entry);
            assert_eq!(fields[if color { 10 } else { 8 }], "91E4E4E4");
        }
    }
}

#[test]
fn xorshift_specification_visits_every_nonzero_state() {
    let mut state = 0x1234;
    let mut visited = vec![false; 65536];
    for _ in 0..65535 {
        assert_ne!(state, 0);
        assert!(!visited[state as usize]);
        visited[state as usize] = true;
        step(&mut state);
    }
    assert_eq!(state, 0x1234);
}
