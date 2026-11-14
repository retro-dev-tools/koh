use koh_asm::assemble;

const NAMESPACE: &str = r#"SECTION "code",ROM0[0]
MyLabel: nop
DEF MyConst EQU 7
ld a,DEF(MyLabel)
ld b,DEF(MyConst)
ld c,ISCONST(MyLabel)
ld d,DEF(Missing)
bit DEF(MyConst),a
rst DEF(MyConst)*8
"#;

const CHARMAP: &str = r#"NEWCHARMAP pairs
CHARMAP "ab",$70
CHARMAP "A",3
SECTION "code",ROM0[0]
ld a,CHARLEN("ab")
ld hl,"A"
ld hl,sp+'A'
add sp,CHARLEN("ab")
ld hl,sp+CHARLEN("ab")
ldh a,[$FF00+CHARLEN("ab")]
res CHARLEN("ab"),b
set 'A',c
stop CHARLEN("ab")
"#;

const PRECISION: &str = r#"OPT Q.8
SECTION "code",ROM0[0]
ld hl,1.0
ld a,0.5
ldh a,[$FF00+0.5]
bit 0.015625,a
rst 0.125
"#;

const DECLARATIONS: &str = r#"SECTION "code",ROM0
Floating: nop
ld a,DEF(Floating)
db DEF(Floating)
ld b,DEF(LaterConst)
db DEF(LaterConst)
DEF LaterConst EQU 7
ld c,DEF(LaterConst)
db DEF(LaterConst)
"#;

const BUILTINS: &str = r#"MACRO probe
 ld a,DEF(_NARG)
 db DEF(_NARG),_NARG
ENDM
SECTION "code",ROM0[0]
db DEF(_NARG),DEF(_RS)
probe
probe one,two
db DEF(_NARG)
RSSET 8
ld b,DEF(_RS)
db _RS
"#;

fn bytes(source: &str) -> Vec<u8> {
    assemble(source, "instruction-context.asm", false)
        .unwrap()
        .sections[0]
        .data
        .clone()
}

#[test]
fn instruction_queries_use_the_current_symbol_namespace() {
    assert_eq!(
        bytes(NAMESPACE),
        [0, 0x3e, 1, 0x06, 1, 0x0e, 1, 0x16, 0, 0xcb, 0x4f, 0xcf]
    );
}

#[test]
fn instruction_values_use_the_active_character_map() {
    assert_eq!(
        bytes(CHARMAP),
        [
            0x3e, 1, 0x21, 3, 0, 0xf8, 3, 0xe8, 1, 0xf8, 1, 0xf0, 1, 0xcb, 0x88, 0xcb, 0xd9, 0x10,
            1
        ]
    );
}

#[test]
fn instruction_fixed_point_values_use_the_active_precision() {
    assert_eq!(
        bytes(PRECISION),
        [0x21, 0, 1, 0x3e, 0x80, 0xf0, 0x80, 0xcb, 0x67, 0xe7]
    );
}

#[test]
fn instruction_queries_are_captured_before_forward_definitions() {
    assert_eq!(
        bytes(
            "SECTION \"code\",ROM0[$100]\nld a,DEF(Later)\nld hl,@\njr Later\nnop\nLater: ld b,DEF(Later)\n"
        ),
        [0x3e, 0, 0x21, 2, 1, 0x18, 1, 0, 0x06, 1]
    );
}

#[test]
fn definition_queries_use_declarations_without_requiring_addresses() {
    assert_eq!(bytes(DECLARATIONS), [0, 0x3e, 1, 1, 0x06, 0, 0, 0x0e, 1, 1]);
}

#[test]
fn definition_queries_preserve_dynamic_builtin_namespaces() {
    assert_eq!(
        bytes(BUILTINS),
        [0, 1, 0x3e, 1, 1, 0, 0x3e, 1, 1, 2, 0, 0x06, 1, 8]
    );
    assert_eq!(
        bytes("SECTION \"code\",ROM0[0]\ndb DEF(Later)\nDEF Later EQU Final+1\nFinal: nop"),
        [0, 0]
    );
}

#[test]
fn invalid_context_values_are_diagnostics() {
    for body in [
        "ld a,1/0",
        "bit Missing,a",
        "rst Missing",
        "ld a,DEF(1)",
        "OPT q.8\nld a,1.0",
        "NEWCHARMAP m\nCHARMAP \"A\",8\nbit 'A',a",
        "ldh a,[$fe00+DEF(Label)]",
    ] {
        let source = format!("SECTION \"code\",ROM0[0]\nLabel: nop\n{body}\n");
        assert!(
            assemble(&source, "invalid-instruction-context.asm", true).is_err(),
            "{body}"
        );
    }
}

#[test]
fn context_dependent_instruction_bytes_match_rgbds_1_0_1() {
    if std::env::var("KOH_RGBDS_TEST").as_deref() != Ok("1") {
        return;
    }
    for tool in ["rgbasm", "rgblink"] {
        let output = std::process::Command::new(tool)
            .arg("--version")
            .output()
            .unwrap();
        assert!(output.status.success());
        assert_eq!(
            String::from_utf8_lossy(&output.stdout).trim(),
            format!("{tool} v1.0.1")
        );
    }
    let directory =
        std::env::temp_dir().join(format!("koh-instruction-context-{}", std::process::id()));
    std::fs::create_dir(&directory).unwrap();
    for (name, source) in [
        ("namespace", NAMESPACE),
        ("charmap", CHARMAP),
        ("precision", PRECISION),
        ("declarations", DECLARATIONS),
        ("builtins", BUILTINS),
    ] {
        let asm = directory.join(format!("{name}.asm"));
        let reference = directory.join(format!("{name}-reference.o"));
        let native = directory.join(format!("{name}-native.o"));
        std::fs::write(&asm, source).unwrap();
        let output = std::process::Command::new("rgbasm")
            .args(["-o", reference.to_str().unwrap(), asm.to_str().unwrap()])
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{name}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        let object = assemble(source, asm.to_str().unwrap(), false).unwrap();
        std::fs::write(&native, koh_asm::object::write_rgbds(&object).unwrap()).unwrap();
        let mut roms = Vec::new();
        for (kind, path) in [("reference", reference), ("native", native)] {
            let rom = directory.join(format!("{name}-{kind}.gb"));
            let output = std::process::Command::new("rgblink")
                .args(["-x", "-o", rom.to_str().unwrap(), path.to_str().unwrap()])
                .output()
                .unwrap();
            assert!(
                output.status.success(),
                "{name}/{kind}: {}",
                String::from_utf8_lossy(&output.stderr)
            );
            roms.push(std::fs::read(rom).unwrap());
        }
        assert_eq!(roms[0], roms[1], "{name}");
    }
    std::fs::remove_dir_all(directory).unwrap();
}
