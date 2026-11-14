use koh_asm::{
    assemble,
    model::*,
    object::{read_elf, write_elf},
};

#[test]
fn complete_expressions_survive_assembly_independent_of_output_format() {
    let source = "SECTION \"code\",ROM0[$100]\ndb HIGH(Target)+1\ndw LOW(Target)\nSECTION \"target\",ROMX,BANK[2]\nTarget: nop";
    let object = assemble(source, "relocations.asm", false).unwrap();
    assert_eq!(object.sections[0].patches.len(), 2);
    assert!(matches!(
        object.sections[0].patches[0].expression,
        RelocationExpr::Binary(BinaryOp::Add, _, _)
    ));
    assert_eq!(object.sections[0].patches[1].width, PatchWidth::Word);
    assert!(read_elf(&write_elf(&object).unwrap()).is_ok());
}

#[test]
fn missing_references_are_explicit_imports_or_diagnostics() {
    let source = "SECTION \"code\",ROM0\ndw HIGH(Missing)+1";
    assert!(
        assemble(source, "missing.asm", false).unwrap_err()[0]
            .message
            .contains("Missing")
    );
    let object = assemble(source, "import.asm", true).unwrap();
    assert_eq!(object.symbols.len(), 1);
    assert_eq!(object.symbols[0].definition, SymbolDefinition::Import);
    assert!(read_elf(&write_elf(&object).unwrap()).is_ok());
}

#[test]
fn lowering_errors_keep_the_actual_use_location() {
    let errors = assemble(
        "SECTION \"code\",ROM0\nnop\ndw Missing",
        "location.asm",
        false,
    )
    .unwrap_err();
    assert_eq!(errors[0].file, "location.asm");
    assert_eq!(errors[0].line, 3);
    assert_eq!(errors[0].message, "undefined symbol 'Missing'");
}

#[test]
fn pc_tracks_execution_section_inside_load() {
    let object = assemble(
        "SECTION \"rom\",ROM0[$100]\nLOAD \"ram\",WRAM0\ndw @\nENDL",
        "load.asm",
        false,
    )
    .unwrap();
    let patch = &object.sections[0].patches[0];
    assert_eq!(patch.pc.section, SectionId(1));
    assert_eq!(patch.pc.offset, 0);
    assert_eq!(patch.expression, RelocationExpr::CurrentAddress);
    assert_eq!(object.sections[1].size, 2);
    assert!(object.sections[1].data.is_empty());
    assert!(read_elf(&write_elf(&object).unwrap()).is_ok());
}

#[test]
fn invalid_graph_is_rejected_before_serialization() {
    let mut object = assemble("SECTION \"code\",ROM0\ndw Missing", "invalid.asm", true).unwrap();
    object.sections[0].patches[0].expression = RelocationExpr::Symbol(SymbolId(999));
    assert!(object.validate().is_err());
    assert!(write_elf(&object).is_err());
}

#[test]
fn raw_identifier_escape_is_not_part_of_symbol_identity() {
    let object = assemble(
        "SECTION \"code\",ROM0\n#ld:: nop\ndw #ld\nDEF #DEF EQU 7\ndb #DEF",
        "raw.asm",
        false,
    )
    .unwrap();
    assert!(
        object
            .symbols
            .iter()
            .any(|s| s.name == "ld" && s.binding == SymbolBinding::Exported)
    );
    assert!(
        object
            .symbols
            .iter()
            .any(|s| s.name == "DEF" && s.definition == SymbolDefinition::Absolute(7))
    );
    assert!(object.symbols.iter().all(|s| !s.name.starts_with('#')));
    assert_eq!(object.sections[0].data[3], 7);
    assert!(read_elf(&write_elf(&object).unwrap()).is_ok());
}

