use crate::{ir::*, resolve::Definition};
use koh_objects::*;
use std::collections::{BTreeMap, BTreeSet};

pub fn symbol(identity: Definition) -> String {
    format!("__koh_a{}_m{:08x}", identity.assembly, identity.token)
}

pub fn static_symbol(identity: Definition) -> String {
    format!(
        "__koh_static_a{}_f{:08x}",
        identity.assembly, identity.token
    )
}

struct Code {
    data: Vec<u8>,
    symbols: Vec<Symbol>,
    patches: Vec<Patch>,
    prefix: String,
    il_offset: u32,
    overflow: bool,
}

impl Code {
    fn bytes(&mut self, bytes: &[u8]) {
        if self.data.len() + bytes.len() > 0x4000 {
            self.overflow = true;
        }
        if !self.overflow {
            self.data.extend_from_slice(bytes);
        }
    }

    fn check_size(&self) -> Result<(), String> {
        if self.overflow {
            Err(format!(
                "{}: emitted ROM0 function exceeds 16 KiB; optimization/banked code required",
                self.prefix
            ))
        } else {
            Ok(())
        }
    }

    fn label(&mut self) -> SymbolId {
        let id = SymbolId(self.symbols.len());
        self.symbols.push(Symbol {
            name: format!("{}.L{}", self.prefix, id.0),
            binding: SymbolBinding::Local,
            definition: SymbolDefinition::Import,
            source: None,
        });
        id
    }

    fn import(&mut self, name: String) -> SymbolId {
        if let Some(index) = self.symbols.iter().position(|s| s.name == name) {
            return SymbolId(index);
        }
        let id = SymbolId(self.symbols.len());
        self.symbols.push(Symbol {
            name,
            binding: SymbolBinding::Local,
            definition: SymbolDefinition::Import,
            source: None,
        });
        id
    }

    fn guard(&mut self, bytes: u16) {
        let negative = 0u16.wrapping_sub(bytes);
        self.bytes(&[
            0xf8,
            0,
            0x11,
            negative as u8,
            (negative >> 8) as u8,
            0x19,
            0x7c,
            0xfe,
            0xc1,
        ]);
        self.jump(0xda, SymbolId(1));
    }

    fn mark(&mut self, id: SymbolId) {
        self.symbols[id.0].definition = SymbolDefinition::Label {
            section: SectionId(0),
            offset: self.data.len() as u32,
        };
    }

    fn mark_il(&mut self, offset: u32) {
        self.il_offset = offset;
        self.symbols.push(Symbol {
            name: format!("{}.IL_{offset:04x}.{}", self.prefix, self.symbols.len()),
            binding: SymbolBinding::Local,
            definition: SymbolDefinition::Label {
                section: SectionId(0),
                offset: self.data.len() as u32,
            },
            source: None,
        });
    }

    fn jump(&mut self, opcode: u8, target: SymbolId) {
        self.bytes(&[opcode]);
        let offset = self.data.len();
        self.bytes(&[0, 0]);
        self.patches.push(Patch {
            offset,
            width: PatchWidth::Word,
            semantics: PatchSemantics::Absolute,
            pc: AddressContext {
                section: SectionId(0),
                offset: offset as u32,
            },
            expression: RelocationExpr::Symbol(target),
            source: Provenance {
                file: format!("<cil:{}>", self.prefix),
                line: self.il_offset.saturating_add(1),
                column: 1,
                expansion: vec![],
            },
        });
    }

    fn address(&mut self, offset: u16) {
        if offset <= 127 {
            self.bytes(&[0xf8, offset as u8]);
        } else {
            self.bytes(&[0xf8, 0, 0x11, offset as u8, (offset >> 8) as u8, 0x19]);
        }
    }

    fn load_a(&mut self, offset: u16) {
        self.bytes(&[0xf5]);
        self.address(offset + 2);
        self.bytes(&[0x46, 0xf1, 0x78]);
    }

    fn load_b(&mut self, offset: u16) {
        self.bytes(&[0xf5]);
        self.address(offset + 2);
        self.bytes(&[0x46, 0xf1]);
    }

    fn store_a(&mut self, offset: u16) {
        self.bytes(&[0xf5]);
        self.address(offset + 2);
        self.bytes(&[0xf1, 0x77]);
    }

