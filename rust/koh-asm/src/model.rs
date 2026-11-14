use std::collections::HashSet;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct SectionId(pub usize);
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct SymbolId(pub usize);

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
    pub size: u32,
    pub data: Vec<u8>,
    pub patches: Vec<Patch>,
    pub lines: Vec<Line>,
    pub source: Option<Provenance>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SymbolBinding {
    Local,
    Exported,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SymbolDefinition {
    Import,
    Absolute(i32),
    Label { section: SectionId, offset: u32 },
}

#[derive(Clone, Debug)]
pub struct Symbol {
    pub name: String,
    pub binding: SymbolBinding,
    pub definition: SymbolDefinition,
    pub source: Option<Provenance>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PatchWidth {
    Byte,
    Word,
    Long,
}

impl PatchWidth {
    pub fn bytes(self) -> usize {
        match self {
            Self::Byte => 1,
            Self::Word => 2,
            Self::Long => 4,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PatchSemantics {
    Absolute,
    Signed,
    PcRelative { bias: u32 },
    HighMemory,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AddressContext {
    pub section: SectionId,
    pub offset: u32,
}

#[derive(Clone, Debug)]
pub struct Patch {
    pub offset: usize,
    pub width: PatchWidth,
    pub semantics: PatchSemantics,
    pub pc: AddressContext,
    pub expression: RelocationExpr,
    pub source: Provenance,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SectionRef {
    Local(SectionId),
    External(String),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UnaryOp {
    Negate,
    BitNot,
    LogicalNot,
    High,
    Low,
    BitWidth,
    TrailingZeros,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BinaryOp {
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Power,
    BitOr,
    BitAnd,
    BitXor,
    LogicalAnd,
    LogicalOr,
    Equal,
    NotEqual,
    Greater,
    Less,
    GreaterEqual,
    LessEqual,
    ShiftLeft,
    ShiftRight,
    UnsignedShiftRight,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RelocationExpr {
    Literal(i32),
    Symbol(SymbolId),
    CurrentAddress,
    Unary(UnaryOp, Box<Self>),
    Binary(BinaryOp, Box<Self>, Box<Self>),
    Bank(SymbolId),
    BankCurrent,
    BankSection(SectionRef),
    SectionSize(SectionRef),
    SectionStart(SectionRef),
    SectionTypeSize(u8),
    SectionTypeStart(u8),
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Hash)]
pub struct SourceLocation {
    pub file: String,
    pub line: u32,
    pub column: u32,
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Hash)]
pub struct Provenance {
    pub file: String,
    pub line: u32,
    pub column: u32,
    pub expansion: Vec<SourceLocation>,
}

#[derive(Clone, Debug)]
pub struct Line {
    pub offset: usize,
    pub count: usize,
    pub file: String,
    pub line: u32,
}

fn name(value: &str) -> Result<(), String> {
    if value.is_empty() || value.contains('\0') {
        return Err("Names must be nonempty and cannot contain NUL".into());
    }
    Ok(())
}

pub(crate) fn memory_region(kind: u8) -> Option<(u32, u32, i32, i32)> {
    [
        (0, 0x4000, 0, 0),
        (0x4000, 0x4000, 1, 65535),
        (0x8000, 0x2000, 0, 1),
        (0xa000, 0x2000, 0, 255),
        (0xc000, 0x1000, 0, 0),
        (0xd000, 0x1000, 1, 7),
        (0xfe00, 0xa0, 0, 0),
        (0xff80, 0x7f, 0, 0),
    ]
    .get(kind as usize)
    .copied()
}

impl Provenance {
    fn validate(&self) -> Result<(), String> {
        name(&self.file)?;
        if self.line == 0 || self.column == 0 {
            return Err("Source line and column must be positive".into());
        }
        for source in &self.expansion {
            name(&source.file)?;
            if source.line == 0 || source.column == 0 {
                return Err("Expansion source line and column must be positive".into());
            }
        }
        Ok(())
    }
}

impl Object {
    pub fn validate(&self) -> Result<(), String> {
        let mut section_names = HashSet::new();
        for section in &self.sections {
            name(&section.name)?;
            if !section_names.insert(&section.name) {
                return Err(format!("Duplicate section '{}'", section.name));
            }
            if section.kind > 7 {
                return Err("Invalid section kind".into());
            }
            let (start, size, first_bank, last_bank) = memory_region(section.kind).unwrap();
            if section.size > size
                || section.address.is_some_and(|address| {
                    address < start as i32
                        || address >= (start + size) as i32
                        || section.size > start + size - address as u32
                })
                || section
                    .bank
                    .is_some_and(|bank| !(first_bank..=last_bank).contains(&bank))
            {
                return Err("Section placement exceeds its memory region or bank range".into());
            }
            if section
                .address
                .is_some_and(|address| !(0..=65535).contains(&address))
                || section.bank.is_some_and(|bank| bank < 0)
            {
                return Err("Invalid section placement".into());
            }
            if section.align > 16
                || section.align_offset < 0
                || i64::from(section.align_offset) >= (1i64 << section.align)
            {
                return Err("Invalid section alignment".into());
            }
            if section
                .address
                .is_some_and(|address| address & ((1 << section.align) - 1) != section.align_offset)
            {
                return Err("Fixed address violates section alignment".into());
            }
            if section.kind <= 1 {
                if section.data.len() != section.size as usize {
                    return Err("ROM data length differs from section size".into());
                }
            } else if !section.data.is_empty() || !section.patches.is_empty() {
                return Err("RAM sections cannot contain initialized data or patches".into());
            }
            if let Some(source) = &section.source {
                source.validate()?;
            }
            for patch in &section.patches {
                if patch
                    .offset
                    .checked_add(patch.width.bytes())
                    .is_none_or(|end| end > section.size as usize)
                {
                    return Err("Patch outside section".into());
                }
                if self
                    .sections
                    .get(patch.pc.section.0)
                    .is_none_or(|pc| patch.pc.offset > pc.size)
                {
                    return Err("Patch PC references an invalid section or offset".into());
                }
                if patch.semantics == PatchSemantics::HighMemory && patch.width != PatchWidth::Byte
                {
                    return Err("High-memory patches must be one byte".into());
                }
                patch.source.validate()?;
                self.validate_expression(&patch.expression, 0)
                    .map_err(|error| {
                        format!(
                            "{}:{}:{}: {error}",
                            patch.source.file, patch.source.line, patch.source.column
                        )
                    })?;
            }
            for line in &section.lines {
                name(&line.file)?;
                if line.line == 0
                    || line
                        .offset
                        .checked_add(line.count)
                        .is_none_or(|end| end > section.size as usize)
                {
                    return Err("Invalid section line map".into());
                }
            }
        }
        let mut symbol_names = HashSet::new();
        for symbol in &self.symbols {
            name(&symbol.name)?;
            if !symbol_names.insert(&symbol.name) {
                return Err(format!("Duplicate symbol '{}'", symbol.name));
            }
            match symbol.definition {
                SymbolDefinition::Import if symbol.binding == SymbolBinding::Exported => {
                    return Err("An imported symbol cannot be exported".into());
                }
                SymbolDefinition::Label { section, offset }
                    if self
                        .sections
                        .get(section.0)
                        .is_none_or(|section| offset > section.size) =>
                {
                    return Err("Label references an invalid section or offset".into());
                }
                _ => {}
            }
            if let Some(source) = &symbol.source {
                source.validate()?;
            }
        }
        Ok(())
    }

    fn validate_expression(&self, expression: &RelocationExpr, depth: usize) -> Result<(), String> {
        if depth > 256 {
            return Err("Relocation expression is too deeply nested".into());
        }
        match expression {
            RelocationExpr::Symbol(id) | RelocationExpr::Bank(id) => {
                if self.symbols.get(id.0).is_none() {
                    return Err("Relocation references an invalid symbol ID".into());
                }
                if matches!(expression, RelocationExpr::Bank(_))
                    && matches!(self.symbols[id.0].definition, SymbolDefinition::Absolute(_))
                {
                    return Err("BANK requires a label or imported symbol".into());
                }
            }
            RelocationExpr::Unary(_, value) => self.validate_expression(value, depth + 1)?,
            RelocationExpr::Binary(_, left, right) => {
                self.validate_expression(left, depth + 1)?;
                self.validate_expression(right, depth + 1)?;
            }
            RelocationExpr::BankSection(reference)
            | RelocationExpr::SectionSize(reference)
            | RelocationExpr::SectionStart(reference) => match reference {
                SectionRef::Local(id) if self.sections.get(id.0).is_none() => {
                    return Err("Relocation references an invalid section ID".into());
                }
                SectionRef::External(value) => name(value)?,
                _ => {}
            },
            RelocationExpr::SectionTypeSize(kind) | RelocationExpr::SectionTypeStart(kind)
                if *kind > 7 =>
            {
                return Err("Relocation references an invalid section type".into());
            }
            _ => {}
        }
        Ok(())
    }
}
