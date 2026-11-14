use crate::model::*;

fn int(out: &mut Vec<u8>, value: i32) {
    out.extend(value.to_le_bytes());
}
fn count(out: &mut Vec<u8>, value: usize) -> Result<(), String> {
    int(
        out,
        i32::try_from(value).map_err(|_| "Object value exceeds signed 32 bits")?,
    );
    Ok(())
}
fn short(out: &mut Vec<u8>, value: usize) -> Result<(), String> {
    out.extend(
        u16::try_from(value)
            .map_err(|_| "Kobj count exceeds 65535")?
            .to_le_bytes(),
    );
    Ok(())
}
fn string(out: &mut Vec<u8>, value: &str, rgbds: bool) -> Result<(), String> {
    if rgbds {
        if value.contains('\0') {
            return Err("RGBDS strings cannot contain NUL".into());
        }
    } else {
        let mut len = u32::try_from(value.len()).map_err(|_| "String exceeds 32 bits")?;
        while len >= 128 {
            out.push((len as u8) | 128);
            len >>= 7;
        }
        out.push(len as u8);
    }
    out.extend(value.as_bytes());
    if rgbds {
        out.push(0);
    }
    Ok(())
}
fn section_kind(kind: u8) -> u8 {
    [3, 2, 1, 6, 0, 5, 7, 4][kind as usize]
}
fn width_kind(width: PatchWidth) -> u8 {
    match width {
        PatchWidth::Byte => 0,
        PatchWidth::Word => 1,
        PatchWidth::Long => 2,
    }
}

fn affine(expression: &RelocationExpr) -> Option<(SymbolId, i32)> {
    match expression {
        RelocationExpr::Symbol(id) => Some((*id, 0)),
        RelocationExpr::Binary(BinaryOp::Add, left, right)
            if matches!(**left, RelocationExpr::Literal(_)) =>
        {
            let RelocationExpr::Literal(value) = **left else {
                unreachable!()
            };
            let (symbol, addend) = affine(right)?;
            Some((symbol, addend.wrapping_add(value)))
        }
        RelocationExpr::Binary(BinaryOp::Add | BinaryOp::Subtract, left, right) => {
            let (symbol, addend) = affine(left)?;
            let RelocationExpr::Literal(value) = **right else {
                return None;
            };
            Some((
                symbol,
                if matches!(expression, RelocationExpr::Binary(BinaryOp::Add, _, _)) {
                    addend.wrapping_add(value)
                } else {
                    addend.wrapping_sub(value)
                },
            ))
        }
        _ => None,
    }
}

fn symbol_range(object: &Object, id: SymbolId, addend: i32) -> Option<(i64, i64)> {
    let (low, high) = match object.symbols[id.0].definition {
        SymbolDefinition::Import => (i64::from(i32::MIN), i64::from(i32::MAX)),
        SymbolDefinition::Absolute(value) => (i64::from(value), i64::from(value)),
        SymbolDefinition::Label { section, offset } => {
            let section = &object.sections[section.0];
            if let Some(address) = section.address {
                let value = i64::from(address) + i64::from(offset);
                (value, value)
            } else {
                let (start, size, _, _) = memory_region(section.kind)?;
                let high = i64::from(start + size) - i64::from(section.size) + i64::from(offset);
                let low = i64::from(start) + i64::from(offset);
                if low > high {
                    return None;
                }
                (low, high)
            }
        }
    };
    let low = low + i64::from(addend);
    let high = high + i64::from(addend);
    if low < i64::from(i32::MIN) || high > i64::from(i32::MAX) {
        return None;
    }
    Some((low, high))
}

fn kobj_relocation(
    object: &Object,
    section: usize,
    patch: &Patch,
) -> Result<(SymbolId, i32, i32, u8, i32), String> {
    let (inner, shift, masked) = match &patch.expression {
        RelocationExpr::Unary(UnaryOp::High, inner) if patch.width == PatchWidth::Byte => {
            (inner.as_ref(), 8, true)
        }
        RelocationExpr::Unary(UnaryOp::Low, inner) if patch.width == PatchWidth::Byte => {
            (inner.as_ref(), 0, true)
        }
        expression => (expression, 0, false),
    };
    let (symbol, addend) = affine(inner)
        .ok_or("Kobj v4 cannot represent this relocation expression; use RGBDS output")?;
    let kind = match patch.semantics {
        PatchSemantics::Absolute => {
            let (low, high) = if masked {
                (0, 255)
            } else {
                symbol_range(object, symbol, addend)
                    .ok_or("Kobj v4 cannot prove relocation range")?
            };
            let (min, max) = match patch.width {
                PatchWidth::Byte => (-128, 255),
                PatchWidth::Word => (-32768, 65535),
                PatchWidth::Long => (i64::from(i32::MIN), i64::from(i32::MAX)),
            };
            if low < min || high > max {
                return Err("Kobj v4 cannot enforce relocation range; use RGBDS output".into());
            }
            width_kind(patch.width)
        }
        PatchSemantics::Signed => {
            return Err("Kobj v4 cannot enforce signed relocation range; use RGBDS output".into());
        }
        PatchSemantics::HighMemory => {
            if masked {
                return Err("Kobj v4 cannot validate a masked high-memory relocation".into());
            }
            let (low, high) = symbol_range(object, symbol, addend)
                .ok_or("Kobj v4 cannot prove high-memory range")?;
            if low < 0xff00 || high > 0xffff {
                return Err(
                    "Kobj v4 cannot enforce high-memory relocation range; use RGBDS output".into(),
                );
            }
            0
        }
        PatchSemantics::PcRelative { .. } => {
            if patch.width != PatchWidth::Byte || patch.pc.section.0 != section || masked {
                return Err("Kobj v4 cannot encode this PC-relative relocation".into());
            }
            3
        }
    };
    let pc_after = match patch.semantics {
        PatchSemantics::PcRelative { bias } => i32::try_from(
            patch
                .pc
                .offset
                .checked_add(bias)
                .ok_or("PC offset overflow")?,
        )
        .map_err(|_| "PC offset exceeds signed 32 bits")?,
        _ => i32::try_from(patch.pc.offset).map_err(|_| "PC offset exceeds signed 32 bits")?,
    };
    Ok((symbol, addend, shift, kind, pc_after))
}

