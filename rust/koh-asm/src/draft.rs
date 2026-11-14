use crate::{encode::OperandKind, expr::Expr, model::Provenance};

#[derive(Default)]
pub(crate) struct Object {
    pub sections: Vec<Section>,
    pub symbols: Vec<Symbol>,
}

#[derive(Default)]
pub(crate) struct Section {
    pub name: String,
    pub kind: u8,
    pub address: Option<i32>,
    pub bank: Option<i32>,
    pub align: u8,
    pub align_offset: i32,
    pub data: Vec<u8>,
    pub patches: Vec<Patch>,
    pub lines: Vec<crate::model::Line>,
    pub source: Option<Provenance>,
}

pub(crate) struct Symbol {
    pub name: String,
    pub kind: u8,
    pub visibility: u8,
    pub section: Option<usize>,
    pub value: i32,
    pub source: Option<Provenance>,
}

pub(crate) struct Patch {
    pub offset: usize,
    pub kind: u8,
    pub pc_after: i32,
    pub pc_offset: i32,
    pub pc_section: usize,
    pub expression: Expr,
    pub source: Provenance,
    pub constraint: Option<OperandKind>,
}
