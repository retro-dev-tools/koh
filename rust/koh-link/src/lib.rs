#[cfg(test)]
use koh_objects::evaluate_binary as binary;
use koh_objects::*;
use std::collections::{BTreeMap, HashMap};

pub const MAX_ROM_SIZE: usize = 8 * 1024 * 1024;
const BOOT_LOGO: [u8; 48] = [
    0xce, 0xed, 0x66, 0x66, 0xcc, 0x0d, 0, 0x0b, 3, 0x73, 0, 0x83, 0, 0x0c, 0, 0x0d, 0, 8, 0x11,
    0x1f, 0x88, 0x89, 0, 0x0e, 0xdc, 0xcc, 0x6e, 0xe6, 0xdd, 0xdd, 0xd9, 0x99, 0xbb, 0xbb, 0x67,
    0x63, 0x6e, 0x0e, 0xec, 0xcc, 0xdd, 0xdc, 0x99, 0x9f, 0xbb, 0xb9, 0x33, 0x3e,
];
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CgbMode {
    #[default]
    Preserve,
    Compatible,
    Only,
}
#[derive(Clone, Debug, Default)]
pub struct LinkOptions {
    pub raw: bool,
    pub size: Option<usize>,
    pub cgb: CgbMode,
}
#[derive(Clone, Debug, serde::Serialize)]
pub struct LinkedSymbol {
    pub name: String,
    pub value: i32,
    pub bank: Option<u32>,
    pub kind: u8,
    pub source: Option<Provenance>,
}
#[derive(Clone, Debug, serde::Serialize)]
pub struct LinkedLine {
    pub bank: u32,
    pub address: u32,
    pub count: u32,
    pub file: String,
    pub line: u32,
    pub expansion: Vec<SourceLocation>,
}
#[derive(Clone, Debug)]
pub struct LinkResult {
    pub rom: Vec<u8>,
    pub symbols: Vec<LinkedSymbol>,
    pub lines: Vec<LinkedLine>,
}
#[derive(Clone, Copy, Default)]
struct Placement {
    address: u32,
    bank: u32,
    group: usize,
}
struct Group {
    name: String,
    kind: u8,
    combination: SectionCombination,
    size: u32,
    address: Option<u32>,
    bank: Option<u32>,
    align: u8,
    offset: u32,
}

