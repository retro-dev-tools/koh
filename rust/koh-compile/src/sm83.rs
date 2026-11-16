use crate::{ir::*, resolve::Definition};
use koh_objects::*;
use std::collections::{BTreeMap, BTreeSet};
mod ranges;

#[derive(Clone, Copy)]
struct Slot {
    offset: u16,
    bytes: u16,
}

pub fn symbol(identity: Definition) -> String {
    format!("__koh_a{}_m{:08x}", identity.assembly, identity.token)
}

pub fn static_symbol(identity: Definition) -> String {
    format!(
        "__koh_static_a{}_f{:08x}",
        identity.assembly, identity.token
    )
}

struct Code<'a> {
    data: Vec<u8>,
    readonly: Vec<u8>,
    literals: BTreeMap<Vec<u8>, SymbolId>,
    symbols: Vec<Symbol>,
    patches: Vec<Patch>,
    prefix: String,
    il_offset: u32,
    overflow: bool,
    pdb: Option<&'a crate::pdb::PortablePdb>,
    method: u32,
    source: Option<(&'a str, &'a crate::pdb::Position)>,
    lines: Vec<Line>,
    debug_bytes: usize,
    debug_overflow: bool,
}

impl Code<'_> {
    fn debug_space(&mut self, bytes: usize) -> bool {
        if let Some(total) = self
            .debug_bytes
            .checked_add(bytes)
            .filter(|&n| n <= 4 * 1024 * 1024)
        {
            self.debug_bytes = total;
            true
        } else {
            self.debug_overflow = true;
            false
        }
    }

    fn provenance(&mut self) -> Provenance {
        if let Some((file, position)) = self.source {
            if self.debug_space(file.len() + 128) {
                return Provenance {
                    file: file.into(),
                    line: position.line,
                    column: position.column.max(1),
                    expansion: vec![],
                };
            }
        }
        Provenance {
            file: format!("<cil:{}>", self.prefix),
            line: self.il_offset.saturating_add(1),
            column: 1,
            expansion: vec![],
        }
    }
    fn literal(&mut self, bytes: &[u8]) -> Result<SymbolId, String> {
        if let Some(&symbol) = self.literals.get(bytes) {
            return Ok(symbol);
        }
        if self.readonly.len() + bytes.len() > 0x4000 {
            return Err("Function initializer data exceeds ROM0 capacity".into());
        }
        let id = SymbolId(self.symbols.len());
        self.symbols.push(Symbol {
            name: format!("{}.D{}", self.prefix, id.0),
            binding: SymbolBinding::Local,
            definition: SymbolDefinition::Label {
                section: SectionId(2),
                offset: self.readonly.len() as u32,
            },
            source: None,
        });
        self.readonly.extend_from_slice(bytes);
        self.literals.insert(bytes.to_vec(), id);
        Ok(id)
    }
    fn bytes(&mut self, bytes: &[u8]) {
        if self.data.len() + bytes.len() > 0x4000 {
            self.overflow = true;
        }
        if !self.overflow {
            let offset = self.data.len();
            self.data.extend_from_slice(bytes);
            if let Some((file, position)) = self.source.filter(|_| !bytes.is_empty()) {
                if let Some(last) = self.lines.last_mut().filter(|line| {
                    line.offset + line.count == offset
                        && line.file == file
                        && line.line == position.line
                }) {
                    last.count += bytes.len();
                } else if self.debug_space(file.len() + 128) {
                    self.lines.push(Line {
                        offset,
                        count: bytes.len(),
                        file: file.into(),
                        line: position.line,
                        expansion: vec![],
                    });
                }
            }
        }
    }

    fn check_size(&self) -> Result<(), String> {
        if self.debug_overflow {
            return Err(format!(
                "{}: source metadata exceeds 4 MiB function limit",
                self.prefix
            ));
        }
        if self.overflow {
            Err(format!(
                "{}: emitted function exceeds 16 KiB ROM bank capacity",
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
        self.source = self
            .pdb
            .and_then(|pdb| pdb.location(self.method, offset))
            .filter(|(file, position)| !file.is_empty() && position.line != 0);
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
        let source = self.provenance();
        self.patches.push(Patch {
            offset,
            width: PatchWidth::Word,
            semantics: PatchSemantics::Absolute,
            pc: AddressContext {
                section: SectionId(0),
                offset: offset as u32,
            },
            expression: RelocationExpr::Symbol(target),
            source,
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
        if source == destination || width == 0 {
            return;
        }
        if width == 1 {
            self.address(source);
            self.bytes(&[0x7e]);
            self.address(destination);
            self.bytes(&[0x77]);
            return;
        }
        if width == 2 {
            self.indirect(source);
            self.save_word(destination);
            return;
        }
        self.address(source);
        self.bytes(&[0x44, 0x4d]);
        self.address(destination);
        self.bytes(&[0x54, 0x5d, 0x60, 0x69]);
        self.copy_registers(width);
    }

    fn copy_registers(&mut self, width: u16) {
        if width > 8 {
            self.bytes(&[
                0x01,
                width as u8,
                (width >> 8) as u8,
                0x2a,
                0x12,
                0x13,
                0x0b,
                0x78,
                0xb1,
                0x20,
                0xf8,
            ]);
            return;
        }
        for byte in 0..width {
            self.bytes(&[0x2a, 0x12]);
            if byte + 1 < width {
                self.bytes(&[0x13]);
            }
        }
    }

    fn extend(&mut self, source: Slot, destination: u16, width: u16) {
        let copied = source.bytes.min(width);
        self.copy(source.offset, destination, copied);
        if copied < width {
            self.constant(destination + copied, width - copied, 0);
        }
    }

    fn value_a(&mut self, value: Slot, byte: u16) {
        if byte < value.bytes {
            self.load_a(value.offset + byte);
        } else {
            self.bytes(&[0x3e, 0]);
        }
    }

    fn indirect(&mut self, address: u16) {
        self.address(address);
        self.bytes(&[0x4e, 0x23, 0x46]);
    }

    fn check_reference(&mut self, ty: Type) {
        if matches!(
            ty,
            Type::ByRef(_)
                | Type::StructRef(_)
                | Type::Array(_, _)
                | Type::String
                | Type::Delegate(_)
                | Type::Null
                | Type::Object(_)
                | Type::ReferenceCell(_)
        ) {
            self.bytes(&[0x78, 0xb1]);
            let target = self.import("__koh_trap_null".into());
            self.jump(0xca, target);
        }
    }

    fn save_word(&mut self, destination: u16) {
        self.address(destination);
        self.bytes(&[0x71, 0x23, 0x70]);
    }

    fn heap_bank(&mut self, saved: u16) {
        self.bytes(&[0xf0, 0x70]);
        self.address(saved);
        self.bytes(&[0x77]);
        self.bytes(&[0x3e, 1, 0xe0, 0x70]);
    }

    fn restore_bank(&mut self, saved: u16) {
        self.address(saved);
        self.bytes(&[0x7e, 0xe0, 0x70]);
    }

    fn object_class(&mut self, saved: u16) {
        self.heap_bank(saved);
        self.bytes(&[0x0a, 0x5f, 0x03, 0x0a, 0x57]);
        self.restore_bank(saved);
    }

    fn pins_active(&mut self, pins: &BTreeMap<u32, u16>) {
        self.bytes(&[0x0e, 0]);
        for &flag in pins.values() {
            self.load_a(flag);
            self.bytes(&[0xb1, 0x4f]);
        }
        self.bytes(&[0x79, 0xb7]);
    }

    fn release_pin(&mut self, flag: u16, saved: u16, pins: &BTreeMap<u32, u16>) {
        let done = self.label();
        self.load_a(flag);
        self.bytes(&[0xb7]);
        self.jump(0xca, done);
        self.constant(flag, 1, 0);
        self.pins_active(pins);
        self.jump(0xc2, done);
        self.restore_bank(saved);
        self.mark(done);
    }

    fn store_pin(
        &mut self,
        source: u16,
        local: u16,
        flag: u16,
        saved: u16,
        pins: &BTreeMap<u32, u16>,
        clear: bool,
    ) {
        if clear {
            self.indirect(source);
            self.bytes(&[0x78, 0xb1]);
            let fault = self.import("__koh_trap_pin".into());
            self.jump(0xc2, fault);
        }
        self.copy(source, local, 2);
        self.release_pin(flag, saved, pins);
        let done = self.label();
        self.load_a(local + 1);
        self.bytes(&[0xfe, 0xd0]);
        self.jump(0xda, done);
        self.bytes(&[0xfe, 0xe0]);
        self.jump(0xd2, done);
        self.pins_active(pins);
        let mapped = self.label();
        self.jump(0xc2, mapped);
        self.bytes(&[0xf0, 0x70]);
        self.store_a(saved);
        self.mark(mapped);
        self.constant(flag, 1, 1);
        self.bytes(&[0x3e, 1, 0xe0, 0x70]);
        self.mark(done);
    }

    fn check_high(&mut self, slot: u16, width: u16, fault: SymbolId) {
        if width <= 2 {
            return;
        }
        self.bytes(&[0x0e, 0]);
        for byte in 2..width {
            self.load_a(slot + byte);
            self.bytes(&[0xb1, 0x4f]);
        }
        self.bytes(&[0x79, 0xb7]);
        self.jump(0xc2, fault);
    }

    fn array_header(&mut self, array: u16, saved: u16) {
        self.indirect(array);
        self.check_reference(Type::Null);
        self.heap_bank(saved);
        self.bytes(&[0x60, 0x69, 0x2a, 0x4f, 0x46]);
    }

    fn initialize_array(
        &mut self,
        array: u16,
        stride: u16,
        header: u16,
        bytes: &[u8],
        temp: u16,
    ) -> Result<(), String> {
        let literal = self.literal(bytes)?;
        self.array_header(array, temp + 24);
        self.save_word(temp);
        let fault = self.import("__koh_trap_initializer".into());
        self.below_constant(temp, (bytes.len() as u16 / stride) + 1, fault);
        for _ in 0..stride.trailing_zeros() {
            self.shift_one(temp, 2, true, false, false);
        }
        self.indirect(temp);
        self.address(array);
        self.bytes(&[0x5e, 0x23, 0x56]);
        for _ in 0..header {
            self.bytes(&[0x13]);
        }
        self.jump(0x21, literal);
        self.bytes(&[0x78, 0xb1]);
        let done = self.label();
        self.jump(0xca, done);
        self.bytes(&[0x2a, 0x12, 0x13, 0x0b, 0x78, 0xb1, 0x20, 0xf8]);
        self.mark(done);
        self.restore_bank(temp + 24);
        Ok(())
    }

    fn below_constant(&mut self, value: u16, limit: u16, fault: SymbolId) {
        self.indirect(value);
        self.bytes(&[0x79, 0xd6, limit as u8, 0x78, 0xde, (limit >> 8) as u8]);
        self.jump(0xd2, fault);
    }

    fn below_word(&mut self, value: u16, limit: u16, fault: SymbolId) {
        self.indirect(value);
        self.address(limit);
        self.bytes(&[0x79, 0x96, 0x23, 0x78, 0x9e]);
        self.jump(0xd2, fault);
    }

    fn scale_word(&mut self, value: u16, stride: u16) {
        if stride.is_power_of_two() {
            for _ in 0..stride.trailing_zeros() {
                self.shift_one(value, 2, true, false, false);
            }
        } else {
            self.indirect(value);
            self.bytes(&[0x50, 0x59, 0x21, 0, 0]);
            for bit in (0..=stride.ilog2()).rev() {
                self.bytes(&[0x29]);
                if stride & (1 << bit) != 0 {
                    self.bytes(&[0x19]);
                }
            }
            self.bytes(&[0x44, 0x4d]);
            self.save_word(value);
        }
    }

    fn new_array(&mut self, length: u16, destination: u16, temp: u16, aligned: bool) {
        let fault = self.import("__koh_trap_allocation".into());
        self.copy(temp + 8, temp, 2);
        self.copy(temp + 20, temp + 2, 2);
        self.arithmetic(temp, temp + 2, temp, 2, 0x88);
        let cursor = self.import("__koh_heap_cursor".into());
        self.jump(0x21, cursor);
        self.bytes(&[0x2a, 0x4f, 0x46]);
        self.save_word(temp + 2);
        if aligned {
            self.copy(temp + 20, temp + 14, 2);
            self.arithmetic(temp + 2, temp + 14, temp + 2, 2, 0x88);
            self.arithmetic(temp + 2, temp + 10, temp + 2, 2, 0x88);
            for byte in 0..2 {
                self.load_a(temp + 10 + byte);
                self.bytes(&[0x2f, 0x4f]);
                self.load_a(temp + 2 + byte);
                self.bytes(&[0xa1]);
                self.store_a(temp + 2 + byte);
            }
            self.arithmetic(temp + 2, temp + 14, temp + 2, 2, 0x98);
        }
        self.arithmetic(temp, temp + 2, temp + 4, 2, 0x88);
        self.jump(0xda, fault);
        self.below_constant(temp + 4, 4097, fault);
        let raw_used = self.import("__koh_raw_used".into());
        self.jump(0x21, raw_used);
        self.bytes(&[0x2a, 0x4f, 0x46]);
        self.save_word(temp + 16);
        self.arithmetic(temp + 4, temp + 16, temp + 18, 2, 0x88);
        self.below_constant(temp + 18, 4097, fault);
        self.indirect(temp + 4);
        self.jump(0x21, cursor);
        self.bytes(&[0x79, 0x22, 0x78, 0x77]);
        self.indirect(temp + 2);
        let heap = self.import("__koh_heap".into());
        self.jump(0x21, heap);
        self.bytes(&[0x50, 0x59, 0x19, 0x44, 0x4d]);
        self.save_word(destination);
        self.heap_bank(temp + 24);
        self.indirect(temp);
        self.address(destination);
        self.bytes(&[0x5e, 0x23, 0x56, 0x62, 0x6b]);
        self.bytes(&[0x36, 0, 0x23, 0x0b, 0x78, 0xb1, 0x20, 0xf8]);
        self.indirect(destination);
        self.address(length);
        self.bytes(&[0x50, 0x59, 0x2a, 0x12, 0x13, 0x2a, 0x12]);
        self.restore_bank(temp + 24);
    }

    fn allocate_raw(&mut self, size: Slot, destination: u16, temp: u16) {
        let fault = self.import("__koh_trap_allocation".into());
        self.check_high(size.offset, size.bytes, fault);
        self.extend(size, temp, 2);
        self.below_constant(temp, 4097, fault);
        let raw_used = self.import("__koh_raw_used".into());
        self.jump(0x21, raw_used);
        self.bytes(&[0x2a, 0x4f, 0x46]);
        self.save_word(temp + 2);
        self.arithmetic(temp, temp + 2, temp + 4, 2, 0x88);
        self.below_constant(temp + 4, 4097, fault);
        let managed = self.import("__koh_heap_cursor".into());
        self.jump(0x21, managed);
        self.bytes(&[0x2a, 0x4f, 0x46]);
        self.save_word(temp + 6);
        self.arithmetic(temp + 4, temp + 6, temp + 8, 2, 0x88);
        self.below_constant(temp + 8, 4097, fault);
        self.indirect(temp + 4);
        self.jump(0x21, raw_used);
        self.bytes(&[0x79, 0x22, 0x78, 0x77]);
        self.constant(temp + 6, 2, 4096);
        self.arithmetic(temp + 6, temp + 4, temp + 6, 2, 0x98);
        self.indirect(temp + 6);
        let heap = self.import("__koh_heap".into());
        self.jump(0x21, heap);
        self.bytes(&[0x50, 0x59, 0x19, 0x44, 0x4d]);
        self.save_word(destination);
    }

    fn constant(&mut self, destination: u16, width: u16, bits: u64) {
        if width > 8 {
            if bits != 0 {
                self.constant(destination, 8, bits);
                self.constant(destination + 8, width - 8, 0);
            } else {
                self.address(destination);
                self.bytes(&[
                    0x01,
                    width as u8,
                    (width >> 8) as u8,
                    0x36,
                    0,
                    0x23,
                    0x0b,
                    0x78,
                    0xb1,
                    0x20,
                    0xf8,
                ]);
            }
            return;
        }
        self.address(destination);
        for byte in 0..width {
            self.bytes(&[
                0x36,
                if byte < 8 {
                    (bits >> (byte * 8)) as u8
                } else {
                    0
                },
            ]);
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

    fn arithmetic_values(
        &mut self,
        left: Slot,
        right: Slot,
        destination: u16,
        width: u16,
        opcode: u8,
    ) {
        self.address(left.offset);
        self.bytes(&[0x44, 0x4d]);
        self.address(destination);
        self.bytes(&[0x54, 0x5d]);
        if right.offset > 127 {
            self.bytes(&[0xd5]);
            self.address(right.offset + 2);
            self.bytes(&[0xd1]);
        } else {
            self.address(right.offset);
        }
        self.bytes(&[0xaf]);
        for byte in 0..width {
            self.bytes(if byte < left.bytes {
                &[0x0a]
            } else {
                &[0x3e, 0]
            });
            if byte < right.bytes {
                self.bytes(&[opcode + 6]);
            } else {
                self.bytes(&[opcode + 0x46, 0]);
            }
            self.bytes(&[0x12]);
            if byte + 1 < width {
                self.bytes(&[0x03, 0x23, 0x13]);
            }
        }
    }

    fn negate(&mut self, source: u16, destination: u16, width: u16) {
        self.negate_value(
            Slot {
                offset: source,
                bytes: width,
            },
            destination,
            width,
        );
    }

    fn negate_value(&mut self, source: Slot, destination: u16, width: u16) {
        self.bytes(&[0x37]);
        for byte in 0..width {
            self.value_a(source, byte);
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

    fn shift_constant(
        &mut self,
        source: u16,
        destination: u16,
        width: u16,
        count: u8,
        left: bool,
        signed: bool,
    ) {
        self.copy(source, destination, width);
        if count != 0 {
            self.bytes(&[0x0e, count]);
            let start = self.label();
            self.mark(start);
            self.shift_one(destination, width, left, signed, false);
            self.bytes(&[0x0d]);
            self.jump(0xc2, start);
        }
    }

    fn equal_zero(&mut self, source: u16, destination: u16, width: u16) {
        self.address(source);
        self.bytes(&[0x0e, 0]);
        for _ in 0..width {
            self.bytes(&[0x2a, 0xb1, 0x4f]);
        }
        self.bytes(&[0x79, 0xb7, 0x3e, 0, 0x20, 1, 0x3c]);
        self.store_a(destination);
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
        self.address(left + width - 1);
        self.bytes(&[0x44, 0x4d]);
        self.address(right + width - 1);
        self.bytes(&[0x54, 0x5d, 0x60, 0x69]);
        for byte in (0..width).rev() {
            self.bytes(&[0x1a, 0x47, 0x7e, 0xb8]);
            if op != Comparison::Equal {
                self.jump(0xda, yes);
            }
            self.jump(0xc2, no);
            if byte != 0 {
                self.bytes(&[0x2b, 0x1b]);
            }
        }
        self.jump(0xc3, if op == Comparison::Equal { yes } else { no });
        self.mark(yes);
        self.constant(destination, 1, 1);
        self.jump(0xc3, end);
        self.mark(no);
        self.constant(destination, 1, 0);
        self.mark(end);
    }

    fn multiply(&mut self, width: u16, temp: u16) {
        let destination = temp + 16;
        let wide = self.label();
        let finished = self.label();
        let sign = temp + 24;
        if width >= 2 {
            self.load_a(temp + width - 1);
            self.load_b(temp + 8 + width - 1);
            self.bytes(&[0xa8, 0xe6, 0x80]);
            self.store_a(sign);
            for slot in [temp, temp + 8] {
                let positive = self.label();
                self.load_a(slot + width - 1);
                self.bytes(&[0xe6, 0x80]);
                self.jump(0xca, positive);
                self.negate(slot, slot, width);
                self.mark(positive);
            }
            for slot in [temp, temp + 8] {
                for byte in 1..width {
                    self.load_a(slot + byte);
                    self.bytes(&[0xb7]);
                    self.jump(0xc2, wide);
                }
            }
            self.indirect(temp);
            self.bytes(&[0xc5]);
            self.indirect(temp + 10);
            self.bytes(&[0xd1, 0x41, 0x21, 0, 0, 0x0e, 8]);
            let bit = self.label();
            let skip = self.label();
            self.mark(bit);
            self.bytes(&[0xcb, 0x38]);
            self.jump(0xd2, skip);
            self.bytes(&[0x19]);
            self.mark(skip);
            self.bytes(&[0xcb, 0x23, 0xcb, 0x12, 0x0d]);
            self.jump(0xc2, bit);
            self.bytes(&[0x44, 0x4d]);
            self.save_word(destination);
            self.constant(destination + 2, width - 2, 0);
            self.jump(0xc3, finished);
        }
        self.mark(wide);
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
        self.mark(finished);
        if width >= 2 {
            let positive = self.label();
            self.load_a(sign);
            self.bytes(&[0xb7]);
            self.jump(0xca, positive);
            self.negate(destination, destination, width);
            self.mark(positive);
        }
    }

    fn divide(
        &mut self,
        left: Slot,
        right: Slot,
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
        self.extend(left, quotient, width);
        self.extend(right, divisor, width);
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
        let wide = self.label();
        let finished = self.label();
        if width >= 2 {
            for slot in [quotient, divisor] {
                for byte in 2..width {
                    self.load_a(slot + byte);
                    self.bytes(&[0xb7]);
                    self.jump(0xc2, wide);
                }
            }
            self.indirect(quotient);
            self.bytes(&[0xc5]);
            self.indirect(divisor + 2);
            self.bytes(&[0xe1, 0x11, 0, 0, 0x3e, 16]);
            let bit = self.label();
            let subtract_word = self.label();
            let next = self.label();
            self.mark(bit);
            self.bytes(&[0xf5, 0x29, 0xcb, 0x13, 0xcb, 0x12]);
            self.jump(0xda, subtract_word);
            self.bytes(&[0x7b, 0x91, 0x7a, 0x98]);
            self.jump(0xda, next);
            self.mark(subtract_word);
            self.bytes(&[0x7b, 0x91, 0x5f, 0x7a, 0x98, 0x57, 0x2c]);
            self.mark(next);
            self.bytes(&[0xf1, 0x3d]);
            self.jump(0xc2, bit);
            self.bytes(if remainder {
                &[0x42, 0x4b]
            } else {
                &[0x44, 0x4d]
            });
            self.save_word(destination);
            self.constant(destination + 2, width - 2, 0);
            self.jump(0xc3, finished);
        }
        self.mark(wide);
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
        self.mark(finished);
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
    values: BTreeMap<u32, (u16, Type, u16)>,
    allocations: BTreeMap<u32, (u16, u16)>,
    locals: Vec<u16>,
    local_widths: Vec<u16>,
    pins: BTreeMap<u32, u16>,
    pin_bank: u16,
    edge: u16,
    arithmetic: u16,
    size: u16,
}

impl Layout {
    fn new(function: &Function, cookie: usize) -> Result<Self, String> {
        let widths = ranges::analyze(function);
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
        let mut large_locals = Vec::new();
        for (index, &width) in widths.locals.iter().enumerate() {
            locals.push(offset as u16);
            if width > 8 {
                large_locals.push((index, width));
            } else {
                offset += usize::from(width);
            }
        }
        let mut allocations = BTreeMap::new();
        for (id, bytes) in stack_allocations(function)? {
            if offset + usize::from(bytes) > 0xf00 {
                return Err("Stack allocation and locals exceed WRAM0 frame capacity".into());
            }
            allocations.insert(id, (offset as u16, bytes));
            offset += usize::from(bytes);
        }
        let pinned: BTreeSet<_> = function
            .blocks
            .iter()
            .flat_map(|b| &b.instructions)
            .filter_map(|i| match i.op {
                Op::StorePinned { local, .. } => Some(local),
                _ => None,
            })
            .collect();
        let pin_bank = offset as u16;
        if !pinned.is_empty() {
            offset += 1;
        }
        let pins = pinned
            .into_iter()
            .map(|local| {
                let flag = offset as u16;
                offset += 1;
                (local, flag)
            })
            .collect();
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
                Control::TypeFault => {}
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
                Control::Switch {
                    selector,
                    cases,
                    default,
                } => {
                    record(*selector, end);
                    for edge in cases.iter().chain(std::iter::once(default)) {
                        for &id in &edge.arguments {
                            record(id, end);
                        }
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
            values.insert(id, (offset as u16, ty, widths.values[&id]));
            offset += usize::from(widths.values[&id]);
        }
        let base = offset;
        for (block, body) in function.blocks.iter().enumerate() {
            let mut extent = base;
            let mut free: BTreeMap<usize, Vec<usize>> = BTreeMap::new();
            let mut active: Vec<(usize, usize, usize)> = Vec::new();
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
                    let bytes = usize::from(widths.values[&id]);
                    let reused = if let Op::Convert { value, .. } = instruction.op {
                        if !shared.contains(&value) && uses[block].get(&value) == Some(&position) {
                            active
                                .iter()
                                .position(|&(end, slot, reserved)| {
                                    end == position
                                        && slot == usize::from(values[&value].0)
                                        && reserved >= bytes
                                })
                                .map(|index| {
                                    let (_, slot, reserved) = active.remove(index);
                                    (slot, reserved)
                                })
                        } else {
                            None
                        }
                    } else {
                        None
                    };
                    let (slot, reserved) = reused.unwrap_or_else(|| {
                        let slot = free.entry(bytes).or_default().pop().unwrap_or_else(|| {
                            let slot = extent;
                            extent += bytes;
                            slot
                        });
                        (slot, bytes)
                    });
                    if extent > 0xf00 {
                        return Err("SM83 frame exceeds WRAM0 stack capacity".into());
                    }
                    values.insert(id, (slot as u16, ty, bytes as u16));
                    active.push((*uses[block].get(&id).unwrap_or(&position), slot, reserved));
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
                    .map(|p| usize::from(widths.values[&p.0]))
                    .sum::<usize>()
            })
            .max()
            .unwrap_or(0);
        let arithmetic = offset;
        offset += 25;
        for (index, width) in large_locals {
            if offset + usize::from(width) > 0xf00 {
                return Err("SM83 frame exceeds WRAM0 stack capacity".into());
            }
            locals[index] = offset as u16;
            offset += usize::from(width);
        }
        let arguments: usize = function
            .parameters
            .iter()
            .map(|ty| usize::from(ty.bits() / 8))
            .sum();
        let returns = function
            .returns
            .map(|ty| usize::from(ty.bits() / 8))
            .unwrap_or(0);
        if offset + arguments + returns + cookie + 4 > 0xf00 {
            return Err("SM83 frame and call storage exceed WRAM0 stack capacity".into());
        }
        Ok(Self {
            values,
            allocations,
            locals,
            local_widths: widths.locals,
            pins,
            pin_bank,
            edge: edge as u16,
            arithmetic: arithmetic as u16,
            size: offset as u16,
        })
    }
}

pub fn class_symbol(identity: Option<Definition>) -> String {
    match identity {
        Some(id) => format!("__koh_type_a{}_t{:08x}", id.assembly, id.token),
        None => "__koh_type_object".into(),
    }
}

pub fn emit(function: &Function) -> Result<Object, String> {
    emit_with_strings(function, None, false, None)
}

pub(crate) fn emit_with_strings(
    function: &Function,
    strings: Option<&BTreeMap<Vec<u16>, String>>,
    banked: bool,
    pdb: Option<&crate::pdb::PortablePdb>,
) -> Result<Object, String> {
    verify(function)?;
    let constants: BTreeMap<_, _> = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|i| {
            if let (Some((id, _)), Op::Constant(bits)) = (i.result, &i.op) {
                Some((id, *bits))
            } else {
                None
            }
        })
        .collect();
    let cookie = if banked { 4 } else { 0 };
    let layout = Layout::new(function, cookie)?;
    let cookie = cookie as u16;
    let name = symbol(function.identity);
    let mut code = Code {
        data: vec![],
        readonly: vec![],
        literals: BTreeMap::new(),
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
        pdb,
        method: function.identity.token,
        source: None,
        lines: vec![],
        debug_bytes: 0,
        debug_overflow: false,
    };
    let allocator = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .any(|i| {
            matches!(
                i.op,
                Op::NewArray { .. }
                    | Op::NewRectangle { .. }
                    | Op::NewObject(_)
                    | Op::NewDelegate { .. }
            )
        })
        .then(|| code.label());
    let aligned = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .any(|i| matches!(i.op, Op::NewArray {alignment,..} if alignment > 1));
    let multiplication = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .any(|i| {
            matches!(
                i.op,
                Op::NewRectangle { .. }
                    | Op::RectangleAddress { .. }
                    | Op::Binary {
                        op: Binary::Multiply,
                        ..
                    }
            )
        });
    let mut multipliers = BTreeMap::<u16, SymbolId>::new();
    let memory_loop = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .any(|i| {
            matches!(
                i.op,
                Op::MemoryFill { .. }
                    | Op::MemoryCopy { .. }
                    | Op::VBlankCopy { .. }
                    | Op::VramWrite { .. }
            )
        });
    code.guard(
        layout.size
            + if allocator.is_some() || multiplication || memory_loop {
                4
            } else {
                2
            },
    );
    code.adjust_sp(-i32::from(layout.size));
    for &flag in layout.pins.values() {
        code.constant(flag, 1, 0);
    }
    let return_width = function.returns.map(|ty| ty.bits() / 8).unwrap_or(0);
    let mut argument = layout.size + 2 + cookie + return_width;
    for &(id, ty) in &function.blocks[0].parameters {
        let width = ty.bits() / 8;
        code.copy(argument, layout.values[&id].0, layout.values[&id].2);
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
            let place = |id| Slot {
                offset: layout.values[&id].0,
                bytes: layout.values[&id].2,
            };
            let width = output.map(|p| p.2).unwrap_or(0);
            let destination = output.map(|p| p.0).unwrap_or(0);
            match instruction.op {
                Op::IsClass { object, class } => {
                    code.indirect(slot(object));
                    code.constant(destination, 4, 0);
                    let done = code.label();
                    code.bytes(&[0x78, 0xb1]);
                    code.jump(0xca, done);
                    code.object_class(layout.arithmetic + 24);
                    let descriptor = code.import(class_symbol(class.identity()));
                    code.jump(0x21, descriptor);
                    code.bytes(&[0x7a, 0xbc]);
                    code.jump(0xc2, done);
                    code.bytes(&[0x7b, 0xbd]);
                    code.jump(0xc2, done);
                    code.constant(destination, 4, 1);
                    code.mark(done);
                }
                Op::CastClass { object, class } => {
                    code.copy(slot(object), destination, 2);
                    code.indirect(destination);
                    let done = code.label();
                    code.bytes(&[0x78, 0xb1]);
                    code.jump(0xca, done);
                    code.object_class(layout.arithmetic + 24);
                    code.bytes(&[0x62, 0x6b]);
                    let descriptor = code.import(class_symbol(class.identity()));
                    code.jump(0x01, descriptor);
                    code.bytes(&[0x16, 65]);
                    let again = code.label();
                    let parent = code.label();
                    let fault = code.import("__koh_trap_type".into());
                    code.mark(again);
                    code.bytes(&[0x7c, 0xb8]);
                    code.jump(0xc2, parent);
                    code.bytes(&[0x7d, 0xb9]);
                    code.jump(0xca, done);
                    code.mark(parent);
                    code.bytes(&[0x7c, 0xb5]);
                    code.jump(0xca, fault);
                    code.bytes(&[0x2a, 0x66, 0x6f, 0x15]);
                    code.jump(0xc2, again);
                    code.jump(0xc3, fault);
                    code.mark(done);
                }
                Op::Upcast { value, .. } => code.copy(slot(value), destination, 2),
                Op::CheckObject(object) => {
                    code.indirect(slot(object));
                    code.check_reference(layout.values[&object].1);
                }
                Op::ObjectFieldAddress { object, offset, .. } => {
                    code.indirect(slot(object));
                    code.check_reference(layout.values[&object].1);
                    code.bytes(&[
                        0x60,
                        0x69,
                        0x11,
                        offset as u8,
                        (offset >> 8) as u8,
                        0x19,
                        0x44,
                        0x4d,
                    ]);
                    code.save_word(destination);
                }
                Op::NewDelegate {
                    target, receiver, ..
                } => {
                    let temp = layout.arithmetic;
                    code.constant(temp + 20, 2, 2);
                    code.constant(temp + 6, 2, 6);
                    code.constant(temp + 8, 2, 6);
                    if aligned {
                        code.constant(temp + 10, 2, 0);
                    }
                    code.jump(0xcd, allocator.ok_or("Missing delegate allocator")?);
                    code.copy(slot(receiver), temp, 2);
                    code.heap_bank(temp + 24);
                    code.indirect(temp + 12);
                    code.bytes(&[0x03, 0x03]);
                    let callee = code.import(symbol(target));
                    code.jump(0x21, callee);
                    code.bytes(&[0x7d, 0x02, 0x03, 0x7c, 0x02, 0x03]);
                    code.jump(0x21, callee);
                    code.patches.last_mut().unwrap().expression = RelocationExpr::Bank(callee);
                    code.bytes(&[0x7d, 0x02, 0x03, 0x7c, 0x02, 0x03]);
                    code.address(temp);
                    code.bytes(&[0x2a, 0x02, 0x03, 0x7e, 0x02]);
                    code.restore_bank(temp + 24);
                    code.copy(temp + 12, destination, 2);
                }
                Op::NewObject(class) => {
                    let temp = layout.arithmetic;
                    code.constant(temp + 20, 2, 2);
                    code.constant(temp + 6, 2, u64::from(class.bytes() - 2));
                    code.constant(temp + 8, 2, u64::from(class.bytes() - 2));
                    if aligned {
                        code.constant(temp + 10, 2, 0);
                    }
                    code.jump(0xcd, allocator.ok_or("Missing object allocator")?);
                    code.copy(temp + 12, destination, 2);
                    code.indirect(destination);
                    code.heap_bank(temp + 24);
                    let descriptor = code.import(class_symbol(class.identity()));
                    code.jump(0x21, descriptor);
                    code.bytes(&[0x7d, 0x02, 0x03, 0x7c, 0x02]);
                    code.restore_bank(temp + 24);
                }
                Op::HBlankStream {
                    source,
                    first,
                    count,
                    register,
                } => {
                    let done = code.label();
                    code.load_a(slot(count));
                    code.bytes(&[0xb7]);
                    code.jump(0xca, done);
                    code.bytes(&[0xf3, 0x4f]);
                    code.load_a(slot(first));
                    code.bytes(&[0x47, 0xfe, 144]);
                    let bounds = code.import("__koh_trap_bounds".into());
                    code.jump(0xd2, bounds);
                    code.bytes(&[0x3e, 144, 0x90, 0xb9]);
                    code.jump(0xda, bounds);
                    code.bytes(&[0xf0, 0x40, 0xcb, 0x7f]);
                    let hardware = code.import("__koh_trap_hardware".into());
                    code.jump(0xca, hardware);
                    code.bytes(&[0xc5]);
                    code.indirect(slot(source) + 2);
                    code.bytes(&[0x60, 0x69, 0xc1]);
                    let leave_blank = code.label();
                    let enter_blank = code.label();
                    code.mark(leave_blank);
                    code.bytes(&[0xf0, 0x44, 0xfe, 144]);
                    code.jump(0xd2, leave_blank);
                    code.mark(enter_blank);
                    code.bytes(&[0xf0, 0x44, 0xfe, 144]);
                    code.jump(0xda, enter_blank);
                    let next = code.label();
                    let line = code.label();
                    let mode = code.label();
                    code.mark(next);
                    code.bytes(&[0x2a, 0x57]);
                    code.mark(line);
                    code.bytes(&[0xf0, 0x44, 0xb8]);
                    code.jump(0xc2, line);
                    code.mark(mode);
                    code.bytes(&[0xf0, 0x41, 0xe6, 3]);
                    code.jump(0xc2, mode);
                    code.bytes(&[
                        0x7a,
                        0xea,
                        register as u8,
                        (register >> 8) as u8,
                        0x04,
                        0x0d,
                    ]);
                    code.jump(0xc2, next);
                    code.mark(done);
                }
                Op::VramWrite { address, value } => {
                    code.indirect(slot(address));
                    code.bytes(&[0xc5]);
                    code.load_a(slot(value) + 2);
                    code.bytes(&[0x57, 0xe1, 0x7c, 0xfe, 0x80]);
                    let hardware = code.import("__koh_trap_hardware".into());
                    code.jump(0xda, hardware);
                    code.bytes(&[0xfe, 0xa0]);
                    code.jump(0xd2, hardware);
                    code.bytes(&[0xf3]);
                    let accessible = code.label();
                    code.mark(accessible);
                    code.bytes(&[0xf0, 0x41, 0xe6, 2]);
                    code.jump(0xc2, accessible);
                    code.bytes(&[0x7a, 0x77]);
                }
                Op::VBlankCopy {
                    destination: target,
                    source,
                    count,
                } => {
                    code.extend(place(count), layout.arithmetic, 2);
                    code.indirect(slot(source));
                    code.bytes(&[0xc5]);
                    code.indirect(slot(target) + 2);
                    code.bytes(&[0xc5]);
                    code.indirect(layout.arithmetic + 4);
                    code.bytes(&[0xd1, 0xe1, 0x78, 0xb1]);
                    let done = code.label();
                    let byte = code.label();
                    let active = code.label();
                    let blank = code.label();
                    code.jump(0xca, done);
                    code.bytes(&[0xf3, 0xf0, 0x40, 0xcb, 0x7f]);
                    let hardware = code.import("__koh_trap_hardware".into());
                    code.jump(0xca, hardware);
                    code.bytes(&[0xf0, 0x44, 0xd6, 144, 0xfe, 6]);
                    code.jump(0xda, byte);
                    code.mark(active);
                    code.bytes(&[0xf0, 0x44, 0xfe, 144]);
                    code.jump(0xd2, active);
                    code.mark(blank);
                    code.bytes(&[0xf0, 0x44, 0xfe, 144]);
                    code.jump(0xda, blank);
                    code.mark(byte);
                    code.bytes(&[0xf0, 0x44, 0xd6, 144, 0xfe, 7]);
                    code.jump(0xd2, done);
                    code.bytes(&[0x2a, 0x12, 0x13, 0x0b, 0x78, 0xb1]);
                    code.jump(0xc2, byte);
                    code.mark(done);
                    code.bytes(&[0xc5]);
                    code.address(layout.arithmetic + 2);
                    code.bytes(&[0xd1, 0x2a, 0x93, 0x4f, 0x7e, 0x9a, 0x47, 0xc5]);
                    code.address(destination + 2);
                    code.bytes(&[0xd1, 0x73, 0x23, 0x72, 0x23, 0xaf, 0x22, 0x77]);
                }
                Op::MemoryCopy {
                    destination,
                    source,
                    count,
                } => {
                    code.extend(place(count), layout.arithmetic, 2);
                    code.indirect(slot(source));
                    code.bytes(&[0xc5]);
                    code.indirect(slot(destination) + 2);
                    code.bytes(&[0xc5]);
                    code.indirect(layout.arithmetic + 4);
                    code.bytes(&[0xd1, 0xe1, 0x78, 0xb1]);
                    let done = code.label();
                    let byte = code.label();
                    code.jump(0xca, done);
                    code.mark(byte);
                    code.bytes(&[0x2a, 0x12, 0x13, 0x0b, 0x78, 0xb1]);
                    code.jump(0xc2, byte);
                    code.mark(done);
                }
                Op::MemoryFill {
                    address,
                    value,
                    count,
                } => {
                    code.extend(place(count), layout.arithmetic, 2);
                    code.load_a(slot(value));
                    code.bytes(&[0xf5]);
                    code.indirect(slot(address) + 2);
                    code.bytes(&[0xc5]);
                    code.indirect(layout.arithmetic + 4);
                    code.bytes(&[0xd1, 0xf1, 0x67, 0x78, 0xb1]);
                    let done = code.label();
                    let byte = code.label();
                    code.jump(0xca, done);
                    code.mark(byte);
                    code.bytes(&[0x7c, 0x12, 0x13, 0x0b, 0x78, 0xb1]);
                    code.jump(0xc2, byte);
                    code.mark(done);
                }
                Op::ArenaAllocate(bytes) => {
                    code.allocate_raw(place(bytes), destination, layout.arithmetic)
                }
                Op::ArenaReset => {
                    let used = code.import("__koh_raw_used".into());
                    code.jump(0x21, used);
                    code.bytes(&[0x36, 0, 0x23, 0x36, 0]);
                }
                Op::CpuControl(control) => match control {
                    CpuControl::DisableInterrupts => code.bytes(&[0xf3]),
                    CpuControl::EnableInterrupts => code.bytes(&[0xfb]),
                    CpuControl::Halt => code.bytes(&[0x76]),
                    CpuControl::Nop => code.bytes(&[0]),
                    CpuControl::Stop => code.bytes(&[0x10, 0]),
                },
                Op::OamDma(page) => {
                    code.load_a(slot(page));
                    let routine = code.import("__koh_oamdma".into());
                    code.jump(0xcd, routine);
                }
                Op::StackAllocate { .. } => {
                    let (region, bytes) = layout.allocations[&instruction
                        .result
                        .ok_or("Invalid stack allocation result")?
                        .0];
                    if bytes == 0 {
                        code.constant(destination, 2, 0);
                    } else {
                        code.address(region);
                        code.bytes(&[
                            0x01,
                            bytes as u8,
                            (bytes >> 8) as u8,
                            0x36,
                            0,
                            0x23,
                            0x0b,
                            0x78,
                            0xb1,
                            0x20,
                            0xf8,
                        ]);
                        code.address(region);
                        code.bytes(&[0x44, 0x4d]);
                        code.save_word(destination);
                    }
                }
                Op::FieldHandle(_) | Op::MethodHandle(_) => {}
                Op::ZeroStruct => code.constant(destination, width, 0),
                Op::FieldAddress {
                    address, offset, ..
                } => {
                    code.indirect(slot(address));
                    code.check_reference(layout.values[&address].1);
                    if offset <= 2 {
                        for _ in 0..offset {
                            code.bytes(&[0x03]);
                        }
                    } else {
                        code.bytes(&[
                            0x60,
                            0x69,
                            0x11,
                            offset as u8,
                            (offset >> 8) as u8,
                            0x19,
                            0x44,
                            0x4d,
                        ]);
                    }
                    code.save_word(destination);
                }
                Op::ExtractField {
                    value,
                    offset,
                    storage,
                } => {
                    let source = slot(value) + offset;
                    let copied = width.min(storage.bytes());
                    code.copy(source, destination, copied);
                    if copied < width {
                        if matches!(storage, crate::ir::FieldStorage::Value(Storage::Scalar(scalar)) if scalar.signed())
                        {
                            code.load_a(source + copied - 1);
                            code.bytes(&[0x87, 0x9f]);
                        } else {
                            code.bytes(&[0xaf]);
                        }
                        for byte in copied..width {
                            code.store_a(destination + byte);
                        }
                    }
                }
                Op::StringLiteral(ref units) => {
                    let target = if let Some(strings) = strings {
                        code.import(
                            strings
                                .get(units)
                                .ok_or("Missing interned string literal")?
                                .clone(),
                        )
                    } else {
                        let mut bytes = (units.len() as u16).to_le_bytes().to_vec();
                        bytes.extend(units.iter().flat_map(|unit| unit.to_le_bytes()));
                        code.literal(&bytes)?
                    };
                    code.jump(0x01, target);
                    code.save_word(destination);
                }
                Op::StringLength(string) => {
                    code.indirect(slot(string));
                    code.check_reference(Type::String);
                    code.bytes(&[0x60, 0x69, 0x4e, 0x23, 0x46]);
                    code.save_word(destination);
                    for byte in 2..width {
                        code.constant(destination + byte, 1, 0);
                    }
                }
                Op::StringChar { string, index } => {
                    let temp = layout.arithmetic;
                    code.indirect(slot(string));
                    code.check_reference(Type::String);
                    code.bytes(&[0x60, 0x69, 0x4e, 0x23, 0x46]);
                    code.save_word(temp);
                    let fault = code.import("__koh_trap_bounds".into());
                    code.check_high(slot(index), place(index).bytes, fault);
                    code.extend(place(index), temp + 10, 2);
                    code.below_word(temp + 10, temp, fault);
                    code.copy(temp + 10, temp, 2);
                    code.scale_word(temp, 2);
                    code.indirect(slot(string));
                    code.address(temp);
                    code.bytes(&[
                        0x5e, 0x23, 0x56, 0x60, 0x69, 0x19, 0x23, 0x23, 0x4e, 0x23, 0x46,
                    ]);
                    code.save_word(destination);
                    for byte in 2..width {
                        code.constant(destination + byte, 1, 0);
                    }
                }
                Op::InitializeArray { array, ref bytes } => {
                    let (stride, header) = match layout.values[&array].1 {
                        Type::Array(element, kind) => (element.bytes(), kind.header_bytes()),
                        Type::Null => (1, 2),
                        _ => return Err("Invalid verified initializer operand".into()),
                    };
                    code.initialize_array(
                        slot(array),
                        u16::from(stride),
                        header,
                        bytes,
                        layout.arithmetic,
                    )?;
                }
                Op::NewRectangle {
                    element,
                    rows,
                    columns,
                } => {
                    let temp = layout.arithmetic;
                    let fault = code.import("__koh_trap_allocation".into());
                    code.check_high(slot(rows), place(rows).bytes, fault);
                    code.check_high(slot(columns), place(columns).bytes, fault);
                    code.extend(place(rows), temp, 4);
                    code.extend(place(columns), temp + 8, 4);
                    let routine = *multipliers.entry(4).or_insert_with(|| code.label());
                    code.jump(0xcd, routine);
                    code.check_high(temp + 16, 4, fault);
                    code.copy(temp + 16, temp + 6, 2);
                    code.below_constant(temp + 6, 4090 / element.bytes() + 1, fault);
                    code.copy(temp + 6, temp + 8, 2);
                    code.scale_word(temp + 8, element.bytes());
                    code.constant(temp + 20, 2, 6);
                    if aligned {
                        code.constant(temp + 10, 2, 0);
                    }
                    code.jump(0xcd, allocator.ok_or("Missing rectangular allocator")?);
                    code.indirect(temp + 12);
                    code.heap_bank(temp + 24);
                    for (value, offset) in [(rows, 2), (columns, 4)] {
                        code.extend(place(value), temp, 2);
                        code.indirect(temp + 12);
                        for _ in 0..offset {
                            code.bytes(&[0x03]);
                        }
                        code.address(temp);
                        code.bytes(&[0x5e, 0x23, 0x56, 0x7b, 0x02, 0x03, 0x7a, 0x02]);
                    }
                    code.restore_bank(temp + 24);
                    code.copy(temp + 12, destination, 2);
                }
                Op::RectangleAddress { array, row, column } => {
                    let Type::Array(element, ArrayKind::Rectangle) = layout.values[&array].1 else {
                        return Err("Invalid verified rectangular type".into());
                    };
                    let temp = layout.arithmetic;
                    code.array_header(slot(array), temp + 24);
                    code.indirect(slot(array));
                    code.bytes(&[0x60, 0x69, 0x23, 0x23, 0x2a, 0x4f, 0x2a, 0x47]);
                    code.save_word(temp + 20);
                    code.indirect(slot(array));
                    code.bytes(&[0x60, 0x69, 0x23, 0x23, 0x23, 0x23, 0x2a, 0x4f, 0x46]);
                    code.save_word(temp + 22);
                    code.restore_bank(temp + 24);
                    let fault = code.import("__koh_trap_bounds".into());
                    code.check_high(slot(row), place(row).bytes, fault);
                    code.check_high(slot(column), place(column).bytes, fault);
                    code.extend(place(row), temp, 2);
                    code.below_word(temp, temp + 20, fault);
                    code.extend(place(column), temp, 2);
                    code.below_word(temp, temp + 22, fault);
                    code.extend(place(row), temp, 4);
                    code.copy(temp + 22, temp + 8, 2);
                    code.constant(temp + 10, 2, 0);
                    let routine = *multipliers.entry(4).or_insert_with(|| code.label());
                    code.jump(0xcd, routine);
                    code.check_high(temp + 16, 4, fault);
                    code.extend(place(column), temp, 2);
                    code.arithmetic(temp + 16, temp, temp + 16, 2, 0x88);
                    code.jump(0xda, fault);
                    code.scale_word(temp + 16, element.bytes());
                    code.indirect(slot(array));
                    code.address(temp + 16);
                    code.bytes(&[0x5e, 0x23, 0x56, 0x60, 0x69, 0x19]);
                    for _ in 0..6 {
                        code.bytes(&[0x23]);
                    }
                    code.bytes(&[0x44, 0x4d]);
                    code.save_word(destination);
                }
                Op::ArrayDimension { array, dimension } => {
                    let Type::Array(_, kind) = layout.values[&array].1 else {
                        return Err("Invalid verified dimension type".into());
                    };
                    let temp = layout.arithmetic;
                    code.array_header(slot(array), temp + 24);
                    code.save_word(temp + 20);
                    let fault = code.import("__koh_trap_bounds".into());
                    code.check_high(slot(dimension), place(dimension).bytes, fault);
                    code.extend(place(dimension), temp, 2);
                    code.below_constant(temp, kind.rank(), fault);
                    if kind == ArrayKind::Rectangle {
                        code.indirect(slot(array));
                        code.address(temp);
                        code.bytes(&[
                            0x5e, 0x23, 0x56, 0x60, 0x69, 0x19, 0x19, 0x23, 0x23, 0x2a, 0x4f, 0x46,
                        ]);
                        code.save_word(destination);
                    } else {
                        code.copy(temp + 20, destination, 2);
                    }
                    code.constant(destination + 2, 2, 0);
                    code.restore_bank(temp + 24);
                }
                Op::NewArray {
                    element,
                    length,
                    alignment,
                } => {
                    let temp = layout.arithmetic;
                    code.constant(temp + 20, 2, 2);
                    let fault = code.import("__koh_trap_allocation".into());
                    code.check_high(slot(length), place(length).bytes, fault);
                    code.extend(place(length), temp + 6, 2);
                    code.below_constant(temp + 6, 4094 / element.bytes() + 1, fault);
                    code.copy(temp + 6, temp + 8, 2);
                    code.scale_word(temp + 8, element.bytes());
                    if aligned {
                        code.constant(temp + 10, 2, u64::from(alignment - 1));
                    }
                    code.jump(0xcd, allocator.ok_or("Missing array allocator")?);
                    code.copy(temp + 12, destination, 2);
                }
                Op::ArrayLength(array) => {
                    code.array_header(slot(array), layout.arithmetic + 24);
                    code.save_word(destination);
                    code.restore_bank(layout.arithmetic + 24);
                }
                Op::ArrayAddress { array, index } => {
                    let Type::Array(element, ArrayKind::Vector) = layout.values[&array].1 else {
                        return Err("Invalid verified array type".into());
                    };
                    let temp = layout.arithmetic;
                    code.array_header(slot(array), temp + 24);
                    code.save_word(temp);
                    let fault = code.import("__koh_trap_bounds".into());
                    code.check_high(slot(index), place(index).bytes, fault);
                    code.extend(place(index), temp + 10, 2);
                    code.below_word(temp + 10, temp, fault);
                    code.copy(temp + 10, temp, 2);
                    code.scale_word(temp, element.bytes());
                    code.indirect(slot(array));
                    code.address(temp);
                    code.bytes(&[0x5e, 0x23, 0x56, 0x60, 0x69, 0x19, 0x23, 0x23, 0x44, 0x4d]);
                    code.save_word(destination);
                    code.restore_bank(temp + 24);
                }
                Op::LocalAddress { local, .. } => {
                    code.address(layout.locals[local as usize]);
                    code.bytes(&[0x44, 0x4d]);
                    code.save_word(destination);
                }
                Op::StaticAddress { field, .. } | Op::InitializerAddress(field) => {
                    let target = code.import(static_symbol(field));
                    code.jump(0x21, target);
                    code.bytes(&[0x44, 0x4d]);
                    code.save_word(destination);
                }
                Op::LoadMemory {
                    address,
                    bits,
                    signed,
                } => {
                    let copied = u16::from(bits / 8);
                    code.indirect(slot(address));
                    code.check_reference(layout.values[&address].1);
                    let managed = layout.values[&address].1.is_byref();
                    if managed {
                        code.heap_bank(layout.arithmetic + 24);
                    }
                    code.address(destination);
                    code.bytes(&[0x50, 0x59]);
                    if copied > 8 {
                        code.bytes(&[0x44, 0x4d, 0x62, 0x6b, 0x50, 0x59]);
                        code.copy_registers(copied);
                    } else {
                        for byte in 0..copied {
                            code.bytes(&[0x1a, 0x22]);
                            if byte + 1 < copied {
                                code.bytes(&[0x13]);
                            }
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
                    if managed {
                        code.restore_bank(layout.arithmetic + 24);
                    }
                }
                Op::StoreMemory {
                    address,
                    value,
                    bits,
                } => {
                    code.indirect(slot(address));
                    code.check_reference(layout.values[&address].1);
                    let managed = layout.values[&address].1.is_byref();
                    if managed {
                        code.heap_bank(layout.arithmetic + 24);
                    }
                    code.address(slot(value));
                    code.bytes(&[0x50, 0x59]);
                    let copied = place(value).bytes.min(u16::from(bits / 8));
                    code.copy_registers(copied);
                    if copied != 0 && copied <= 8 && copied < bits / 8 {
                        code.bytes(&[0x13]);
                    }
                    if copied < u16::from(bits / 8) {
                        code.bytes(&[0xaf]);
                        for byte in copied..u16::from(bits / 8) {
                            code.bytes(&[0x12]);
                            if byte + 1 < u16::from(bits / 8) {
                                code.bytes(&[0x13]);
                            }
                        }
                    }
                    if managed {
                        code.restore_bank(layout.arithmetic + 24);
                    }
                }
                Op::Call {
                    target: _,
                    ref signature,
                    ref arguments,
                }
                | Op::InvokeDelegate {
                    ref signature,
                    ref arguments,
                } => {
                    let indirect = matches!(instruction.op, Op::InvokeDelegate { .. });
                    let target = if let Op::Call { target, .. } = instruction.op {
                        target
                    } else {
                        function.identity
                    };
                    let temp = layout.arithmetic;
                    if indirect {
                        code.indirect(slot(arguments[0]));
                        code.check_reference(layout.values[&arguments[0]].1);
                        code.heap_bank(temp + 24);
                        for (offset, saved) in [(2, temp), (4, temp + 2), (6, temp + 4)] {
                            code.indirect(slot(arguments[0]));
                            code.bytes(&[0x60, 0x69]);
                            for _ in 0..offset {
                                code.bytes(&[0x23]);
                            }
                            code.bytes(&[0x2a, 0x4f, 0x46]);
                            code.save_word(saved);
                        }
                        code.restore_bank(temp + 24);
                    }
                    let initialized = if !indirect && banked && target.token >> 24 == 0xff {
                        let constructor = Definition {
                            assembly: target.assembly,
                            token: 0x06000000 | (target.token & 0xffffff),
                        };
                        let state = code.import(static_symbol(constructor));
                        code.jump(0xfa, state);
                        code.bytes(&[0xb7]);
                        let done = code.label();
                        code.jump(0xc2, done);
                        Some(done)
                    } else {
                        None
                    };
                    let abi_return = signature.returns.map(|ty| ty.bits() / 8).unwrap_or(0);
                    let size: usize = signature
                        .parameters
                        .iter()
                        .map(|ty| usize::from(ty.bits() / 8))
                        .sum::<usize>()
                        + usize::from(abi_return + cookie);
                    if size + 2 > 0xf00 {
                        return Err(format!(
                            "{name} IL_{:04x}: call storage exceeds stack capacity",
                            instruction.il_offset
                        ));
                    }
                    let size = size as u16;
                    code.guard(size + 2);
                    code.adjust_sp(-i32::from(size));
                    let mut offset = abi_return + cookie;
                    for (index, (&value, &ty)) in
                        arguments.iter().zip(&signature.parameters).enumerate()
                    {
                        let bytes = ty.bits() / 8;
                        code.extend(
                            Slot {
                                offset: if indirect && index == 0 {
                                    temp + 4 + size
                                } else {
                                    slot(value) + size
                                },
                                bytes: place(value).bytes,
                            },
                            offset,
                            bytes,
                        );
                        offset += bytes;
                    }
                    let callee = if indirect {
                        SymbolId(0)
                    } else {
                        code.import(symbol(target))
                    };
                    if banked {
                        code.jump(0x01, SymbolId(0));
                        code.patches.last_mut().unwrap().expression = RelocationExpr::BankCurrent;
                        code.address(2);
                        code.bytes(&[0x71, 0x23, 0x70]);
                        if indirect {
                            code.indirect(temp + size);
                            code.address(temp + 2 + size);
                            code.bytes(&[0x5e, 0x23, 0x56]);
                        } else {
                            code.jump(0x11, callee);
                            code.patches.last_mut().unwrap().expression =
                                RelocationExpr::Bank(callee);
                            code.jump(0x01, callee);
                        }
                        let trampoline = code.import("__koh_far_call".into());
                        code.jump(0xcd, trampoline);
                    } else if indirect {
                        code.indirect(temp + size);
                        let returned = code.label();
                        code.jump(0x21, returned);
                        code.bytes(&[0xe5, 0x60, 0x69, 0xe9]);
                        code.mark(returned);
                    } else {
                        code.jump(0xcd, callee);
                    }
                    let size = size - cookie;
                    if width != 0 {
                        code.copy(0, destination + size, width);
                    }
                    code.adjust_sp(i32::from(size));
                    if let Some(done) = initialized {
                        code.mark(done);
                    }
                }
                Op::Constant(bits) => code.constant(destination, width, bits),
                Op::LoadLocal(local) => code.extend(
                    Slot {
                        offset: layout.locals[local as usize],
                        bytes: layout.local_widths[local as usize],
                    },
                    destination,
                    width,
                ),
                Op::StorePinned { local, value } => code.store_pin(
                    slot(value),
                    layout.locals[local as usize],
                    layout.pins[&local],
                    layout.pin_bank,
                    &layout.pins,
                    layout.values[&value].1 == Type::Native,
                ),
                Op::StoreLocal { local, value } => code.extend(
                    place(value),
                    layout.locals[local as usize],
                    layout.local_widths[local as usize],
                ),
                Op::Negate(value) => code.negate_value(place(value), destination, width),
                Op::Not(value) => {
                    for byte in 0..width {
                        code.value_a(place(value), byte);
                        code.bytes(&[0x2f]);
                        code.store_a(destination + byte);
                    }
                }
                Op::Convert {
                    value,
                    bits,
                    signed,
                } => {
                    let copied = width.min(u16::from(bits / 8)).min(place(value).bytes);
                    code.copy(slot(value), destination, copied);
                    if copied < width {
                        if signed
                            && copied
                                == (u16::from(bits)
                                    .min(layout.values[&value].1.bits())
                                    .min(output.ok_or("Conversion result missing")?.1.bits())
                                    / 8)
                        {
                            code.load_a(slot(value) + copied - 1);
                            code.bytes(&[0x87, 0x9f]);
                        } else {
                            code.bytes(&[0xaf]);
                        }
                        code.bytes(&[0xf5]);
                        code.address(destination + copied + 2);
                        code.bytes(&[0xf1]);
                        for _ in copied..width {
                            code.bytes(&[0x22]);
                        }
                    }
                }
                Op::Compare { op, left, right } => {
                    let compared_width = place(left).bytes.max(place(right).bytes);
                    if op == Comparison::Equal && constants.get(&right) == Some(&0) {
                        code.equal_zero(slot(left), destination, place(left).bytes);
                    } else if op == Comparison::Equal && constants.get(&left) == Some(&0) {
                        code.equal_zero(slot(right), destination, place(right).bytes);
                    } else {
                        let mut op = op;
                        if compared_width < layout.values[&left].1.bits() / 8 {
                            op = match op {
                                Comparison::LessSigned => Comparison::LessUnsigned,
                                Comparison::GreaterSigned => Comparison::GreaterUnsigned,
                                _ => op,
                            };
                        }
                        let mut offsets = [slot(left), slot(right)];
                        for (index, id) in [left, right].into_iter().enumerate() {
                            if place(id).bytes < compared_width {
                                offsets[index] = layout.arithmetic + index as u16 * 8;
                                code.extend(place(id), offsets[index], compared_width);
                            }
                        }
                        code.compare(offsets[0], offsets[1], destination, compared_width, op);
                    }
                }
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
                        code.arithmetic_values(
                            place(left),
                            place(right),
                            destination,
                            width,
                            opcode,
                        );
                    }
                    Binary::Multiply => {
                        let literal = [(left, right), (right, left)].into_iter().find_map(
                            |(value, literal)| {
                                constants
                                    .get(&literal)
                                    .filter(|c| **c == 0 || c.is_power_of_two())
                                    .map(|&c| (value, c))
                            },
                        );
                        if let Some((value, constant)) = literal {
                            if constant == 0 {
                                code.constant(destination, width, 0);
                            } else {
                                code.extend(place(value), destination, width);
                                code.shift_constant(
                                    destination,
                                    destination,
                                    width,
                                    constant.trailing_zeros() as u8,
                                    true,
                                    false,
                                );
                            }
                        } else {
                            code.extend(place(left), layout.arithmetic, width);
                            code.extend(place(right), layout.arithmetic + 8, width);
                            let routine = *multipliers.entry(width).or_insert_with(|| code.label());
                            code.jump(0xcd, routine);
                            code.copy(layout.arithmetic + 16, destination, width);
                        }
                    }
                    Binary::DivideSigned
                    | Binary::DivideUnsigned
                    | Binary::RemainderSigned
                    | Binary::RemainderUnsigned => code.divide(
                        place(left),
                        place(right),
                        destination,
                        width,
                        layout.arithmetic,
                        matches!(op, Binary::DivideSigned | Binary::RemainderSigned),
                        matches!(op, Binary::RemainderSigned | Binary::RemainderUnsigned),
                    ),
                    Binary::ShiftLeft | Binary::ShiftRightSigned | Binary::ShiftRightUnsigned => {
                        let semantic_bits = layout.values[&left].1.bits();
                        let working_width = width.max(place(left).bytes);
                        let working = if working_width == width {
                            destination
                        } else {
                            layout.arithmetic
                        };
                        let signed = op == Binary::ShiftRightSigned
                            && place(left).bytes == semantic_bits / 8;
                        code.extend(place(left), working, working_width);
                        if let Some(&count) = constants.get(&right) {
                            code.shift_constant(
                                working,
                                working,
                                working_width,
                                (count & u64::from(semantic_bits - 1)) as u8,
                                op == Binary::ShiftLeft,
                                signed,
                            );
                            code.copy(working, destination, width);
                            continue;
                        }
                        code.load_a(slot(right));
                        code.bytes(&[
                            0xe6,
                            u8::try_from(semantic_bits - 1)
                                .map_err(|_| "Shift exceeds scalar width")?,
                            0x4f,
                        ]);
                        let start = code.label();
                        let end = code.label();
                        code.jump(0xca, end);
                        code.mark(start);
                        code.shift_one(
                            working,
                            working_width,
                            op == Binary::ShiftLeft,
                            signed,
                            false,
                        );
                        code.bytes(&[0x0d]);
                        code.jump(0xc2, start);
                        code.mark(end);
                        code.copy(working, destination, width);
                    }
                },
            }
            code.check_size()?;
        }
        let transfer = |code: &mut Code<'_>, edge: &Edge| {
            let mut offset = layout.edge;
            for (&value, &(target, _)) in edge
                .arguments
                .iter()
                .zip(&function.blocks[edge.block].parameters)
            {
                let (source, _, source_width) = layout.values[&value];
                let width = layout.values[&target].2;
                code.extend(
                    Slot {
                        offset: source,
                        bytes: source_width,
                    },
                    offset,
                    width,
                );
                offset += width;
            }
            offset = layout.edge;
            for &(id, _) in &function.blocks[edge.block].parameters {
                let width = layout.values[&id].2;
                code.copy(offset, layout.values[&id].0, width);
                offset += width;
            }
            code.jump(0xc3, labels[edge.block]);
        };
        code.mark_il(block.terminator.il_offset);
        match &block.terminator.kind {
            Control::Jump(edge) => transfer(&mut code, edge),
            Control::Switch {
                selector,
                cases,
                default,
            } => {
                let (offset, _, bytes) = layout.values[selector];
                for (index, edge) in cases.iter().enumerate() {
                    let next = code.label();
                    for (byte, expected) in (index as u32).to_le_bytes().into_iter().enumerate() {
                        code.value_a(Slot { offset, bytes }, byte as u16);
                        code.bytes(&[0xfe, expected]);
                        code.jump(0xc2, next);
                    }
                    transfer(&mut code, edge);
                    code.mark(next);
                    code.check_size()?;
                }
                transfer(&mut code, default);
            }
            Control::Branch { condition, yes, no } => {
                code.bytes(&[0x0e, 0]);
                for byte in 0..layout.values[condition].2 {
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
            Control::TypeFault => {
                let fault = code.import("__koh_trap_type".into());
                code.jump(0xc3, fault);
            }
            Control::Return(value) => {
                if !layout.pins.is_empty() {
                    code.pins_active(&layout.pins);
                    let done = code.label();
                    code.jump(0xca, done);
                    code.restore_bank(layout.pin_bank);
                    code.mark(done);
                }
                if let Some(value) = value {
                    code.extend(
                        Slot {
                            offset: layout.values[value].0,
                            bytes: layout.values[value].2,
                        },
                        layout.size + 2 + cookie,
                        return_width,
                    );
                }
                code.adjust_sp(i32::from(layout.size));
                code.bytes(&[0xc9]);
            }
        }
        code.check_size()?;
    }
    code.source = None;
    for (width, routine) in multipliers {
        code.mark(routine);
        code.multiply(width, layout.arithmetic + 2);
        code.bytes(&[0xc9]);
        code.check_size()?;
    }
    if let Some(allocator) = allocator {
        code.mark(allocator);
        code.new_array(
            layout.arithmetic + 8,
            layout.arithmetic + 14,
            layout.arithmetic + 2,
            aligned,
        );
        code.bytes(&[0xc9]);
    }
    code.check_size()?;
    let mut object = Object {
        sections: vec![
            Section {
                name: format!(".text.{name}"),
                kind: if banked { 1 } else { 0 },
                size: code.data.len() as u32,
                data: code.data,
                patches: code.patches,
                lines: code.lines,
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
    if !code.readonly.is_empty() {
        object.sections.push(Section {
            name: format!(".rodata.{name}"),
            kind: 0,
            size: code.readonly.len() as u32,
            data: code.readonly,
            ..Default::default()
        });
    }
    object.validate()?;
    Ok(object)
}

#[cfg(test)]
mod debug_tests {
    use super::*;
    use crate::pdb::{PortablePdb, Position, SequencePoint};

    fn multiplication_chain() -> Function {
        Function {
            identity: Definition {
                assembly: 0,
                token: 0x06000001,
            },
            parameters: vec![Type::I32, Type::I32],
            returns: Some(Type::I32),
            locals: vec![],
            blocks: vec![Block {
                parameters: vec![(0, Type::I32), (1, Type::I32)],
                instructions: (0..40)
                    .map(|i| Instruction {
                        result: Some((i + 2, Type::I32)),
                        op: Op::Binary {
                            op: Binary::Multiply,
                            left: if i == 0 { 0 } else { i + 1 },
                            right: 1,
                        },
                        il_offset: i,
                    })
                    .collect(),
                terminator: Terminator {
                    kind: Control::Return(Some(41)),
                    il_offset: 40,
                },
            }],
        }
    }

    fn symbols(file: String) -> PortablePdb {
        PortablePdb {
            id: [0; 20],
            documents: vec![file],
            methods: vec![
                (0..=40)
                    .map(|il_offset| SequencePoint {
                        il_offset,
                        source: Some(Position {
                            document: 1,
                            line: il_offset + 1,
                            column: 1,
                        }),
                    })
                    .collect(),
            ],
        }
    }

    #[test]
    fn runtime_helpers_and_prologues_have_no_source_ranges() {
        let function = multiplication_chain();
        let pdb = symbols("game.cs".into());
        let object = emit_with_strings(&function, None, false, Some(&pdb)).unwrap();
        let text = &object.sections[0];
        assert!(!text.lines.is_empty());
        assert!(text.lines[0].offset > 0);
        let end = text.lines.last().unwrap().offset + text.lines.last().unwrap().count;
        assert!(end < text.data.len(), "unmapped software multiply helper");
        assert!(text.lines.iter().all(|line| line.line <= 41));
    }

    #[test]
    fn expanded_source_metadata_has_a_function_budget() {
        let function = multiplication_chain();
        let pdb = symbols("x".repeat(65_535));
        let error = emit_with_strings(&function, None, false, Some(&pdb)).unwrap_err();
        assert!(error.contains("source metadata exceeds"), "{error}");
    }
}