fn replace_pc(expression: &mut RelocationExpr, replacement: &RelocationExpr) {
    match expression {
        RelocationExpr::CurrentAddress => *expression = replacement.clone(),
        RelocationExpr::Unary(_, value) => replace_pc(value, replacement),
        RelocationExpr::Binary(_, left, right) => {
            replace_pc(left, replacement);
            replace_pc(right, replacement);
        }
        _ => {}
    }
}
fn contains_pc(expression: &RelocationExpr) -> bool {
    match expression {
        RelocationExpr::CurrentAddress => true,
        RelocationExpr::Unary(_, value) => contains_pc(value),
        RelocationExpr::Binary(_, left, right) => contains_pc(left) || contains_pc(right),
        _ => false,
    }
}

fn kobj_adapter(object: &Object) -> Result<Object, String> {
    let mut adapted = object.clone();
    for section in &mut adapted.sections {
        for patch in &mut section.patches {
            if !contains_pc(&patch.expression) {
                continue;
            }
            let existing = adapted
                .symbols
                .iter()
                .enumerate()
                .find_map(|(id, symbol)| match symbol.definition {
                    SymbolDefinition::Label { section, offset } if section == patch.pc.section => {
                        Some((SymbolId(id), offset))
                    }
                    _ => None,
                });
            let (symbol, offset) = existing.unwrap_or_else(|| {
                let base = format!("__koh_kobj_pc_{}", patch.pc.section.0);
                let mut name = base.clone();
                let mut suffix = 0usize;
                while adapted.symbols.iter().any(|symbol| symbol.name == name) {
                    suffix += 1;
                    name = format!("{base}_{suffix}");
                }
                let id = SymbolId(adapted.symbols.len());
                adapted.symbols.push(Symbol {
                    name,
                    binding: SymbolBinding::Local,
                    definition: SymbolDefinition::Label {
                        section: patch.pc.section,
                        offset: 0,
                    },
                    source: Some(patch.source.clone()),
                });
                (id, 0)
            });
            let delta = i32::try_from(i64::from(patch.pc.offset) - i64::from(offset))
                .map_err(|_| "Kobj PC anchor offset exceeds signed 32 bits")?;
            replace_pc(
                &mut patch.expression,
                &RelocationExpr::Binary(
                    BinaryOp::Add,
                    Box::new(RelocationExpr::Symbol(symbol)),
                    Box::new(RelocationExpr::Literal(delta)),
                ),
            );
        }
    }
    adapted.validate()?;
    Ok(adapted)
}

pub fn write_kobj(object: &Object) -> Result<Vec<u8>, String> {
    object.validate()?;
    if let Some(symbol) = object
        .symbols
        .iter()
        .find(|symbol| symbol.definition == SymbolDefinition::Import)
    {
        return Err(format!(
            "Kobj v4 cannot represent imported symbol '{}'; use RGBDS output",
            symbol.name
        ));
    }
    let object = kobj_adapter(object)?;
    let mut out = b"KOH\0\x04\x01".to_vec();
    short(&mut out, object.sections.len())?;
    for (id, section) in object.sections.iter().enumerate() {
        if section.align != 0 && section.address.is_none() {
            return Err("Kobj v4 cannot encode floating alignment; use RGBDS output".into());
        }
        string(&mut out, &section.name, false)?;
        out.push(section.kind);
        for option in [section.address, section.bank] {
            out.push(u8::from(option.is_some()));
            if let Some(value) = option {
                int(&mut out, value);
            }
        }
        count(&mut out, section.size as usize)?;
        if section.kind <= 1 {
            out.extend(&section.data);
        } else {
            let size = out
                .len()
                .checked_add(section.size as usize)
                .ok_or("Object length overflow")?;
            out.resize(size, 0);
        }
        short(&mut out, section.patches.len())?;
        for patch in &section.patches {
            let (symbol, addend, shift, kind, pc_after) = kobj_relocation(&object, id, patch)
                .map_err(|error| {
                    format!(
                        "{}:{}:{}: {error}",
                        patch.source.file, patch.source.line, patch.source.column
                    )
                })?;
            count(&mut out, patch.offset)?;
            out.push(kind);
            int(&mut out, pc_after);
            int(&mut out, 0);
            int(&mut out, 0);
            string(&mut out, &object.symbols[symbol.0].name, false)?;
            int(&mut out, addend);
            int(&mut out, shift);
        }
        count(&mut out, section.lines.len())?;
        for line in &section.lines {
            count(&mut out, line.offset)?;
            count(&mut out, line.count)?;
            string(&mut out, &line.file, false)?;
            out.extend(line.line.to_le_bytes());
        }
    }
    out.push(2);
    short(&mut out, object.symbols.len())?;
    for symbol in &object.symbols {
        string(&mut out, &symbol.name, false)?;
        let (kind, visibility, section, value) = match symbol.definition {
            SymbolDefinition::Import => (0, 2, None, 0),
            SymbolDefinition::Absolute(value) => (
                1,
                u8::from(symbol.binding == SymbolBinding::Exported),
                None,
                i64::from(value),
            ),
            SymbolDefinition::Label { section, offset } => (
                0,
                u8::from(symbol.binding == SymbolBinding::Exported),
                Some(section),
                i64::from(offset),
            ),
        };
        out.extend([kind, visibility]);
        string(
            &mut out,
            section
                .map(|id| object.sections[id.0].name.as_str())
                .unwrap_or(""),
            false,
        )?;
        out.extend(value.to_le_bytes());
    }
    out.push(255);
    Ok(out)
}