pub fn link(objects: &[Object], options: &LinkOptions) -> Result<LinkResult, String> {
    if options.size.is_some_and(|n| n == 0 || n > MAX_ROM_SIZE) {
        return Err("Image size must be between 1 byte and 8 MiB".into());
    }
    let mut groups: Vec<Group> = Vec::new();
    let mut names = HashMap::new();
    let mut placements = Vec::new();
    for object in objects {
        object.validate()?;
        let mut local = Vec::new();
        for s in &object.sections {
            if names.contains_key(&s.name) && s.combination == SectionCombination::Normal {
                return Err(format!("Duplicate section '{}'", s.name));
            }
            let g = if let Some(&g) = names.get(&s.name) {
                g
            } else {
                let g = groups.len();
                names.insert(s.name.clone(), g);
                groups.push(Group {
                    name: s.name.clone(),
                    kind: s.kind,
                    combination: s.combination,
                    size: 0,
                    address: None,
                    bank: None,
                    align: 0,
                    offset: 0,
                });
                g
            };
            let group = &mut groups[g];
            if group.kind != s.kind
                || group.combination != s.combination
                || (group.size != 0 && s.combination == SectionCombination::Normal)
            {
                return Err(format!("Incompatible duplicate section '{}'", s.name));
            }
            if s.combination == SectionCombination::Normal
                && local.iter().any(|p: &Placement| p.group == g)
            {
                return Err(format!("Duplicate section '{}'", s.name));
            }
            let base = if s.combination == SectionCombination::Fragment {
                group.size
            } else {
                0
            };
            if let Some(address) = s.address {
                let address = (address as u32)
                    .checked_sub(base)
                    .ok_or("Fragment address precedes combined section")?;
                if group.address.is_some_and(|a| a != address) {
                    return Err(format!("Conflicting addresses for '{}'", s.name));
                }
                group.address = Some(address);
            }
            if let Some(bank) = s.bank {
                if group.bank.is_some_and(|b| b != bank as u32) {
                    return Err(format!("Conflicting banks for '{}'", s.name));
                }
                group.bank = Some(bank as u32);
            }
            let mask = (1u32 << s.align) - 1;
            let offset = (s.align_offset as u32).wrapping_sub(base) & mask;
            let common = (1u32 << group.align.min(s.align)) - 1;
            if (group.offset ^ offset) & common != 0 {
                return Err(format!("Conflicting alignment for '{}'", s.name));
            }
            if s.align > group.align {
                group.align = s.align;
                group.offset = offset;
            }
            group.size = if s.combination == SectionCombination::Union {
                group.size.max(s.size)
            } else {
                group
                    .size
                    .checked_add(s.size)
                    .ok_or("Section size overflow")?
            };
            if group.size > memory_region(s.kind).unwrap().1 {
                return Err(format!("Combined section '{}' exceeds region", s.name));
            }
            local.push(Placement {
                address: base,
                bank: 0,
                group: g,
            });
        }
        placements.push(local);
    }
    let mut order: Vec<usize> = (0..groups.len()).collect();
    order.sort_by_key(|&g| {
        (
            groups[g].address.is_none(),
            groups[g].bank.is_none(),
            std::cmp::Reverse(groups[g].align),
            std::cmp::Reverse(groups[g].size),
            g,
        )
    });
    let mut occupied: BTreeMap<(u8, u32), Vec<(u32, u32)>> = BTreeMap::new();
    let mut located = vec![Placement::default(); groups.len()];
    for g in order {
        let s = &groups[g];
        let (start, length, first, last) = memory_region(s.kind).unwrap();
        let mask = (1u32 << s.align) - 1;
        if s.address.is_some_and(|a| a & mask != s.offset) {
            return Err(format!("Address violates alignment for '{}'", s.name));
        }
        let mut found = None;
        for bank in s.bank.unwrap_or(first as u32)..=s.bank.unwrap_or(last as u32) {
            let ranges = occupied.entry((s.kind, bank)).or_default();
            ranges.sort_unstable();
            let mut address = s.address.unwrap_or(start);
            if s.address.is_none() {
                address += s.offset.wrapping_sub(address) & mask;
            }
            for &(a, b) in ranges.iter() {
                if address < b && address.saturating_add(s.size) > a {
                    if s.address.is_some() {
                        address = start + length;
                        break;
                    }
                    address = b + (s.offset.wrapping_sub(b) & mask);
                }
            }
            if address >= start
                && address
                    .checked_add(s.size)
                    .is_some_and(|end| end <= start + length)
            {
                ranges.push((address, address + s.size));
                found = Some(Placement {
                    address,
                    bank,
                    group: g,
                });
                break;
            }
        }
        located[g]=found.ok_or_else(||{
            let bank=s.bank.unwrap_or(first as u32);let ranges=occupied.get(&(s.kind,bank));let mut cursor=start;let mut total=0;let mut largest=0;
            if let Some(ranges)=ranges{for &(a,b) in ranges{let free=a.saturating_sub(cursor);total+=free;largest=largest.max(free);cursor=cursor.max(b);}}
            let free=(start+length).saturating_sub(cursor);total+=free;largest=largest.max(free);
            let conflicts:Vec<_>=groups.iter().enumerate().filter(|(i,other)|*i!=g&&other.kind==s.kind&&located[*i].bank==bank&&occupied.get(&(other.kind,bank)).is_some_and(|ranges|ranges.contains(&(located[*i].address,located[*i].address+other.size)))).map(|(_,other)|other.name.as_str()).collect();
            format!("Cannot place section '{}' (size {:#x}, region capacity {length:#x}, bank {bank}, address {:?}); largest free block {largest:#x}, total free {total:#x}, overflow {:#x}; occupied by {}",s.name,s.size,s.address,s.size.saturating_sub(largest),conflicts.join(", "))
        })?;
    }
    for local in &mut placements {
        for p in local {
            p.address += located[p.group].address;
            p.bank = located[p.group].bank;
        }
    }
    let mut exports = HashMap::new();
    for (oi, o) in objects.iter().enumerate() {
        for (si, s) in o.symbols.iter().enumerate() {
            if s.binding == SymbolBinding::Exported
                && exports.insert(s.name.clone(), (oi, si)).is_some()
            {
                return Err(format!("Duplicate exported symbol '{}'", s.name));
            }
        }
    }
    let context = Context {
        objects,
        placements: &placements,
        groups: &groups,
        located: &located,
        exports,
        section_names: names,
    };
    let mut symbols = Vec::new();
    let mut lines = Vec::new();
    let mut writes = Vec::new();
    let mut required = 0;
    for (oi, o) in objects.iter().enumerate() {
        for (si, s) in o.symbols.iter().enumerate() {
            let (value, bank) = context.symbol(oi, SymbolId(si))?;
            if s.definition != SymbolDefinition::Import {
                symbols.push(LinkedSymbol{name:s.name.clone(),value,bank,kind:if bank.is_none(){1}else if matches!(s.definition,SymbolDefinition::Label{section,..} if o.sections[section.0].kind>1){2}else{0},source:s.source.clone()});
            }
        }
        for (si, s) in o.sections.iter().enumerate() {
            let p = placements[oi][si];
            for l in &s.lines {
                if l.count > 0 {
                    let expansion = l.expansion.clone();
                    lines.push(LinkedLine {
                        bank: p.bank,
                        address: p.address + l.offset as u32,
                        count: l.count as u32,
                        file: l.file.clone(),
                        line: l.line,
                        expansion,
                    });
                }
            }
            if s.kind > 1 {
                continue;
            }
            let mut data = s.data.clone();
            for patch in &s.patches {
                let pc = placements[oi][patch.pc.section.0];
                let value =
                    context.eval(oi, &patch.expression, pc.address + patch.pc.offset, pc.bank)?;
                let value =
                    patch_value(value, patch, pc.address + patch.pc.offset).map_err(|e| {
                        format!(
                            "{}:{}:{}: {e}",
                            patch.source.file, patch.source.line, patch.source.column
                        )
                    })?;
                let bytes = value.to_le_bytes();
                data[patch.offset..patch.offset + patch.width.bytes()]
                    .copy_from_slice(&bytes[..patch.width.bytes()]);
            }
            let offset = if s.kind == 0 {
                p.address as usize
            } else {
                p.bank as usize * 0x4000 + p.address as usize - 0x4000
            };
            let end = offset.checked_add(data.len()).ok_or("ROM size overflow")?;
            if end > MAX_ROM_SIZE {
                return Err("ROM exceeds 8 MiB cartridge limit".into());
            }
            required = required.max(end);
            writes.push((offset, data));
        }
    }
    let requested = options.size.unwrap_or(0x8000);
    let size = if options.raw {
        if required > requested {
            return Err(format!(
                "Raw image requires {required:#x} bytes, requested size is {requested} ({requested:#x})"
            ));
        }
        requested
    } else {
        required.max(requested).max(0x8000).next_power_of_two()
    };
    if size > MAX_ROM_SIZE {
        return Err("ROM exceeds 8 MiB cartridge limit".into());
    }
    let mut rom = vec![0; size];
    for (offset, data) in writes {
        rom[offset..offset + data.len()].copy_from_slice(&data);
    }
    if !options.raw {
        rom[0x104..0x134].copy_from_slice(&BOOT_LOGO);
        match options.cgb {
            CgbMode::Compatible => rom[0x143] = 0x80,
            CgbMode::Only => rom[0x143] = 0xc0,
            _ => {}
        }
        rom[0x148] = (size / 0x8000).ilog2() as u8;
        rom[0x14d] = rom[0x134..=0x14c]
            .iter()
            .fold(0u8, |a, b| a.wrapping_sub(*b).wrapping_sub(1));
        let sum = rom
            .iter()
            .enumerate()
            .filter(|(i, _)| *i != 0x14e && *i != 0x14f)
            .fold(0u16, |a, (_, b)| a.wrapping_add(*b as u16));
        rom[0x14e..=0x14f].copy_from_slice(&sum.to_be_bytes());
    }
    symbols.sort_by(|a, b| (a.bank, a.value, &a.name).cmp(&(b.bank, b.value, &b.name)));
    lines.sort_by(|a, b| {
        (a.bank, a.address, &a.file, a.line).cmp(&(b.bank, b.address, &b.file, b.line))
    });
    Ok(LinkResult {
        rom,
        symbols,
        lines,
    })
}

