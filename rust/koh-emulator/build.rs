use std::{env, fs, path::PathBuf};

fn main() {
    let source = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap())
        .join("../../src/Emulator/Koh.Boot");
    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    for file in fs::read_dir(&source).unwrap() {
        println!("cargo:rerun-if-changed={}", file.unwrap().path().display());
    }
    for (name, size) in [("dmg_boot", 0x100), ("cgb_boot", 0x900)] {
        let path = source.join(format!("{name}.asm"));
        let text = fs::read_to_string(&path).unwrap();
        let object = koh_asm::assemble(&text, &path.to_string_lossy(), false)
            .unwrap_or_else(|errors| panic!("boot assembly: {errors:?}"));
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                raw: true,
                size: Some(size),
                ..Default::default()
            },
        )
        .expect("boot link");
        fs::write(out.join(format!("{name}.bin")), linked.rom).unwrap();
    }
}
