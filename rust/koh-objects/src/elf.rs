use crate::*;
use object::write::elf::{FileHeader, SectionHeader, Sym, Writer};
use object::{Endianness, Object as _, ObjectSection as _, ObjectSymbol as _, elf};
const MAX_ELF_SIZE: usize = 64 * 1024 * 1024;
const FLAGS: u32 = 0x4b4f4801;
const NOTE: &[u8] = b"\x04\0\0\0\x08\0\0\0\x01\0\0\0Koh\0SM83\x01\0\0\0";

#[derive(Default)]
struct Bytes(Vec<u8>, bool);
impl Bytes {
    fn byte(&mut self, v: u8) {
        if self.0.len() < MAX_ELF_SIZE {
            self.0.push(v);
        } else {
            self.1 = true;
        }
    }
    fn word(&mut self, v: u32) {
        for byte in v.to_le_bytes() {
            self.byte(byte);
        }
    }
    fn text(&mut self, v: &str) {
        self.word(v.len() as u32);
        if v.len() <= MAX_ELF_SIZE - self.0.len() {
            self.0.extend(v.as_bytes());
        } else {
            self.1 = true;
        }
    }
    fn source(&mut self, v: &Provenance) {
        self.text(&v.file);
        self.word(v.line);
        self.word(v.column);
        self.word(v.expansion.len() as u32);
        for x in &v.expansion {
            self.text(&x.file);
            self.word(x.line);
            self.word(x.column);
        }
    }
    fn optional_source(&mut self, v: &Option<Provenance>) {
        self.byte(v.is_some() as u8);
        if let Some(v) = v {
            self.source(v);
        }
    }
    fn reference(&mut self, v: &SectionRef) {
        match v {
            SectionRef::Local(x) => {
                self.byte(0);
                self.word(x.0 as u32)
            }
            SectionRef::External(x) => {
                self.byte(1);
                self.text(x)
            }
        }
    }
    fn expression(&mut self, v: &RelocationExpr) {
        match v {
            RelocationExpr::Literal(x) => {
                self.byte(1);
                self.word(*x as u32)
            }
            RelocationExpr::Symbol(x) => {
                self.byte(2);
                self.word(x.0 as u32)
            }
            RelocationExpr::CurrentAddress => self.byte(3),
            RelocationExpr::Unary(op, x) => {
                self.expression(x);
                self.byte(0x20 + *op as u8)
            }
            RelocationExpr::Binary(op, a, b) => {
                self.expression(a);
                self.expression(b);
                self.byte(0x40 + *op as u8)
            }
            RelocationExpr::Bank(x) => {
                self.byte(4);
                self.word(x.0 as u32)
            }
            RelocationExpr::BankCurrent => self.byte(5),
            RelocationExpr::BankSection(x) => {
                self.byte(6);
                self.reference(x)
            }
            RelocationExpr::SectionSize(x) => {
                self.byte(7);
                self.reference(x)
            }
            RelocationExpr::SectionStart(x) => {
                self.byte(8);
                self.reference(x)
            }
            RelocationExpr::SectionTypeSize(x) => {
                self.byte(9);
                self.byte(*x)
            }
            RelocationExpr::SectionTypeStart(x) => {
                self.byte(10);
                self.byte(*x)
            }
        }
    }
}
struct Cursor<'a>(&'a [u8]);
impl<'a> Cursor<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], String> {
        if n > self.0.len() {
            return Err("Truncated Koh ELF metadata".into());
        }
        let (a, b) = self.0.split_at(n);
        self.0 = b;
        Ok(a)
    }
    fn byte(&mut self) -> Result<u8, String> {
        Ok(self.take(1)?[0])
    }
    fn word(&mut self) -> Result<u32, String> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn count(&mut self) -> Result<usize, String> {
        let n = self.word()? as usize;
        if n > self.0.len() {
            return Err("Invalid Koh ELF record count".into());
        }
        Ok(n)
    }
    fn text(&mut self) -> Result<String, String> {
        let n = self.count()?;
        String::from_utf8(self.take(n)?.to_vec()).map_err(|_| "Invalid UTF-8 in Koh ELF".into())
    }
    fn source(&mut self) -> Result<Provenance, String> {
        let file = self.text()?;
        let line = self.word()?;
        let column = self.word()?;
        let n = self.count()?;
        let mut expansion = Vec::new();
        for _ in 0..n {
            expansion.push(SourceLocation {
                file: self.text()?,
                line: self.word()?,
                column: self.word()?,
            });
        }
        Ok(Provenance {
            file,
            line,
            column,
            expansion,
        })
    }
    fn optional_source(&mut self) -> Result<Option<Provenance>, String> {
        match self.byte()? {
            0 => Ok(None),
            1 => Ok(Some(self.source()?)),
            _ => Err("Invalid optional source".into()),
        }
    }
    fn optional_int(&mut self) -> Result<Option<i32>, String> {
        match self.byte()? {
            0 => Ok(None),
            1 => Ok(Some(self.word()? as i32)),
            _ => Err("Invalid optional integer".into()),
        }
    }
    fn reference(&mut self) -> Result<SectionRef, String> {
        match self.byte()? {
            0 => Ok(SectionRef::Local(SectionId(self.word()? as usize))),
            1 => Ok(SectionRef::External(self.text()?)),
            _ => Err("Invalid section reference".into()),
        }
    }
    fn expression(&mut self) -> Result<RelocationExpr, String> {
        let mut stack: Vec<(RelocationExpr, usize)> = Vec::new();
        while !self.0.is_empty() {
            let op = self.byte()?;
            let (value, depth) = match op {
                1 => (RelocationExpr::Literal(self.word()? as i32), 0),
                2 => (RelocationExpr::Symbol(SymbolId(self.word()? as usize)), 0),
                3 => (RelocationExpr::CurrentAddress, 0),
                4 => (RelocationExpr::Bank(SymbolId(self.word()? as usize)), 0),
                5 => (RelocationExpr::BankCurrent, 0),
                6 => (RelocationExpr::BankSection(self.reference()?), 0),
                7 => (RelocationExpr::SectionSize(self.reference()?), 0),
                8 => (RelocationExpr::SectionStart(self.reference()?), 0),
                9 => (RelocationExpr::SectionTypeSize(self.byte()?), 0),
                10 => (RelocationExpr::SectionTypeStart(self.byte()?), 0),
                0x20..=0x26 => {
                    let (x, d) = stack.pop().ok_or("RPN stack underflow")?;
                    let ops = [
                        UnaryOp::Negate,
                        UnaryOp::BitNot,
                        UnaryOp::LogicalNot,
                        UnaryOp::High,
                        UnaryOp::Low,
                        UnaryOp::BitWidth,
                        UnaryOp::TrailingZeros,
                    ];
                    (
                        RelocationExpr::Unary(ops[(op - 0x20) as usize], Box::new(x)),
                        d + 1,
                    )
                }
                0x40..=0x53 => {
                    let (b, db) = stack.pop().ok_or("RPN stack underflow")?;
                    let (a, da) = stack.pop().ok_or("RPN stack underflow")?;
                    let ops = [
                        BinaryOp::Add,
                        BinaryOp::Subtract,
                        BinaryOp::Multiply,
                        BinaryOp::Divide,
                        BinaryOp::Modulo,
                        BinaryOp::Power,
                        BinaryOp::BitOr,
                        BinaryOp::BitAnd,
                        BinaryOp::BitXor,
                        BinaryOp::LogicalAnd,
                        BinaryOp::LogicalOr,
                        BinaryOp::Equal,
                        BinaryOp::NotEqual,
                        BinaryOp::Greater,
                        BinaryOp::Less,
                        BinaryOp::GreaterEqual,
                        BinaryOp::LessEqual,
                        BinaryOp::ShiftLeft,
                        BinaryOp::ShiftRight,
                        BinaryOp::UnsignedShiftRight,
                    ];
                    (
                        RelocationExpr::Binary(ops[(op - 0x40) as usize], Box::new(a), Box::new(b)),
                        da.max(db) + 1,
                    )
                }
                _ => return Err("Unknown Koh RPN opcode".into()),
            };
            if depth > 256 {
                return Err("Koh RPN expression exceeds depth limit".into());
            }
            stack.push((value, depth));
            if stack.len() > 257 {
                return Err("Koh RPN stack exceeds depth limit".into());
            }
        }
        if stack.len() != 1 {
            return Err("Koh RPN must produce one value".into());
        }
        Ok(stack.pop().unwrap().0)
    }
}
fn metadata(object: &Object, order: &[usize]) -> Result<Vec<u8>, String> {
    let mut b = Bytes::default();
    b.word(1);
    b.word(object.sections.len() as u32);
    b.word(object.symbols.len() as u32);
    for s in &object.sections {
        b.byte(s.kind);
        b.byte(s.combination as u8);
        for x in [s.address, s.bank] {
            b.byte(x.is_some() as u8);
            if let Some(x) = x {
                b.word(x as u32)
            }
        }
        b.byte(s.align);
        b.word(s.align_offset as u32);
        b.optional_source(&s.source);
        b.word(s.lines.len() as u32);
        for x in &s.lines {
            b.word(x.offset as u32);
            b.word(x.count as u32);
            b.text(&x.file);
            b.word(x.line);
            b.word(x.expansion.len() as u32);
            for frame in &x.expansion {
                b.text(&frame.file);
                b.word(frame.line);
                b.word(frame.column);
            }
        }
        b.word(s.patches.len() as u32);
        for p in &s.patches {
            b.word(p.offset as u32);
            b.byte(p.width as u8);
            match p.semantics {
                PatchSemantics::Absolute => b.byte(0),
                PatchSemantics::Signed => b.byte(1),
                PatchSemantics::PcRelative { bias } => {
                    b.byte(2);
                    b.word(bias)
                }
                PatchSemantics::HighMemory => b.byte(3),
            }
            b.word(p.pc.section.0 as u32);
            b.word(p.pc.offset);
            b.source(&p.source);
            let mut e = Bytes::default();
            if simple(&p.expression).is_none() {
                e.expression(&p.expression);
            }
            b.word(e.0.len() as u32);
            if e.1 || e.0.len() > MAX_ELF_SIZE - b.0.len() {
                return Err("ELF exceeds 64 MiB output budget".into());
            }
            b.0.extend(e.0);
        }
    }
    for (original, s) in object.symbols.iter().enumerate() {
        b.word(order.iter().position(|x| *x == original).unwrap() as u32 + 1);
        b.byte(s.binding as u8);
        b.optional_source(&s.source)
    }
    if b.1 {
        return Err("ELF exceeds 64 MiB output budget".into());
    }
    Ok(b.0)
}
fn simple(e: &RelocationExpr) -> Option<(Option<usize>, i32)> {
    match e {
        RelocationExpr::Literal(x) => Some((None, *x)),
        RelocationExpr::Symbol(x) => Some((Some(x.0), 0)),
        RelocationExpr::Binary(BinaryOp::Add, a, b) => match (&**a, &**b) {
            (RelocationExpr::Symbol(x), RelocationExpr::Literal(v))
            | (RelocationExpr::Literal(v), RelocationExpr::Symbol(x)) => Some((Some(x.0), *v)),
            _ => None,
        },
        _ => None,
    }
}
fn relocation_type(p: &Patch) -> u8 {
    if simple(&p.expression).is_none() {
        255
    } else {
        match p.semantics {
            PatchSemantics::Absolute => 1 + p.width as u8,
            PatchSemantics::Signed => 4 + p.width as u8,
            PatchSemantics::PcRelative { .. } => 7 + p.width as u8,
            PatchSemantics::HighMemory => 10,
        }
    }
}
pub fn write_elf(object: &Object) -> Result<Vec<u8>, String> {
    object.validate()?;
    if object.sections.len() > 65000 || object.symbols.len() > 0xfffffe {
        return Err("ELF32 table limit exceeded".into());
    }
    let mut order: Vec<usize> = (0..object.symbols.len()).collect();
    order.sort_by_key(|i| {
        matches!(object.symbols[*i].definition, SymbolDefinition::Import)
            || object.symbols[*i].binding == SymbolBinding::Exported
    });
    let meta = metadata(object, &order)?;
    let mut budget = 52u64;
    for section in &object.sections {
        let alignment = if section.align_offset == 0 {
            1u64 << section.align
        } else {
            1
        };
        budget = (budget + alignment - 1) & !(alignment - 1);
        budget += section.data.len() as u64;
        if budget > MAX_ELF_SIZE as u64 {
            return Err("ELF exceeds 64 MiB output budget".into());
        }
    }
    budget += meta.len() as u64 + 1024;
    for section in &object.sections {
        budget += section.name.len() as u64 + 1 + section.patches.len() as u64 * 12 + 128;
    }
    for symbol in &object.symbols {
        budget += symbol.name.len() as u64 + 32;
    }
    if budget > MAX_ELF_SIZE as u64 {
        return Err("ELF exceeds 64 MiB output budget".into());
    }
    let mut out = Vec::new();
    let mut header = Vec::new();
    {
        let mut w = Writer::new_single_phase(Endianness::Little, false, &mut out);
        w.write_file_header(&FileHeader {
            os_abi: elf::ELFOSABI_NONE,
            abi_version: 0,
            e_type: elf::ET_REL,
            e_machine: elf::EM_NONE,
            e_entry: 0,
            e_flags: elf::FileFlags(FLAGS),
        })
        .map_err(|e| e.to_string())?;
        let mut headers = Vec::new();
        for s in &object.sections {
            let name = w.add_section_name(s.name.as_bytes());
            let alignment = if s.align_offset == 0 {
                1u64 << s.align
            } else {
                1
            };
            let offset = w.write_align(alignment);
            if s.kind <= 1 {
                w.write(&s.data)
            }
            headers.push(SectionHeader {
                sh_name: w.section_name_offset(Some(name)),
                sh_type: if s.kind <= 1 {
                    elf::SHT_PROGBITS
                } else {
                    elf::SHT_NOBITS
                },
                sh_flags: if s.kind <= 1 {
                    elf::SHF_ALLOC | elf::SHF_EXECINSTR
                } else {
                    elf::SHF_ALLOC | elf::SHF_WRITE
                },
                sh_offset: offset,
                sh_size: s.size as u64,
                sh_addralign: alignment,
                ..Default::default()
            });
        }
        for (name, data, kind) in [
            (b".note.koh".as_slice(), NOTE, elf::SHT_NOTE),
            (b".koh.meta".as_slice(), meta.as_slice(), elf::SHT_PROGBITS),
        ] {
            let id = w.add_section_name(name);
            let offset = w.write_align(4);
            w.write(data);
            headers.push(SectionHeader {
                sh_name: w.section_name_offset(Some(id)),
                sh_type: kind,
                sh_offset: offset,
                sh_size: data.len() as u64,
                sh_addralign: 4,
                ..Default::default()
            });
        }
        let mut rela_headers = Vec::new();
        let rela_names: Vec<String> = object
            .sections
            .iter()
            .enumerate()
            .filter(|(_, s)| !s.patches.is_empty())
            .map(|(i, _)| format!(".rela.koh.{i}"))
            .collect();
        for ((i, s), name) in object
            .sections
            .iter()
            .enumerate()
            .filter(|(_, s)| !s.patches.is_empty())
            .zip(&rela_names)
        {
            let id = w.add_section_name(name.as_bytes());
            let offset = w.write_align_relocation();
            for p in &s.patches {
                let (symbol, addend) = simple(&p.expression).unwrap_or((None, 0));
                let r_sym = symbol
                    .map(|x| order.iter().position(|i| *i == x).unwrap() as u32 + 1)
                    .unwrap_or(0);
                w.write_relocation(
                    true,
                    &object::write::elf::Rel {
                        r_offset: p.offset as u64,
                        r_sym,
                        r_type: elf::RelocationType(relocation_type(p) as u32),
                        r_addend: addend as i64,
                    },
                );
            }
            rela_headers.push(SectionHeader {
                sh_name: w.section_name_offset(Some(id)),
                sh_type: elf::SHT_RELA,
                sh_offset: offset,
                sh_size: s.patches.len() as u64 * 12,
                sh_link: 0,
                sh_info: i as u32 + 1,
                sh_addralign: 4,
                sh_entsize: 12,
                ..Default::default()
            });
        }
        w.write_null_symbol();
        for i in &order {
            let s = &object.symbols[*i];
            let id = w.add_string(s.name.as_bytes());
            let (section, st_shndx, value) = match s.definition {
                SymbolDefinition::Import => (None, elf::SHN_UNDEF, 0),
                SymbolDefinition::Absolute(v) => (None, elf::SHN_ABS, v as u32 as u64),
                SymbolDefinition::Label { section, offset } => {
                    (Some(section.0 as u32 + 1), elf::SHN_UNDEF, offset as u64)
                }
            };
            w.write_symbol(&Sym {
                section,
                st_name: w.string_offset(Some(id)),
                st_info: if matches!(s.definition, SymbolDefinition::Import)
                    || s.binding == SymbolBinding::Exported
                {
                    elf::STB_GLOBAL | elf::STT_NOTYPE
                } else {
                    elf::STB_LOCAL | elf::STT_NOTYPE
                },
                st_other: elf::SymbolOther(0),
                st_shndx,
                st_value: value,
                st_size: 0,
            });
        }
        w.write_strtab().map_err(|e| e.to_string())?;
        w.write_shstrtab().map_err(|e| e.to_string())?;
        w.write_null_section_header();
        for h in &headers {
            w.write_section_header(h);
        }
        w.write_strtab_section_header();
        let symtab_index = headers.len() as u32 + 2;
        let locals = order
            .iter()
            .take_while(|i| {
                object.symbols[**i].binding == SymbolBinding::Local
                    && !matches!(object.symbols[**i].definition, SymbolDefinition::Import)
            })
            .count() as u32
            + 1;
        w.write_symtab_section_header(locals);
        for mut h in rela_headers {
            h.sh_link = symtab_index;
            w.write_section_header(&h);
        }
        w.write_shstrtab_section_header();
        w.write_file_header_to(&mut header)
            .map_err(|e| e.to_string())?;
    }
    out[..header.len()].copy_from_slice(&header);
    Ok(out)
}
pub fn read_elf(data: &[u8]) -> Result<Object, String> {
    use object::read::elf::FileHeader as _;
    let file =
        object::read::elf::ElfFile32::<Endianness>::parse(data).map_err(|e| e.to_string())?;
    let h = file.elf_header();
    let endian = file.endian();
    if !file.is_little_endian()
        || h.e_type(endian) != elf::ET_REL
        || h.e_machine(endian) != elf::EM_NONE
        || h.e_flags(endian).0 != FLAGS
        || h.e_entry(endian) != 0
        || h.e_phnum(endian) != 0
    {
        return Err("Unsupported Koh ELF header".into());
    }
    let note = file
        .sections()
        .filter(|s| s.name().ok() == Some(".note.koh"))
        .last()
        .ok_or("Missing Koh SM83 identification note")?;
    if note.data().map_err(|e| e.to_string())? != NOTE {
        return Err("Unsupported Koh SM83 note or ABI".into());
    }
    let meta = file
        .sections()
        .filter(|s| s.name().ok() == Some(".koh.meta"))
        .last()
        .ok_or("Missing Koh metadata")?;
    let mut c = Cursor(meta.data().map_err(|e| e.to_string())?);
    if c.word()? != 1 {
        return Err("Unsupported Koh metadata version".into());
    }
    let ns = c.count()?;
    let ny = c.count()?;
    if ns > 65000 || ny > 0xfffffe {
        return Err("ELF32 table limit exceeded".into());
    }
    let mut copied_data = 0usize;
    let mut object = Object::default();
    let mut pending = Vec::new();
    let mut symbol_ids = Vec::new();
    for i in 0..ns {
        let raw = file
            .section_by_index(object::SectionIndex(i + 1))
            .map_err(|e| e.to_string())?;
        let kind = c.byte()?;
        let capacity = memory_region(kind).ok_or("Invalid section kind")?.1;
        if raw.size() > u64::from(capacity) {
            return Err("ELF section exceeds memory region capacity".into());
        }
        let section_data = raw.data().map_err(|e| e.to_string())?;
        if (kind <= 1 && section_data.len() as u64 != raw.size())
            || (kind > 1 && !section_data.is_empty())
        {
            return Err("ELF section data does not match its memory region".into());
        }
        copied_data = copied_data
            .checked_add(section_data.len())
            .ok_or("ELF section data size overflow")?;
        if copied_data > data.len() {
            return Err("ELF section data exceeds input allocation budget".into());
        }
        let combination = match c.byte()? {
            0 => SectionCombination::Normal,
            1 => SectionCombination::Union,
            2 => SectionCombination::Fragment,
            _ => return Err("Invalid section combination".into()),
        };
        let address = c.optional_int()?;
        let bank = c.optional_int()?;
        let align = c.byte()?;
        let align_offset = c.word()? as i32;
        let source = c.optional_source()?;
        let n = c.count()?;
        let mut lines = Vec::new();
        for _ in 0..n {
            lines.push(Line {
                offset: c.word()? as usize,
                count: c.word()? as usize,
                file: c.text()?,
                line: c.word()?,
                expansion: {
                    let count = c.count()?;
                    let mut frames = Vec::new();
                    for _ in 0..count {
                        frames.push(SourceLocation {
                            file: c.text()?,
                            line: c.word()?,
                            column: c.word()?,
                        });
                    }
                    frames
                },
            })
        }
        let n = c.count()?;
        let mut patches = Vec::new();
        for _ in 0..n {
            let offset = c.word()? as usize;
            let width = match c.byte()? {
                0 => PatchWidth::Byte,
                1 => PatchWidth::Word,
                2 => PatchWidth::Long,
                _ => return Err("Invalid patch width".into()),
            };
            let semantics = match c.byte()? {
                0 => PatchSemantics::Absolute,
                1 => PatchSemantics::Signed,
                2 => PatchSemantics::PcRelative { bias: c.word()? },
                3 => PatchSemantics::HighMemory,
                _ => return Err("Invalid patch semantics".into()),
            };
            let pc = AddressContext {
                section: SectionId(c.word()? as usize),
                offset: c.word()?,
            };
            let source = c.source()?;
            let len = c.count()?;
            let expression = if len == 0 {
                pending.push((i, patches.len()));
                RelocationExpr::Literal(0)
            } else {
                Cursor(c.take(len)?).expression()?
            };
            patches.push(Patch {
                offset,
                width,
                semantics,
                pc,
                source,
                expression,
            });
        }
        object.sections.push(Section {
            name: raw.name().map_err(|e| e.to_string())?.into(),
            kind,
            combination,
            address,
            bank,
            align,
            align_offset,
            size: raw.size().try_into().map_err(|_| "Section exceeds ELF32")?,
            data: section_data.to_vec(),
            patches,
            lines,
            source,
        });
    }
    for _ in 0..ny {
        let index = c.word()? as usize;
        symbol_ids.push(index);
        let binding = match c.byte()? {
            0 => SymbolBinding::Local,
            1 => SymbolBinding::Exported,
            _ => return Err("Invalid symbol binding".into()),
        };
        let source = c.optional_source()?;
        let raw = file
            .symbol_by_index(object::SymbolIndex(index))
            .map_err(|e| e.to_string())?;
        let definition = if raw.is_undefined() {
            SymbolDefinition::Import
        } else if raw.section() == object::SymbolSection::Absolute {
            SymbolDefinition::Absolute(raw.address() as u32 as i32)
        } else {
            let section = raw.section_index().ok_or("Invalid symbol section")?.0;
            if section == 0 || section > ns {
                return Err("Symbol section outside Koh sections".into());
            }
            SymbolDefinition::Label {
                section: SectionId(section - 1),
                offset: raw
                    .address()
                    .try_into()
                    .map_err(|_| "Symbol value exceeds ELF32")?,
            }
        };
        object.symbols.push(Symbol {
            name: raw.name().map_err(|e| e.to_string())?.into(),
            binding,
            definition,
            source,
        });
    }
    for (si, pi) in pending {
        let raw = file
            .sections()
            .filter(|s| s.name().ok() == Some(format!(".rela.koh.{si}").as_str()))
            .last()
            .ok_or("Missing RELA section")?;
        let bytes = raw.data().map_err(|e| e.to_string())?;
        let mut r = Cursor(
            bytes
                .get(pi * 12..pi * 12 + 12)
                .ok_or("Missing RELA record")?,
        );
        let _offset = r.word()?;
        let info = r.word()?;
        let addend = r.word()? as i32;
        let symbol = info >> 8;
        let expression = if symbol == 0 {
            RelocationExpr::Literal(addend)
        } else {
            let index = symbol_ids
                .iter()
                .position(|x| *x == symbol as usize)
                .ok_or("Unknown RELA symbol")?;
            let value = RelocationExpr::Symbol(SymbolId(index));
            if addend == 0 {
                value
            } else {
                RelocationExpr::Binary(
                    BinaryOp::Add,
                    Box::new(value),
                    Box::new(RelocationExpr::Literal(addend)),
                )
            }
        };
        object.sections[si].patches[pi].expression = expression;
    }
    if !c.0.is_empty() {
        return Err("Trailing Koh metadata".into());
    }
    object.validate()?;
    if write_elf(&object)? != data {
        return Err("Noncanonical or inconsistent Koh ELF records".into());
    }
    Ok(object)
}

#[cfg(test)]
mod tests {
    use super::Cursor;
    #[test]
    fn rpn_pending_operands_obey_expression_depth_limit() {
        let mut valid = vec![3; 257];
        valid.extend(vec![0x40; 256]);
        assert!(Cursor(&valid).expression().is_ok());
        let invalid = vec![3; 258];
        assert!(
            Cursor(&invalid)
                .expression()
                .unwrap_err()
                .contains("stack exceeds")
        );
    }
}