    fn copy(&mut self, source: u16, destination: u16, width: u16) {
        if source == destination {
            return;
        }
        self.bytes(&[0xf5]);
        self.address(source + 2);
        self.bytes(&[0x44, 0x4d]);
        self.address(destination + 2);
        self.bytes(&[0x54, 0x5d, 0x60, 0x69, 0xf1]);
        for byte in 0..width {
            self.bytes(&[0x2a, 0x12]);
            if byte + 1 < width {
                self.bytes(&[0x13]);
            }
        }
    }

    fn indirect(&mut self, address: u16) {
        self.load_a(address);
        self.bytes(&[0x4f]);
        self.load_a(address + 1);
        self.bytes(&[0x47]);
    }

    fn constant(&mut self, destination: u16, width: u16, bits: u64) {
        self.bytes(&[0xf5]);
        self.address(destination + 2);
        self.bytes(&[0xf1]);
        for byte in 0..width {
            self.bytes(&[0x36, (bits >> (byte * 8)) as u8]);
            if byte + 1 < width {
                self.bytes(&[0x23]);
            }
        }
    }

    fn adjust_sp(&mut self, mut amount: i32) {
        while amount != 0 {
            let part = amount.clamp(-128, 127);
            self.bytes(&[0xe8, part as i8 as u8]);
            amount -= part;
        }
    }

    fn arithmetic(&mut self, left: u16, right: u16, destination: u16, width: u16, opcode: u8) {
        self.bytes(&[0xaf]);
        for byte in 0..width {
            self.load_a(left + byte);
            self.load_b(right + byte);
            self.bytes(&[opcode]);
            self.store_a(destination + byte);
        }
    }

    fn negate(&mut self, source: u16, destination: u16, width: u16) {
        self.bytes(&[0x37]);
        for byte in 0..width {
            self.load_a(source + byte);
            self.bytes(&[0x2f, 0xce, 0]);
            self.store_a(destination + byte);
        }
    }

    fn shift_one(&mut self, slot: u16, width: u16, left: bool, signed: bool, carry_in: bool) {
        for index in 0..width {
            let byte = if left { index } else { width - 1 - index };
            self.load_a(slot + byte);
            if index == 0 && !carry_in {
                self.bytes(&[
                    0xcb,
                    if left {
                        0x27
                    } else if signed {
                        0x2f
                    } else {
                        0x3f
                    },
                ]);
            } else {
                self.bytes(&[if left { 0x17 } else { 0x1f }]);
            }
            self.store_a(slot + byte);
        }
    }

    fn compare(
        &mut self,
        mut left: u16,
        mut right: u16,
        destination: u16,
        width: u16,
        op: Comparison,
    ) {
        let yes = self.label();
        let no = self.label();
        let end = self.label();
        if matches!(op, Comparison::GreaterSigned | Comparison::GreaterUnsigned) {
            std::mem::swap(&mut left, &mut right);
        }
        if matches!(op, Comparison::LessSigned | Comparison::GreaterSigned) {
            let same = self.label();
            self.load_a(left + width - 1);
            self.load_b(right + width - 1);
            self.bytes(&[0xa8, 0xe6, 0x80]);
            self.jump(0xca, same);
            self.load_a(left + width - 1);
            self.bytes(&[0xe6, 0x80]);
            self.jump(0xc2, yes);
            self.jump(0xc3, no);
            self.mark(same);
        }
        for byte in (0..width).rev() {
            self.load_a(left + byte);
            self.load_b(right + byte);
            self.bytes(&[0xb8]);
            if op != Comparison::Equal {
                self.jump(0xda, yes);
            }
            self.jump(0xc2, no);
        }
        self.jump(0xc3, if op == Comparison::Equal { yes } else { no });
        self.mark(yes);
        self.constant(destination, 4, 1);
        self.jump(0xc3, end);
        self.mark(no);
        self.constant(destination, 4, 0);
        self.mark(end);
    }

    fn multiply(&mut self, left: u16, right: u16, destination: u16, width: u16, temp: u16) {
        self.copy(left, temp, width);
        self.copy(right, temp + 8, width);
        self.constant(destination, width, 0);
        self.bytes(&[0x0e, (width * 8) as u8]);
        let start = self.label();
        let skip = self.label();
        self.mark(start);
        self.load_a(temp + 8);
        self.bytes(&[0xe6, 1]);
        self.jump(0xca, skip);
        self.arithmetic(destination, temp, destination, width, 0x88);
        self.mark(skip);
        self.shift_one(temp, width, true, false, false);
        self.shift_one(temp + 8, width, false, false, false);
        self.bytes(&[0x0d]);
        self.jump(0xc2, start);
    }