fn patch_value(value: i32, p: &Patch, pc: u32) -> Result<i32, String> {
    let bits = p.width.bytes() * 8;
    let value = match p.semantics {
        PatchSemantics::PcRelative { bias } => {
            if !(-32768..=65535).contains(&value) {
                return Err(format!("JR target {value} outside 16-bit address range"));
            }
            i64::from(
                (value as u16)
                    .wrapping_sub(pc as u16)
                    .wrapping_sub(bias as u16) as i16,
            )
        }
        PatchSemantics::HighMemory => {
            if !(0..=255).contains(&value) && !(0xff00..=0xffff).contains(&value) {
                return Err("High-memory address outside $FF00-$FFFF".into());
            }
            i64::from(value & 255)
        }
        _ => i64::from(value),
    };
    let min = -(1i64 << (bits - 1));
    let max = if matches!(
        p.semantics,
        PatchSemantics::Signed | PatchSemantics::PcRelative { .. }
    ) {
        (1i64 << (bits - 1)) - 1
    } else {
        (1i64 << bits) - 1
    };
    if value < min || value > max {
        return Err(format!("Patch value {value} outside {bits}-bit range"));
    }
    Ok(value as i32)
}

struct Context<'a> {
    objects: &'a [Object],
    placements: &'a [Vec<Placement>],
    groups: &'a [Group],
    located: &'a [Placement],
    exports: HashMap<String, (usize, usize)>,
    section_names: HashMap<String, usize>,
}
impl Context<'_> {
    fn symbol(&self, oi: usize, id: SymbolId) -> Result<(i32, Option<u32>), String> {
        let s = &self.objects[oi].symbols[id.0];
        match s.definition {
            SymbolDefinition::Absolute(v) => Ok((v, None)),
            SymbolDefinition::Label { section, offset } => {
                let p = self.placements[oi][section.0];
                Ok(((p.address + offset) as i32, Some(p.bank)))
            }
            SymbolDefinition::Import => {
                let &(o, i) = self
                    .exports
                    .get(&s.name)
                    .ok_or_else(|| format!("Undefined symbol '{}'", s.name))?;
                self.symbol(o, SymbolId(i))
            }
        }
    }
    fn section(&self, oi: usize, r: &SectionRef) -> Result<usize, String> {
        match r {
            SectionRef::Local(id) => Ok(self.placements[oi][id.0].group),
            SectionRef::External(n) => self
                .section_names
                .get(n)
                .copied()
                .ok_or_else(|| format!("Undefined section '{n}'")),
        }
    }
    fn eval(&self, oi: usize, e: &RelocationExpr, pc: u32, bank: u32) -> Result<i32, String> {
        Ok(match e {
            RelocationExpr::Literal(v) => *v,
            RelocationExpr::Symbol(id) => self.symbol(oi, *id)?.0,
            RelocationExpr::Bank(id) => {
                self.symbol(oi, *id)?.1.ok_or("BANK requires a label")? as i32
            }
            RelocationExpr::CurrentAddress => pc as i32,
            RelocationExpr::BankCurrent => bank as i32,
            RelocationExpr::BankSection(r) => self.located[self.section(oi, r)?].bank as i32,
            RelocationExpr::SectionSize(r) => self.groups[self.section(oi, r)?].size as i32,
            RelocationExpr::SectionStart(r) => self.located[self.section(oi, r)?].address as i32,
            RelocationExpr::SectionTypeSize(k) => {
                memory_region(*k).ok_or("Invalid region")?.1 as i32
            }
            RelocationExpr::SectionTypeStart(k) => {
                memory_region(*k).ok_or("Invalid region")?.0 as i32
            }
            RelocationExpr::Unary(op, v) => {
                let v = self.eval(oi, v, pc, bank)?;
                evaluate_unary(*op, v)
            }
            RelocationExpr::Binary(op, a, b) => evaluate_binary(
                *op,
                self.eval(oi, a, pc, bank)?,
                self.eval(oi, b, pc, bank)?,
            )?,
        })
    }
}

impl LinkResult {
    pub fn sym(&self) -> String {
        let mut out = String::from("; koh-link symbol file\n");
        for s in &self.symbols {
            if let Some(bank) = s.bank {
                out.push_str(&format!("{bank:02X}:{:04X} {}\n", s.value, s.name));
            }
        }
        out
    }
    pub fn dwarf(&self) -> Result<Vec<u8>, String> {
        crate::dwarf::write(self)
    }
}
mod dwarf;
mod dwarf_read;
pub use dwarf_read::{DwarfDebugInfo, read_dwarf};
#[cfg(test)]
mod tests;