#[test]
fn floating_pc_expressions_and_numeric_jr_targets_remain_relocations() {
    let object = assemble(
        "SECTION \"pad\",ROM0[0]\nds $100\nSECTION \"code\",ROM0\ndb HIGH(@)\njr $103",
        "pc.asm",
        false,
    )
    .unwrap();
    assert_eq!(object.sections[1].patches.len(), 2);
    assert!(matches!(
        object.sections[1].patches[0].expression,
        RelocationExpr::Unary(UnaryOp::High, _)
    ));
    assert!(matches!(
        object.sections[1].patches[1].semantics,
        PatchSemantics::PcRelative { bias: 2 }
    ));
}

#[test]
fn bank_current_uses_its_own_section() {
    let object = assemble(
        "SECTION \"one\",ROMX,BANK[1]\ndb BANK(@)\nSECTION \"two\",ROMX,BANK[2]\ndb BANK(@)",
        "bank.asm",
        false,
    )
    .unwrap();
    assert_eq!(object.sections[0].data, [1]);
    assert_eq!(object.sections[1].data, [2]);
    let floating = assemble(
        "SECTION \"one\",ROMX[$4000]\ndb BANK(@)\nSECTION \"two\",ROMX[$4000],BANK[2]\ndb BANK(@)",
        "floating-bank.asm",
        false,
    )
    .unwrap();
    assert_eq!(
        floating.sections[0].patches[0].expression,
        RelocationExpr::BankCurrent
    );
}

#[test]
fn current_address_divisor_is_not_assumed_to_be_zero() {
    let object = assemble(
        "SECTION \"pad\",ROM0[0]\nds $100\nSECTION \"code\",ROM0\ndw $100/@",
        "divide.asm",
        false,
    )
    .unwrap();
    assert!(matches!(
        object.sections[1].patches[0].expression,
        RelocationExpr::Binary(BinaryOp::Divide, _, _)
    ));
    assert!(read_elf(&write_elf(&object).unwrap()).is_ok());
}

#[test]
fn signed_offset_semantics_survive_deferred_resolution() {
    let object = assemble("SECTION \"code\",ROM0\nadd sp, Delta", "signed.asm", true).unwrap();
    let patch = &object.sections[0].patches[0];
    assert_eq!(patch.width, PatchWidth::Byte);
    assert_eq!(patch.semantics, PatchSemantics::Signed);
    assert!(read_elf(&write_elf(&object).unwrap()).is_ok());
}

#[test]
fn unsupported_relocation_functions_and_constant_faults_are_diagnostics() {
    for expr in ["MYSTERY(Missing)", "1/0", "Missing/0", "STRLEN(Missing)"] {
        let source = format!("SECTION \"code\",ROM0\ndw {expr}");
        assert!(
            assemble(&source, "bad-expression.asm", true).is_err(),
            "{expr}"
        );
    }
}

#[test]
fn jr_wraps_cpu_pc_at_end_of_address_space() {
    for (address, target, expected) in [
        (0xfffd, "$0000", 1),
        (0xfffc, "-1", 1),
        (0xfffd, "$ff80", 0x81),
    ] {
        let source =
            format!("SECTION \"rom\",ROM0\nLOAD \"run\",HRAM[${address:04x}]\njr {target}\nENDL\n");
        let object = assemble(&source, "wrap.asm", false).unwrap();
        assert_eq!(object.sections[0].data, [0x18, expected]);
        assert!(object.sections[0].patches.is_empty());
    }
    for target in ["$10000", "-32769"] {
        let source = format!("SECTION \"rom\",ROM0\nLOAD \"run\",HRAM[$fffd]\njr {target}\nENDL\n");
        assert!(assemble(&source, "invalid-target.asm", false).is_err());
    }
}

#[test]
fn floating_jr_wrap_defers_until_target_is_placed() {
    let source = "SECTION \"rom\",ROM0\nLOAD \"run\",HRAM\nStart:\njr Start-$fffd\nENDL\n";
    let object = assemble(source, "floating-wrap.asm", false).unwrap();
    assert_eq!(object.sections[0].patches.len(), 1);
    let decoded = read_elf(&write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(&[decoded], &Default::default()).unwrap();
    assert_eq!(&linked.rom[..2], &[0x18, 1]);
}
