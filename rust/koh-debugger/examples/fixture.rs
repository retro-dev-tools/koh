fn main() {
    let directory = std::path::PathBuf::from(std::env::args_os().nth(1).expect("output directory"));
    std::fs::create_dir_all(&directory).unwrap();
    let source = "SECTION \"Entry\", ROM0[$100]\n jp Main\nSECTION \"main\", ROM0[$150]\nMain::\n call Sub\n ld a,$42\nLoop:: jr Loop\nSECTION \"sub\", ROM0[$200]\nSub:: nop\n ret\n";
    let object = koh_asm::assemble(source, "fixture.asm", false).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    std::fs::write(directory.join("fixture.gb"), &linked.rom).unwrap();
    std::fs::write(directory.join("fixture.elf"), linked.dwarf().unwrap()).unwrap();
    std::fs::write(directory.join("fixture.asm"), source).unwrap();
}