fn rpn(object: &Object, expression: &RelocationExpr, out: &mut Vec<u8>) -> Result<(), String> {
    match expression {
        RelocationExpr::Literal(value) => {
            out.push(0x80);
            int(out, *value);
        }
        RelocationExpr::Symbol(id) => {
            out.push(0x81);
            count(out, id.0)?;
        }
        RelocationExpr::CurrentAddress => {
            out.push(0x81);
            int(out, -1);
        }
        RelocationExpr::Unary(operator, value) => {
            rpn(object, value, out)?;
            out.push(match operator {
                UnaryOp::Negate => 5,
                UnaryOp::BitNot => 0x13,
                UnaryOp::LogicalNot => 0x23,
                UnaryOp::High => 0x70,
                UnaryOp::Low => 0x71,
                UnaryOp::BitWidth => 0x72,
                UnaryOp::TrailingZeros => 0x73,
            });
        }
        RelocationExpr::Binary(operator, left, right) => {
            rpn(object, left, out)?;
            rpn(object, right, out)?;
            out.push(match operator {
                BinaryOp::Add => 0,
                BinaryOp::Subtract => 1,
                BinaryOp::Multiply => 2,
                BinaryOp::Divide => 3,
                BinaryOp::Modulo => 4,
                BinaryOp::Power => 6,
                BinaryOp::BitOr => 0x10,
                BinaryOp::BitAnd => 0x11,
                BinaryOp::BitXor => 0x12,
                BinaryOp::LogicalAnd => 0x21,
                BinaryOp::LogicalOr => 0x22,
                BinaryOp::Equal => 0x30,
                BinaryOp::NotEqual => 0x31,
                BinaryOp::Greater => 0x32,
                BinaryOp::Less => 0x33,
                BinaryOp::GreaterEqual => 0x34,
                BinaryOp::LessEqual => 0x35,
                BinaryOp::ShiftLeft => 0x40,
                BinaryOp::ShiftRight => 0x41,
                BinaryOp::UnsignedShiftRight => 0x42,
            });
        }
        RelocationExpr::Bank(id) => {
            out.push(0x50);
            count(out, id.0)?;
        }
        RelocationExpr::BankCurrent => out.push(0x52),
        RelocationExpr::BankSection(reference)
        | RelocationExpr::SectionSize(reference)
        | RelocationExpr::SectionStart(reference) => {
            out.push(match expression {
                RelocationExpr::BankSection(_) => 0x51,
                RelocationExpr::SectionSize(_) => 0x53,
                RelocationExpr::SectionStart(_) => 0x54,
                _ => unreachable!(),
            });
            string(
                out,
                match reference {
                    SectionRef::Local(id) => &object.sections[id.0].name,
                    SectionRef::External(name) => name,
                },
                true,
            )?;
        }
        RelocationExpr::SectionTypeSize(kind) => out.extend([0x55, section_kind(*kind)]),
        RelocationExpr::SectionTypeStart(kind) => out.extend([0x56, section_kind(*kind)]),
    }
    Ok(())
}

#[derive(PartialEq, Eq)]
struct SourceNode {
    parent: Option<usize>,
    line: u32,
    file: String,
}
#[derive(Default)]
struct Sources {
    nodes: Vec<SourceNode>,
}
impl Sources {
    fn add(&mut self, source: &Provenance) -> usize {
        let mut parent = None;
        let mut parent_line = 0;
        for location in source
            .expansion
            .iter()
            .map(|s| (&s.file, s.line))
            .chain(std::iter::once((&source.file, source.line)))
        {
            let node = SourceNode {
                parent,
                line: parent_line,
                file: location.0.clone(),
            };
            let id = if let Some(id) = self.nodes.iter().position(|existing| *existing == node) {
                id
            } else {
                self.nodes.push(node);
                self.nodes.len() - 1
            };
            parent = Some(id);
            parent_line = location.1;
        }
        parent.unwrap()
    }
}

