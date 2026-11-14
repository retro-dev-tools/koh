use crate::expr::Expr;

#[derive(Clone, Debug, Default)]
pub struct Object {
    pub sections: Vec<Section>,
    pub symbols: Vec<Symbol>,
}

#[derive(Clone, Debug, Default)]
pub struct Section {
    pub name: String,
    pub kind: u8,
    pub address: Option<i32>,
    pub bank: Option<i32>,
    pub align: u8,
    pub align_offset: i32,
    pub data: Vec<u8>,
    pub patches: Vec<Patch>,
    pub lines: Vec<Line>,
}

#[derive(Clone, Debug)]
pub struct Symbol {
    pub name: String,
    pub kind: u8,
    pub visibility: u8,
    pub section: Option<usize>,
    pub value: i32,
}

#[derive(Clone, Debug)]
pub struct Patch {
    pub offset: usize,
    pub kind: u8,
    pub pc_after: i32,
    pub pc_offset: i32,
    pub expression: Expr,
    pub file: String,
    pub line: u32,
    pub symbol: Option<String>,
    pub addend: i32,
    pub shift: i32,
}

#[derive(Clone, Debug)]
pub struct Line {
    pub offset: usize,
    pub count: usize,
    pub file: String,
    pub line: u32,
}
