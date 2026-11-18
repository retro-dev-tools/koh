use super::{Host, assert_heap_snapshot, compile, execute_mode, gameboy_images, host};
use koh_compile::resolve::Resolver;
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn mono_tiles_preserve_ink_paper_wrap_and_selected_banks(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Mono");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let data = [
        0x00u8, 0xff, 0x80, 0x01, 0x55, 0xaa, 0x3c, 0xc3, 0x0f, 0xf0, 0x12, 0x34, 0x56, 0x78, 0x9a,
        0xbc,
    ];
    let expanded = |ink: u8, paper: u8| {
        let mut bytes = [0u8; 32];
        for (row, &mono) in data.iter().enumerate() {
            for pixel in 0..8 {
                let shift = 7 - pixel;
                let index = if mono & (1 << shift) != 0 { ink } else { paper };
                bytes[row * 2] |= (index & 1) << shift;
                bytes[row * 2 + 1] |= ((index >> 1) & 1) << shift;
            }
        }
        bytes
    };
    for method in ["Main", "On", "BankOne", "Wrap", "Empty"] {
        let object = compile::object(&resolver, Some(&format!("Program::{method}"))).unwrap();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
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
            "--native-video",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{method}: {text}"
        );
        let selected = usize::from(color && method == "BankOne");
        for bank in 0..=usize::from(color) {
            let mut expected = vec![if bank == 0 { 0xa7 } else { 0xb8 }; 8192];
            if bank == selected && method != "Empty" {
                if method == "Wrap" {
                    let bytes = expanded(1, 2);
                    expected[255 * 16..256 * 16].copy_from_slice(&bytes[..16]);
                    expected[..16].copy_from_slice(&bytes[16..]);
                } else {
                    for ink in 0..4 {
                        for paper in 0..4 {
                            let start = usize::from(16 + (ink * 4 + paper) * 2) * 16;
                            expected[start..start + 32].copy_from_slice(&expanded(ink, paper));
                        }
                    }
                }
            }
            assert_heap_snapshot(fields[if color { 6 + bank } else { 5 }], &expected, method);
        }
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], method);
        }
    }
}