    fn divide(
        &mut self,
        left: u16,
        right: u16,
        destination: u16,
        width: u16,
        temp: u16,
        signed: bool,
        remainder: bool,
    ) {
        let quotient = temp;
        let divisor = temp + 8;
        let rest = temp + 16;
        let sign = temp + 24;
        self.copy(left, quotient, width);
        self.copy(right, divisor, width);
        self.bytes(&[0x0e, 0]);
        for byte in 0..width {
            self.load_a(divisor + byte);
            self.bytes(&[0xb1, 0x4f]);
        }
        self.bytes(&[0x79, 0xb7]);
        self.jump(0xca, SymbolId(2));
        if signed {
            if !remainder {
                let normal = self.label();
                for byte in 0..width {
                    self.load_a(quotient + byte);
                    self.bytes(&[0xfe, if byte == width - 1 { 0x80 } else { 0 }]);
                    self.jump(0xc2, normal);
                }
                for byte in 0..width {
                    self.load_a(divisor + byte);
                    self.bytes(&[0xfe, 0xff]);
                    self.jump(0xc2, normal);
                }
                self.jump(0xc3, SymbolId(2));
                self.mark(normal);
            }
            self.load_a(quotient + width - 1);
            self.bytes(&[0xe6, 0x80]);
            self.store_a(sign);
            if !remainder {
                self.load_b(divisor + width - 1);
                self.bytes(&[0xa8, 0xe6, 0x80]);
                self.store_a(sign);
            }
            for slot in [quotient, divisor] {
                let positive = self.label();
                self.load_a(slot + width - 1);
                self.bytes(&[0xe6, 0x80]);
                self.jump(0xca, positive);
                self.negate(slot, slot, width);
                self.mark(positive);
            }
        }
        self.constant(rest, width, 0);
        self.bytes(&[0x0e, (width * 8) as u8]);
        let start = self.label();
        let subtract = self.label();
        let skip = self.label();
        self.mark(start);
        self.shift_one(quotient, width, true, false, false);
        self.shift_one(rest, width, true, false, true);
        self.jump(0xda, subtract);
        for byte in (0..width).rev() {
            self.load_a(rest + byte);
            self.load_b(divisor + byte);
            self.bytes(&[0xb8]);
            self.jump(0xda, skip);
            self.jump(0xc2, subtract);
        }
        self.mark(subtract);
        self.arithmetic(rest, divisor, rest, width, 0x98);
        self.load_a(quotient);
        self.bytes(&[0xf6, 1]);
        self.store_a(quotient);
        self.mark(skip);
        self.bytes(&[0x0d]);
        self.jump(0xc2, start);
        let result = if remainder { rest } else { quotient };
        self.copy(result, destination, width);
        if signed {
            let positive = self.label();
            self.load_a(sign);
            self.bytes(&[0xb7]);
            self.jump(0xca, positive);
            self.negate(destination, destination, width);
            self.mark(positive);
        }
    }
}

struct Layout {
    values: BTreeMap<u32, (u16, Type)>,
    locals: Vec<u16>,
    edge: u16,
    arithmetic: u16,
    size: u16,
}

