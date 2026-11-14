use koh_asm::{
    assemble,
    model::*,
    object::{write_kobj, write_rgbds},
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
    assert!(write_kobj(&object).is_err());
    assert!(write_rgbds(&object).is_ok());
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
    assert!(write_kobj(&object).is_err());
    assert!(write_rgbds(&object).is_ok());
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
    assert!(write_rgbds(&object).is_ok());
}

#[test]
fn invalid_graph_is_rejected_before_serialization() {
    let mut object = assemble("SECTION \"code\",ROM0\ndw Missing", "invalid.asm", true).unwrap();
    object.sections[0].patches[0].expression = RelocationExpr::Symbol(SymbolId(999));
    assert!(object.validate().is_err());
    assert!(write_rgbds(&object).is_err());
    assert!(write_kobj(&object).is_err());
}

#[test]
fn signed_offset_semantics_survive_deferred_resolution() {
    let object = assemble("SECTION \"code\",ROM0\nadd sp, Delta", "signed.asm", true).unwrap();
    let patch = &object.sections[0].patches[0];
    assert_eq!(patch.width, PatchWidth::Byte);
    assert_eq!(patch.semantics, PatchSemantics::Signed);
    assert!(write_kobj(&object).is_err());
    assert!(write_rgbds(&object).is_ok());
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
