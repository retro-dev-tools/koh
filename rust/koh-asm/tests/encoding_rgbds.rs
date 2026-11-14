use std::{fs, path::Path, process::Command};

fn run(program: &str, args: &[&str]) {
    let output = Command::new(program).args(args).output().unwrap();
    assert!(
        output.status.success(),
        "{program}: {}",
        String::from_utf8_lossy(&output.stderr)
    );
}

fn corpus() -> Vec<String> {
    let mut lines = Vec::new();
    let registers = ["b", "c", "d", "e", "h", "l", "[hl]", "a"];
    for a in registers {
        for b in registers {
            if (a, b) != ("[hl]", "[hl]") {
                lines.push(format!("ld {a},{b}"));
            }
        }
        lines.extend([format!("ld {a},42"), format!("inc {a}"), format!("dec {a}")]);
        for op in ["add", "adc", "sub", "sbc", "and", "xor", "or", "cp"] {
            lines.extend([format!("{op} a,{a}"), format!("{op} {a}")]);
        }
        for op in ["rlc", "rrc", "rl", "rr", "sla", "sra", "swap", "srl"] {
            lines.push(format!("{op} {a}"));
        }
        for op in ["bit", "res", "set"] {
            for bit in 0..8 {
                lines.push(format!("{op} {bit},{a}"));
            }
        }
    }
    for p in ["bc", "de", "hl", "sp"] {
        lines.extend([
            format!("ld {p},$1234"),
            format!("inc {p}"),
            format!("dec {p}"),
            format!("add hl,{p}"),
        ]);
    }
    for p in ["bc", "de", "hl", "af"] {
        lines.extend([format!("push {p}"), format!("pop {p}")]);
    }
    for c in ["nz", "z", "nc", "c"] {
        lines.extend([
            format!("ret {c}"),
            format!("jp {c},$1234"),
            format!("call {c},$1234"),
        ]);
    }
    for op in ["add", "adc", "sub", "sbc", "and", "xor", "or", "cp"] {
        lines.push(format!("{op} 42"));
    }
    lines.extend(
        [
            "nop",
            "halt",
            "stop",
            "stop $42",
            "di",
            "ei",
            "daa",
            "cpl",
            "scf",
            "ccf",
            "rlca",
            "rrca",
            "rla",
            "rra",
            "reti",
            "ret",
            "jp hl",
            "ld sp,hl",
            "jp $1234",
            "call $1234",
            "ld [$1234],sp",
            "ld [$1234],a",
            "ld a,[$1234]",
            "ldh [$ff80],a",
            "ldh a,[$ffff]",
            "add sp,-128",
            "ld hl,sp+127",
            "ld hl,sp-128",
            "ld [$ff00+c],a",
            "ld a,[$ff00+c]",
        ]
        .map(str::to_owned),
    );
    for p in ["bc", "de", "hl+", "hl-", "hli", "hld"] {
        lines.extend([format!("ld [{p}],a"), format!("ld a,[{p}]")]);
    }
    for v in (0..=56).step_by(8) {
        lines.push(format!("rst {v}"));
    }
    lines
}

fn compare(directory: &Path, name: &str, body: &str) {
    let source = format!("SECTION \"test\", ROM0[$0000]\n{body}\n");
    let asm = directory.join(format!("{name}.asm"));
    let reference_object = directory.join(format!("{name}-reference.o"));
    let rust_object = directory.join(format!("{name}-rust.o"));
    let reference_rom = directory.join(format!("{name}-reference.gb"));
    let rust_rom = directory.join(format!("{name}-rust.gb"));
    fs::write(&asm, &source).unwrap();
    let object = koh_asm::assemble(&source, asm.to_str().unwrap(), true).unwrap();
    fs::write(&rust_object, koh_asm::object::write_rgbds(&object).unwrap()).unwrap();
    run(
        "rgbasm",
        &[
            "-o",
            reference_object.to_str().unwrap(),
            asm.to_str().unwrap(),
        ],
    );
    run(
        "rgblink",
        &[
            "-x",
            "-o",
            reference_rom.to_str().unwrap(),
            reference_object.to_str().unwrap(),
        ],
    );
    run(
        "rgblink",
        &[
            "-x",
            "-o",
            rust_rom.to_str().unwrap(),
            rust_object.to_str().unwrap(),
        ],
    );
    assert_eq!(
        fs::read(rust_rom).unwrap(),
        fs::read(reference_rom).unwrap(),
        "{name}"
    );
}

#[test]
fn exhaustive_instruction_output_matches_rgbds_1_0_1() {
    if std::env::var("KOH_RGBDS_TEST").as_deref() != Ok("1") {
        return;
    }
    for program in ["rgbasm", "rgblink"] {
        let output = Command::new(program).arg("--version").output().unwrap();
        assert!(output.status.success());
        assert_eq!(
            String::from_utf8_lossy(&output.stdout).trim(),
            format!("{program} v1.0.1")
        );
    }
    let directory = std::env::temp_dir().join(format!("koh-encoding-rgbds-{}", std::process::id()));
    fs::create_dir(&directory).unwrap();
    let lines = corpus();
    assert_eq!(lines.len(), 565);
    compare(&directory, "opcodes", &lines.join("\n"));
    compare(
        &directory,
        "statements",
        "Start:: db 1,2, :: dw $1234, :: nop :: ret\nIF(1)\n db 3,\nELIF(0)\n db 4\nENDC\nASSERT(1)\n db\n",
    );
    compare(
        &directory,
        "condition-inversions",
        "ret !nz\nret !!z\nret !nc\nret !!!c\njp !nz,$1234\njp !!z,$1234\ncall !nc,$1234\ncall !!!c,$1234\njr !nz,End\njr !!z,End\njr !nc,End\njr !!!c,End\nEnd:\nret",
    );
    compare(
        &directory,
        "relocations",
        "Start:\n jr Forward\n jr nz,Forward\n jr z,Forward\n jr nc,Forward\n jr c,Forward\n db 0\nForward:\n jp Start\n call Forward\n ld hl,Forward\n ld a,LOW(Forward)\n jr Start\n",
    );
    fs::remove_dir_all(directory).unwrap();
}