impl Layout {
    fn new(function: &Function) -> Result<Self, String> {
        let types: BTreeMap<_, _> = function
            .blocks
            .iter()
            .flat_map(|b| {
                b.parameters
                    .iter()
                    .copied()
                    .chain(b.instructions.iter().filter_map(|i| i.result))
            })
            .collect();
        let mut offset = 0usize;
        let mut locals = Vec::new();
        for ty in &function.locals {
            locals.push(offset as u16);
            offset += usize::from(ty.bits() / 8);
        }
        let mut owners = BTreeMap::new();
        let mut shared = BTreeSet::new();
        for (block, body) in function.blocks.iter().enumerate() {
            for &(id, _) in &body.parameters {
                shared.insert(id);
                owners.insert(id, block);
            }
            for instruction in &body.instructions {
                if let Some((id, _)) = instruction.result {
                    owners.insert(id, block);
                }
            }
        }
        let mut uses = vec![BTreeMap::new(); function.blocks.len()];
        for (block, body) in function.blocks.iter().enumerate() {
            let mut record = |id, position| {
                uses[block].insert(id, position);
                if owners[&id] != block {
                    shared.insert(id);
                }
            };
            for (position, instruction) in body.instructions.iter().enumerate() {
                for id in instruction.op.operands() {
                    record(id, position);
                }
            }
            let end = body.instructions.len();
            match &body.terminator.kind {
                Control::Return(value) => {
                    if let Some(id) = value {
                        record(*id, end);
                    }
                }
                Control::Jump(edge) => {
                    for &id in &edge.arguments {
                        record(id, end);
                    }
                }
                Control::Branch { condition, yes, no } => {
                    record(*condition, end);
                    for &id in yes.arguments.iter().chain(&no.arguments) {
                        record(id, end);
                    }
                }
            }
        }
        let mut values = BTreeMap::new();
        for (&id, &ty) in &types {
            if !shared.contains(&id) {
                continue;
            }
            if offset > 0xf00 {
                return Err("SM83 frame exceeds WRAM0 stack capacity".into());
            }
            values.insert(id, (offset as u16, ty));
            offset += usize::from(ty.bits() / 8);
        }
        let base = offset;
        for (block, body) in function.blocks.iter().enumerate() {
            let mut extent = base;
            let mut free: BTreeMap<usize, Vec<usize>> = BTreeMap::new();
            let mut active = Vec::new();
            for (position, instruction) in body.instructions.iter().enumerate() {
                active.retain(|&(end, slot, bytes)| {
                    if end < position {
                        free.entry(bytes).or_default().push(slot);
                        false
                    } else {
                        true
                    }
                });
                if let Some((id, ty)) = instruction.result {
                    if shared.contains(&id) {
                        continue;
                    }
                    let bytes = usize::from(ty.bits() / 8);
                    let slot = free.entry(bytes).or_default().pop().unwrap_or_else(|| {
                        let slot = extent;
                        extent += bytes;
                        slot
                    });
                    if extent > 0xf00 {
                        return Err("SM83 frame exceeds WRAM0 stack capacity".into());
                    }
                    values.insert(id, (slot as u16, ty));
                    active.push((*uses[block].get(&id).unwrap_or(&position), slot, bytes));
                }
            }
            offset = offset.max(extent);
        }
        let edge = offset;
        offset += function
            .blocks
            .iter()
            .map(|b| {
                b.parameters
                    .iter()
                    .map(|p| usize::from(p.1.bits() / 8))
                    .sum::<usize>()
            })
            .max()
            .unwrap_or(0);
        let arithmetic = offset;
        offset += 25;
        let arguments: usize = function
            .parameters
            .iter()
            .map(|ty| usize::from(ty.bits() / 8))
            .sum();
        let returns = function
            .returns
            .map(|ty| usize::from(ty.bits() / 8))
            .unwrap_or(0);
        if offset + arguments + returns + 4 > 0xf00 {
            return Err("SM83 frame and call storage exceed WRAM0 stack capacity".into());
        }
        Ok(Self {
            values,
            locals,
            edge: edge as u16,
            arithmetic: arithmetic as u16,
            size: offset as u16,
        })
    }
}