fn patch_header(
    out: &mut Vec<u8>,
    patch: &Patch,
    node: usize,
    kind: u8,
    pc_offset: u32,
) -> Result<(), String> {
    count(out, node)?;
    out.extend(patch.source.line.to_le_bytes());
    count(out, patch.offset)?;
    count(out, patch.pc.section.0)?;
    out.extend(pc_offset.to_le_bytes());
    out.push(kind);
    Ok(())
}

pub fn write_rgbds(object: &Object) -> Result<Vec<u8>, String> {
    object.validate()?;
    let mut sources = Sources::default();
    let mut section_nodes = Vec::new();
    let mut symbol_nodes = Vec::new();
    let mut patch_nodes = Vec::new();
    for section in &object.sections {
        section_nodes.push(
            sources.add(
                section
                    .source
                    .as_ref()
                    .ok_or("RGBDS requires section source provenance")?,
            ),
        );
        let mut nodes = Vec::new();
        for patch in &section.patches {
            nodes.push(sources.add(&patch.source));
        }
        patch_nodes.push(nodes);
    }
    for symbol in &object.symbols {
        symbol_nodes.push(if symbol.definition == SymbolDefinition::Import {
            None
        } else {
            Some(
                sources.add(
                    symbol
                        .source
                        .as_ref()
                        .ok_or("RGBDS requires defined-symbol source provenance")?,
                ),
            )
        });
    }
    let mut out = b"RGB9".to_vec();
    int(&mut out, 13);
    count(&mut out, object.symbols.len())?;
    count(&mut out, object.sections.len())?;
    count(&mut out, sources.nodes.len())?;
    for node in sources.nodes.iter().rev() {
        if let Some(parent) = node.parent {
            count(&mut out, parent)?;
        } else {
            int(&mut out, -1);
        }
        out.extend(node.line.to_le_bytes());
        out.push(1);
        string(&mut out, &node.file, true)?;
    }
    for (id, symbol) in object.symbols.iter().enumerate() {
        string(&mut out, &symbol.name, true)?;
        if symbol.definition == SymbolDefinition::Import {
            out.push(1);
            continue;
        }
        out.push(if symbol.binding == SymbolBinding::Exported {
            2
        } else {
            0
        });
        count(&mut out, symbol_nodes[id].unwrap())?;
        out.extend(symbol.source.as_ref().unwrap().line.to_le_bytes());
        match symbol.definition {
            SymbolDefinition::Absolute(value) => {
                int(&mut out, -1);
                int(&mut out, value);
            }
            SymbolDefinition::Label { section, offset } => {
                count(&mut out, section.0)?;
                out.extend(offset.to_le_bytes());
            }
            SymbolDefinition::Import => unreachable!(),
        }
    }
    let mut assertions = Vec::new();
    for (id, section) in object.sections.iter().enumerate() {
        string(&mut out, &section.name, true)?;
        count(&mut out, section_nodes[id])?;
        out.extend(section.source.as_ref().unwrap().line.to_le_bytes());
        count(&mut out, section.size as usize)?;
        out.push(section_kind(section.kind));
        int(&mut out, section.address.unwrap_or(-1));
        int(&mut out, section.bank.unwrap_or(-1));
        out.push(section.align);
        int(&mut out, section.align_offset);
        if section.kind > 1 {
            continue;
        }
        out.extend(&section.data);
        count(&mut out, section.patches.len())?;
        for (patch_id, patch) in section.patches.iter().enumerate() {
            let (kind, pc_offset) = match patch.semantics {
                PatchSemantics::PcRelative { bias } => {
                    if patch.width != PatchWidth::Byte {
                        return Err("RGBDS supports only byte PC-relative relocations".into());
                    }
                    let offset = i64::from(patch.pc.offset) + i64::from(bias) - 2;
                    (
                        3,
                        u32::try_from(offset)
                            .map_err(|_| "RGBDS PC-relative bias cannot be represented")?,
                    )
                }
                _ => (width_kind(patch.width), patch.pc.offset),
            };
            let mut lowered_expression = patch.expression.clone();
            if let PatchSemantics::PcRelative { bias } = patch.semantics {
                if bias != 2 {
                    let correction = i32::try_from(2i64 - i64::from(bias))
                        .map_err(|_| "RGBDS PC-relative bias exceeds expression range")?;
                    replace_pc(
                        &mut lowered_expression,
                        &RelocationExpr::Binary(
                            BinaryOp::Add,
                            Box::new(RelocationExpr::CurrentAddress),
                            Box::new(RelocationExpr::Literal(correction)),
                        ),
                    );
                }
            }
            let mut expression = Vec::new();
            rpn(object, &lowered_expression, &mut expression)?;
            if patch.semantics == PatchSemantics::HighMemory {
                expression.push(0x60);
            }
            patch_header(&mut out, patch, patch_nodes[id][patch_id], kind, pc_offset)?;
            count(&mut out, expression.len())?;
            out.extend(expression);
            if patch.semantics == PatchSemantics::Signed && patch.width != PatchWidth::Long {
                let bits = patch.width.bytes() * 8;
                let min = -(1i32 << (bits - 1));
                let max = (1i32 << (bits - 1)) - 1;
                let condition = RelocationExpr::Binary(
                    BinaryOp::LogicalAnd,
                    Box::new(RelocationExpr::Binary(
                        BinaryOp::GreaterEqual,
                        Box::new(patch.expression.clone()),
                        Box::new(RelocationExpr::Literal(min)),
                    )),
                    Box::new(RelocationExpr::Binary(
                        BinaryOp::LessEqual,
                        Box::new(patch.expression.clone()),
                        Box::new(RelocationExpr::Literal(max)),
                    )),
                );
                patch_header(
                    &mut assertions,
                    patch,
                    patch_nodes[id][patch_id],
                    1,
                    patch.pc.offset,
                )?;
                let mut rpn_condition = Vec::new();
                rpn(object, &condition, &mut rpn_condition)?;
                count(&mut assertions, rpn_condition.len())?;
                assertions.extend(rpn_condition);
                string(&mut assertions, "Signed relocation is out of range", true)?;
            }
        }
    }
    count(
        &mut out,
        object
            .sections
            .iter()
            .flat_map(|section| &section.patches)
            .filter(|patch| {
                patch.semantics == PatchSemantics::Signed && patch.width != PatchWidth::Long
            })
            .count(),
    )?;
    out.extend(assertions);
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn source() -> Provenance {
        Provenance {
            file: "main.asm".into(),
            line: 7,
            column: 3,
            expansion: vec![],
        }
    }
    fn fixture() -> Object {
        Object {
            sections: vec![Section {
                name: "Code".into(),
                kind: 0,
                size: 4,
                data: vec![0; 4],
                source: Some(source()),
                patches: vec![Patch {
                    offset: 0,
                    width: PatchWidth::Word,
                    semantics: PatchSemantics::Absolute,
                    pc: AddressContext {
                        section: SectionId(0),
                        offset: 0,
                    },
                    expression: RelocationExpr::Symbol(SymbolId(0)),
                    source: source(),
                }],
                lines: vec![Line {
                    offset: 0,
                    count: 4,
                    file: "main.asm".into(),
                    line: 7,
                }],
                ..Section::default()
            }],
            symbols: vec![Symbol {
                name: "target".into(),
                binding: SymbolBinding::Exported,
                definition: SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: 3,
                },
                source: Some(source()),
            }],
        }
    }
    struct Reader {
        bytes: Vec<u8>,
        pos: usize,
    }
    impl Reader {
        fn take(&mut self, count: usize) -> Vec<u8> {
            let value = self.bytes[self.pos..self.pos + count].to_vec();
            self.pos += count;
            value
        }
        fn byte(&mut self) -> u8 {
            self.take(1)[0]
        }
        fn int(&mut self) -> i32 {
            i32::from_le_bytes(self.take(4).try_into().unwrap())
        }
        fn short(&mut self) -> u16 {
            u16::from_le_bytes(self.take(2).try_into().unwrap())
        }
        fn zstr(&mut self) -> String {
            let mut value = Vec::new();
            loop {
                let byte = self.byte();
                if byte == 0 {
                    break;
                }
                value.push(byte);
            }
            String::from_utf8(value).unwrap()
        }
        fn netstr(&mut self) -> String {
            let mut len = 0;
            let mut shift = 0;
            loop {
                let byte = self.byte();
                len |= usize::from(byte & 127) << shift;
                if byte < 128 {
                    break;
                }
                shift += 7;
            }
            String::from_utf8(self.take(len)).unwrap()
        }
    }
    #[derive(Debug)]
    struct ReadPatch {
        node: i32,
        line: i32,
        offset: i32,
        pc_section: i32,
        pc_offset: i32,
        kind: u8,
        rpn: Vec<u8>,
    }
    fn read_patch(reader: &mut Reader) -> ReadPatch {
        let node = reader.int();
        let line = reader.int();
        let offset = reader.int();
        let pc_section = reader.int();
        let pc_offset = reader.int();
        let kind = reader.byte();
        let size = reader.int() as usize;
        ReadPatch {
            node,
            line,
            offset,
            pc_section,
            pc_offset,
            kind,
            rpn: reader.take(size),
        }
    }
    fn rgbds(object: &Object) -> (Vec<(i32, i32, String)>, Vec<ReadPatch>, Vec<ReadPatch>) {
        let mut reader = Reader {
            bytes: write_rgbds(object).unwrap(),
            pos: 0,
        };
        assert_eq!(reader.take(4), b"RGB9");
        assert_eq!(reader.int(), 13);
        let symbols = reader.int();
        let sections = reader.int();
        let nodes = reader.int();
        let mut sources = Vec::new();
        for _ in 0..nodes {
            let parent = reader.int();
            let line = reader.int();
            assert_eq!(reader.byte(), 1);
            sources.push((parent, line, reader.zstr()));
        }
        sources.reverse();
        for _ in 0..symbols {
            reader.zstr();
            if reader.byte() != 1 {
                reader.take(16);
            }
        }
        let mut patches = Vec::new();
        for _ in 0..sections {
            reader.zstr();
            reader.take(8);
            let size = reader.int() as usize;
            let kind = reader.byte();
            reader.take(13);
            if kind == 2 || kind == 3 {
                reader.take(size);
                let count = reader.int();
                for _ in 0..count {
                    patches.push(read_patch(&mut reader));
                }
            }
        }
        let mut assertions = Vec::new();
        for _ in 0..reader.int() {
            assertions.push(read_patch(&mut reader));
            assert!(!reader.zstr().is_empty());
        }
        assert_eq!(reader.pos, reader.bytes.len());
        (sources, patches, assertions)
    }

    #[test]
    fn kobj_v4_keeps_patch_metadata_line_map_and_int64_symbol_values() {
        let object = fixture();
        let mut r = Reader {
            bytes: write_kobj(&object).unwrap(),
            pos: 0,
        };
        assert_eq!(r.take(6), b"KOH\0\x04\x01");
        assert_eq!(r.short(), 1);
        assert_eq!(r.netstr(), "Code");
        assert_eq!(r.take(3), [0, 0, 0]);
        assert_eq!(r.int(), 4);
        assert_eq!(r.take(4), [0; 4]);
        assert_eq!(r.short(), 1);
        assert_eq!(r.int(), 0);
        assert_eq!(r.byte(), 1);
        assert_eq!(r.int(), 0);
        assert_eq!(r.take(8), [0; 8]);
        assert_eq!(r.netstr(), "target");
        assert_eq!(r.int(), 0);
        assert_eq!(r.int(), 0);
        assert_eq!(r.int(), 1);
        assert_eq!(r.int(), 0);
        assert_eq!(r.int(), 4);
        assert_eq!(r.netstr(), "main.asm");
        assert_eq!(r.int(), 7);
        assert_eq!(r.byte(), 2);
        assert_eq!(r.short(), 1);
        assert_eq!(r.netstr(), "target");
        assert_eq!(r.take(2), [0, 1]);
        assert_eq!(r.netstr(), "Code");
        assert_eq!(r.take(8), 3i64.to_le_bytes());
        assert_eq!(r.byte(), 255);
        assert_eq!(r.pos, r.bytes.len());
    }

    #[test]
    fn rgbds_keeps_expansion_context_and_patch_location() {
        let mut object = fixture();
        object.sections[0].patches[0].source.expansion = vec![
            SourceLocation {
                file: "outer.asm".into(),
                line: 12,
                column: 1,
            },
            SourceLocation {
                file: "macro.asm".into(),
                line: 18,
                column: 4,
            },
        ];
        let (sources, patches, assertions) = rgbds(&object);
        assert!(assertions.is_empty());
        let patch = &patches[0];
        assert_eq!(
            (
                patch.line,
                patch.offset,
                patch.pc_section,
                patch.pc_offset,
                patch.kind
            ),
            (7, 0, 0, 0, 1)
        );
        assert_eq!(patch.rpn, [0x81, 0, 0, 0, 0]);
        let leaf = &sources[patch.node as usize];
        assert_eq!(leaf.2, "main.asm");
        assert_eq!(leaf.1, 18);
        let parent = &sources[leaf.0 as usize];
        assert_eq!(parent.2, "macro.asm");
        assert_eq!(parent.1, 12);
        assert_eq!(sources[parent.0 as usize].2, "outer.asm");
    }

    #[test]
    fn rgbds_signed_patches_have_link_time_range_assertions() {
        let mut object = fixture();
        object.sections[0].patches[0].width = PatchWidth::Byte;
        object.sections[0].patches[0].semantics = PatchSemantics::Signed;
        object.symbols[0].definition = SymbolDefinition::Import;
        object.symbols[0].binding = SymbolBinding::Local;
        let (_, patches, assertions) = rgbds(&object);
        assert_eq!(patches[0].kind, 0);
        assert_eq!(assertions.len(), 1);
        assert_eq!(assertions[0].kind, 1);
        assert_eq!(
            assertions[0].rpn,
            [
                0x81, 0, 0, 0, 0, 0x80, 128, 255, 255, 255, 0x34, 0x81, 0, 0, 0, 0, 0x80, 127, 0,
                0, 0, 0x35, 0x21
            ]
        );
        assert!(write_kobj(&object).is_err());
        object.symbols[0].definition = SymbolDefinition::Absolute(7);
        assert!(write_kobj(&object).unwrap_err().contains("signed"));
    }

    #[test]
    fn rgbds_pc_context_is_independent_of_patch_section() {
        let mut object = fixture();
        object.sections.push(Section {
            name: "RAM".into(),
            kind: 4,
            size: 8,
            source: Some(source()),
            ..Section::default()
        });
        let patch = &mut object.sections[0].patches[0];
        patch.width = PatchWidth::Byte;
        patch.semantics = PatchSemantics::PcRelative { bias: 4 };
        patch.pc = AddressContext {
            section: SectionId(1),
            offset: 1,
        };
        patch.expression = RelocationExpr::CurrentAddress;
        let (_, patches, _) = rgbds(&object);
        assert_eq!(
            (patches[0].pc_section, patches[0].pc_offset, patches[0].kind),
            (1, 3, 3)
        );
        assert_eq!(
            patches[0].rpn,
            [0x81, 255, 255, 255, 255, 0x80, 254, 255, 255, 255, 0]
        );
        assert!(write_kobj(&object).is_err());
    }

    #[test]
    fn kobj_current_address_uses_collision_safe_adapter_only_anchor() {
        let mut object = fixture();
        object.symbols.clear();
        object.symbols.push(Symbol {
            name: "__koh_kobj_pc_0".into(),
            binding: SymbolBinding::Local,
            definition: SymbolDefinition::Absolute(9),
            source: Some(source()),
        });
        object.sections[0].patches[0].expression = RelocationExpr::CurrentAddress;
        let adapted = kobj_adapter(&object).unwrap();
        assert_eq!(object.symbols.len(), 1);
        assert_eq!(adapted.symbols.len(), 2);
        assert_eq!(adapted.symbols[1].name, "__koh_kobj_pc_0_1");
        assert_eq!(adapted.symbols[1].source, Some(source()));
        assert!(write_kobj(&object).is_ok());
        assert_eq!(
            kobj_adapter(&object).unwrap().symbols[1].name,
            adapted.symbols[1].name
        );
    }

    #[test]
    fn kobj_derives_relocations_without_changing_high_low_arithmetic() {
        let mut object = fixture();
        object.sections[0].patches[0].width = PatchWidth::Byte;
        let symbol = RelocationExpr::Symbol(SymbolId(0));
        let high = RelocationExpr::Unary(UnaryOp::High, Box::new(symbol.clone()));
        object.sections[0].patches[0].expression = RelocationExpr::Binary(
            BinaryOp::Add,
            Box::new(high.clone()),
            Box::new(RelocationExpr::Literal(1)),
        );
        assert!(write_kobj(&object).is_err());
        assert!(write_rgbds(&object).is_ok());
        object.sections[0].patches[0].expression = RelocationExpr::Unary(
            UnaryOp::High,
            Box::new(RelocationExpr::Binary(
                BinaryOp::Add,
                Box::new(symbol.clone()),
                Box::new(RelocationExpr::Literal(1)),
            )),
        );
        assert_eq!(
            kobj_relocation(&object, 0, &object.sections[0].patches[0]).unwrap(),
            (SymbolId(0), 1, 8, 0, 0)
        );
        object.sections[0].patches[0].width = PatchWidth::Word;
        object.sections[0].patches[0].expression =
            RelocationExpr::Unary(UnaryOp::Low, Box::new(symbol));
        assert!(write_kobj(&object).is_err());
        assert!(write_rgbds(&object).is_ok());
    }

    #[test]
    fn high_memory_semantics_are_validated_or_encoded() {
        let mut object = fixture();
        object.sections.push(Section {
            name: "HRAM".into(),
            kind: 7,
            size: 4,
            source: Some(source()),
            ..Section::default()
        });
        object.symbols[0].definition = SymbolDefinition::Label {
            section: SectionId(1),
            offset: 0,
        };
        let patch = &mut object.sections[0].patches[0];
        patch.width = PatchWidth::Byte;
        patch.semantics = PatchSemantics::HighMemory;
        assert!(write_kobj(&object).is_ok());
        let (_, patches, _) = rgbds(&object);
        assert_eq!(patches[0].rpn, [0x81, 0, 0, 0, 0, 0x60]);
        object.symbols[0].definition = SymbolDefinition::Import;
        object.symbols[0].binding = SymbolBinding::Local;
        assert!(write_kobj(&object).is_err());
        assert!(write_rgbds(&object).is_ok());
    }

    #[test]
    fn kobj_rejects_imported_symbols_even_without_a_patch() {
        let mut object = fixture();
        object.sections[0].patches.clear();
        object.symbols[0].definition = SymbolDefinition::Import;
        object.symbols[0].binding = SymbolBinding::Local;
        assert!(
            write_kobj(&object)
                .unwrap_err()
                .contains("imported symbol 'target'")
        );
        assert!(write_rgbds(&object).is_ok());
    }

    #[test]
    fn section_placement_checks_memory_and_bank_boundaries() {
        for kind in 0..8 {
            let (start, size, first, last) = memory_region(kind).unwrap();
            let mut object = Object {
                sections: vec![Section {
                    name: "region".into(),
                    kind,
                    size,
                    data: if kind <= 1 {
                        vec![0; size as usize]
                    } else {
                        vec![]
                    },
                    address: Some(start as i32),
                    bank: Some(first),
                    source: Some(source()),
                    ..Section::default()
                }],
                symbols: vec![],
            };
            assert!(object.validate().is_ok());
            object.sections[0].bank = Some(last + 1);
            assert!(object.validate().is_err());
            object.sections[0].bank = Some(first);
            object.sections[0].address = Some((start + 1) as i32);
            assert!(object.validate().is_err());
            object.sections[0].address = Some(start as i32);
            object.sections[0].size = size + 1;
            assert!(object.validate().is_err());
        }
    }

    #[test]
    fn malformed_symbol_section_data_and_provenance_graphs_are_rejected() {
        let mut object = fixture();
        object.sections[0].patches[0].expression = RelocationExpr::Symbol(SymbolId(7));
        assert!(object.validate().is_err());
        assert!(write_rgbds(&object).is_err());
        object = fixture();
        object.sections[0].patches[0].expression =
            RelocationExpr::SectionSize(SectionRef::Local(SectionId(3)));
        assert!(object.validate().is_err());
        object = fixture();
        object.symbols[0].definition = SymbolDefinition::Label {
            section: SectionId(5),
            offset: 0,
        };
        assert!(object.validate().is_err());
        object = fixture();
        object.sections[0].size = 3;
        assert!(object.validate().is_err());
        object = fixture();
        object.sections[0].patches[0].offset = 3;
        assert!(object.validate().is_err());
        object = fixture();
        object.sections[0].patches[0].source.line = 0;
        assert!(object.validate().is_err());
        object = fixture();
        object.sections[0].source = None;
        assert!(write_rgbds(&object).is_err());
        object = fixture();
        object.symbols[0].source = None;
        assert!(write_rgbds(&object).is_err());
        object = fixture();
        object.sections[0].patches[0].expression = RelocationExpr::Bank(SymbolId(0));
        object.symbols[0].definition = SymbolDefinition::Absolute(1);
        assert!(object.validate().is_err());
        object = fixture();
        object.sections[0].kind = 4;
        assert!(object.validate().is_err());
    }

    #[test]
    fn every_typed_operator_and_section_query_has_exact_rpn() {
        let object = fixture();
        for (operator, code) in [
            (BinaryOp::Add, 0),
            (BinaryOp::Subtract, 1),
            (BinaryOp::Multiply, 2),
            (BinaryOp::Divide, 3),
            (BinaryOp::Modulo, 4),
            (BinaryOp::Power, 6),
            (BinaryOp::BitOr, 0x10),
            (BinaryOp::BitAnd, 0x11),
            (BinaryOp::BitXor, 0x12),
            (BinaryOp::LogicalAnd, 0x21),
            (BinaryOp::LogicalOr, 0x22),
            (BinaryOp::Equal, 0x30),
            (BinaryOp::NotEqual, 0x31),
            (BinaryOp::Greater, 0x32),
            (BinaryOp::Less, 0x33),
            (BinaryOp::GreaterEqual, 0x34),
            (BinaryOp::LessEqual, 0x35),
            (BinaryOp::ShiftLeft, 0x40),
            (BinaryOp::ShiftRight, 0x41),
            (BinaryOp::UnsignedShiftRight, 0x42),
        ] {
            let mut bytes = Vec::new();
            rpn(
                &object,
                &RelocationExpr::Binary(
                    operator,
                    Box::new(RelocationExpr::Literal(1)),
                    Box::new(RelocationExpr::Symbol(SymbolId(0))),
                ),
                &mut bytes,
            )
            .unwrap();
            assert_eq!(bytes, [0x80, 1, 0, 0, 0, 0x81, 0, 0, 0, 0, code]);
        }
        for (operator, code) in [
            (UnaryOp::Negate, 5),
            (UnaryOp::BitNot, 0x13),
            (UnaryOp::LogicalNot, 0x23),
            (UnaryOp::High, 0x70),
            (UnaryOp::Low, 0x71),
            (UnaryOp::BitWidth, 0x72),
            (UnaryOp::TrailingZeros, 0x73),
        ] {
            let mut bytes = Vec::new();
            rpn(
                &object,
                &RelocationExpr::Unary(operator, Box::new(RelocationExpr::Literal(1))),
                &mut bytes,
            )
            .unwrap();
            assert_eq!(bytes, [0x80, 1, 0, 0, 0, code]);
        }
        for (expression, expected) in [
            (RelocationExpr::Bank(SymbolId(0)), vec![0x50, 0, 0, 0, 0]),
            (RelocationExpr::BankCurrent, vec![0x52]),
            (
                RelocationExpr::BankSection(SectionRef::Local(SectionId(0))),
                b"\x51Code\0".to_vec(),
            ),
            (
                RelocationExpr::SectionSize(SectionRef::External("Other".into())),
                b"\x53Other\0".to_vec(),
            ),
            (
                RelocationExpr::SectionStart(SectionRef::Local(SectionId(0))),
                b"\x54Code\0".to_vec(),
            ),
            (RelocationExpr::SectionTypeSize(0), vec![0x55, 3]),
            (RelocationExpr::SectionTypeStart(7), vec![0x56, 4]),
        ] {
            let mut bytes = Vec::new();
            rpn(&object, &expression, &mut bytes).unwrap();
            assert_eq!(bytes, expected);
        }
    }

    #[test]
    fn kobj_alignment_limits_and_utf8_lengths_are_explicit() {
        let mut object = fixture();
        object.sections[0].align = 4;
        assert!(write_kobj(&object).is_err());
        assert!(write_rgbds(&object).is_ok());
        object.sections[0].address = Some(0x100);
        assert!(write_kobj(&object).is_ok());
        object.sections[0].align_offset = 1;
        assert!(object.validate().is_err());
        let value = "é".repeat(70);
        let mut bytes = Vec::new();
        string(&mut bytes, &value, false).unwrap();
        assert_eq!(&bytes[..2], &[0x8c, 1]);
        let mut reader = Reader { bytes, pos: 0 };
        assert_eq!(reader.netstr(), value);
    }
}