pub fn emit(function: &Function) -> Result<Object, String> {
    verify(function)?;
    let layout = Layout::new(function)?;
    let name = symbol(function.identity);
    let mut code = Code {
        data: vec![],
        symbols: vec![
            Symbol {
                name: name.clone(),
                binding: SymbolBinding::Exported,
                definition: SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: 0,
                },
                source: None,
            },
            Symbol {
                name: "__koh_trap_stack".into(),
                binding: SymbolBinding::Local,
                definition: SymbolDefinition::Import,
                source: None,
            },
            Symbol {
                name: "__koh_trap_arithmetic".into(),
                binding: SymbolBinding::Local,
                definition: SymbolDefinition::Import,
                source: None,
            },
        ],
        patches: vec![],
        prefix: name.clone(),
        il_offset: 0,
        overflow: false,
    };
    code.guard(layout.size + 2);
    code.adjust_sp(-i32::from(layout.size));
    let return_width = function
        .returns
        .map(|ty| u16::from(ty.bits() / 8))
        .unwrap_or(0);
    let mut argument = layout.size + 2 + return_width;
    for &(id, ty) in &function.blocks[0].parameters {
        let width = u16::from(ty.bits() / 8);
        code.copy(argument, layout.values[&id].0, width);
        argument += width;
    }
    let labels: Vec<_> = function.blocks.iter().map(|_| code.label()).collect();
    code.check_size()?;
    for (index, block) in function.blocks.iter().enumerate() {
        code.mark(labels[index]);
        for instruction in &block.instructions {
            code.mark_il(instruction.il_offset);
            let output = instruction.result.map(|r| layout.values[&r.0]);
            let slot = |id| layout.values[&id].0;
            let width = output.map(|p| u16::from(p.1.bits() / 8)).unwrap_or(0);
            let destination = output.map(|p| p.0).unwrap_or(0);
            match instruction.op {
                Op::StaticAddress { field, .. } => {
                    let target = code.import(static_symbol(field));
                    code.jump(0x21, target);
                    code.bytes(&[0x44, 0x4d, 0x79]);
                    code.store_a(destination);
                    code.bytes(&[0x78]);
                    code.store_a(destination + 1);
                }
                Op::LoadMemory {
                    address,
                    bits,
                    signed,
                } => {
                    let copied = u16::from(bits / 8);
                    code.indirect(slot(address));
                    code.address(destination);
                    code.bytes(&[0x50, 0x59]);
                    for byte in 0..copied {
                        code.bytes(&[0x1a, 0x22]);
                        if byte + 1 < copied {
                            code.bytes(&[0x13]);
                        }
                    }
                    if copied < width {
                        if signed {
                            code.bytes(&[0x87, 0x9f]);
                        } else {
                            code.bytes(&[0xaf]);
                        }
                        for _ in copied..width {
                            code.bytes(&[0x22]);
                        }
                    }
                }
                Op::StoreMemory {
                    address,
                    value,
                    bits,
                } => {
                    code.indirect(slot(address));
                    code.address(slot(value));
                    code.bytes(&[0x50, 0x59]);
                    for byte in 0..u16::from(bits / 8) {
                        code.bytes(&[0x2a, 0x12]);
                        if byte + 1 < u16::from(bits / 8) {
                            code.bytes(&[0x13]);
                        }
                    }
                }
                Op::Call {
                    target,
                    ref signature,
                    ref arguments,
                } => {
                    let size: usize = signature
                        .parameters
                        .iter()
                        .map(|ty| usize::from(ty.bits() / 8))
                        .sum::<usize>()
                        + usize::from(width);
                    if size + 2 > 0xf00 {
                        return Err(format!(
                            "{name} IL_{:04x}: call storage exceeds stack capacity",
                            instruction.il_offset
                        ));
                    }
                    let size = size as u16;
                    code.guard(size + 2);
                    code.adjust_sp(-i32::from(size));
                    let mut offset = width;
                    for (&value, &ty) in arguments.iter().zip(&signature.parameters) {
                        let bytes = u16::from(ty.bits() / 8);
                        code.copy(slot(value) + size, offset, bytes);
                        offset += bytes;
                    }
                    let callee = code.import(symbol(target));
                    code.jump(0xcd, callee);
                    if width != 0 {
                        code.copy(0, destination + size, width);
                    }
                    code.adjust_sp(i32::from(size));
                }
                Op::Constant(bits) => code.constant(destination, width, bits),
                Op::LoadLocal(local) => {
                    code.copy(layout.locals[local as usize], destination, width)
                }
                Op::StoreLocal { local, value } => code.copy(
                    slot(value),
                    layout.locals[local as usize],
                    u16::from(function.locals[local as usize].bits() / 8),
                ),
                Op::Negate(value) => code.negate(slot(value), destination, width),
                Op::Not(value) => {
                    for byte in 0..width {
                        code.load_a(slot(value) + byte);
                        code.bytes(&[0x2f]);
                        code.store_a(destination + byte);
                    }
                }
                Op::Convert {
                    value,
                    bits,
                    signed,
                } => {
                    let copied = width
                        .min(u16::from(bits / 8))
                        .min(u16::from(layout.values[&value].1.bits() / 8));
                    code.copy(slot(value), destination, copied);
                    if copied < width {
                        if signed {
                            code.load_a(slot(value) + copied - 1);
                            code.bytes(&[0x87, 0x9f]);
                        } else {
                            code.bytes(&[0xaf]);
                        }
                        for byte in copied..width {
                            code.store_a(destination + byte);
                        }
                    }
                }
                Op::Compare { op, left, right } => code.compare(
                    slot(left),
                    slot(right),
                    destination,
                    u16::from(layout.values[&left].1.bits() / 8),
                    op,
                ),
                Op::Binary { op, left, right } => match op {
                    Binary::Add | Binary::Subtract | Binary::And | Binary::Or | Binary::Xor => {
                        let opcode = match op {
                            Binary::Add => 0x88,
                            Binary::Subtract => 0x98,
                            Binary::And => 0xa0,
                            Binary::Or => 0xb0,
                            Binary::Xor => 0xa8,
                            _ => unreachable!(),
                        };
                        code.arithmetic(slot(left), slot(right), destination, width, opcode);
                    }
                    Binary::Multiply => code.multiply(
                        slot(left),
                        slot(right),
                        destination,
                        width,
                        layout.arithmetic,
                    ),
                    Binary::DivideSigned
                    | Binary::DivideUnsigned
                    | Binary::RemainderSigned
                    | Binary::RemainderUnsigned => code.divide(
                        slot(left),
                        slot(right),
                        destination,
                        width,
                        layout.arithmetic,
                        matches!(op, Binary::DivideSigned | Binary::RemainderSigned),
                        matches!(op, Binary::RemainderSigned | Binary::RemainderUnsigned),
                    ),
                    Binary::ShiftLeft | Binary::ShiftRightSigned | Binary::ShiftRightUnsigned => {
                        code.copy(slot(left), destination, width);
                        code.load_a(slot(right));
                        code.bytes(&[0xe6, (width * 8 - 1) as u8, 0x4f]);
                        let start = code.label();
                        let end = code.label();
                        code.jump(0xca, end);
                        code.mark(start);
                        code.shift_one(
                            destination,
                            width,
                            op == Binary::ShiftLeft,
                            op == Binary::ShiftRightSigned,
                            false,
                        );
                        code.bytes(&[0x0d]);
                        code.jump(0xc2, start);
                        code.mark(end);
                    }
                },
            }
            code.check_size()?;
        }
        let transfer = |code: &mut Code, edge: &Edge| {
            let mut offset = layout.edge;
            for &value in &edge.arguments {
                let (source, ty) = layout.values[&value];
                let width = u16::from(ty.bits() / 8);
                code.copy(source, offset, width);
                offset += width;
            }
            offset = layout.edge;
            for &(id, ty) in &function.blocks[edge.block].parameters {
                let width = u16::from(ty.bits() / 8);
                code.copy(offset, layout.values[&id].0, width);
                offset += width;
            }
            code.jump(0xc3, labels[edge.block]);
        };
        code.mark_il(block.terminator.il_offset);
        match &block.terminator.kind {
            Control::Jump(edge) => transfer(&mut code, edge),
            Control::Branch { condition, yes, no } => {
                code.bytes(&[0x0e, 0]);
                for byte in 0..4 {
                    code.load_a(layout.values[condition].0 + byte);
                    code.bytes(&[0xb1, 0x4f]);
                }
                code.bytes(&[0x79, 0xb7]);
                let truth = code.label();
                code.jump(0xc2, truth);
                transfer(&mut code, no);
                code.mark(truth);
                transfer(&mut code, yes);
            }
            Control::Return(value) => {
                if let Some(value) = value {
                    code.copy(layout.values[value].0, layout.size + 2, return_width);
                }
                code.adjust_sp(i32::from(layout.size));
                code.bytes(&[0xc9]);
            }
        }
        code.check_size()?;
    }
    code.check_size()?;
    let object = Object {
        sections: vec![
            Section {
                name: format!(".text.{name}"),
                kind: 0,
                size: code.data.len() as u32,
                data: code.data,
                patches: code.patches,
                ..Default::default()
            },
            Section {
                name: ".koh.stack".into(),
                kind: 4,
                combination: SectionCombination::Union,
                address: Some(0xc100),
                bank: Some(0),
                size: 0xf00,
                ..Default::default()
            },
        ],
        symbols: code.symbols,
    };
    object.validate()?;
    Ok(object)
}
