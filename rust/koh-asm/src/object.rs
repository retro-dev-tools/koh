use crate::{expr::Expr, model::Object};
use std::collections::{HashMap, HashSet};

fn int(out: &mut Vec<u8>, value: i32) {
    out.extend(value.to_le_bytes());
}
fn count(out: &mut Vec<u8>, value: usize) -> Result<(), String> {
    int(
        out,
        i32::try_from(value).map_err(|_| "Object count exceeds 32 bits")?,
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
fn validate(object: &Object) -> Result<(), String> {
    let mut sections = HashSet::new();
    for s in &object.sections {
        if !sections.insert(&s.name) {
            return Err(format!("Duplicate section '{}'", s.name));
        }
        if s.kind > 7 {
            return Err("Invalid section kind".into());
        }
        if s.align > 16 || s.align_offset < 0 || i64::from(s.align_offset) >= (1i64 << s.align) {
            return Err("Invalid section alignment".into());
        }
        for p in &s.patches {
            let width = match p.kind {
                0 | 3 => 1,
                1 => 2,
                2 => 4,
                _ => return Err("Invalid patch kind".into()),
            };
            if p.offset
                .checked_add(width)
                .is_none_or(|end| end > s.data.len())
            {
                return Err("Patch outside section".into());
            }
        }
        for l in &s.lines {
            if l.offset
                .checked_add(l.count)
                .is_none_or(|end| end > s.data.len())
            {
                return Err("Line map outside section".into());
            }
        }
    }
    let mut symbols = HashSet::new();
    for s in &object.symbols {
        if !symbols.insert(&s.name) {
            return Err(format!("Duplicate symbol '{}'", s.name));
        }
        if s.kind > 4 || s.visibility > 2 || s.section.is_some_and(|id| id >= object.sections.len())
        {
            return Err("Invalid symbol record".into());
        }
    }
    Ok(())
}

pub fn write_kobj(object: &Object) -> Result<Vec<u8>, String> {
    validate(object)?;
    let mut out = b"KOH\0\x04\x01".to_vec();
    short(&mut out, object.sections.len())?;
    for s in &object.sections {
        if s.align != 0
            && !s
                .address
                .is_some_and(|address| address & ((1 << s.align) - 1) == s.align_offset)
        {
            return Err("Kobj v4 cannot encode floating alignment; use RGBDS output".into());
        }
        string(&mut out, &s.name, false)?;
        out.push(s.kind);
        for option in [s.address, s.bank] {
            out.push(u8::from(option.is_some()));
            if let Some(value) = option {
                int(&mut out, value);
            }
        }
        count(&mut out, s.data.len())?;
        out.extend(&s.data);
        short(&mut out, s.patches.len())?;
        for p in &s.patches {
            if p.symbol.is_none() {
                return Err("Kobj v4 cannot encode complex relocation; use RGBDS output".into());
            }
            count(&mut out, p.offset)?;
            out.push(p.kind);
            int(&mut out, p.pc_after);
            int(&mut out, 0);
            int(&mut out, 0);
            string(&mut out, p.symbol.as_deref().unwrap_or(""), false)?;
            int(&mut out, p.addend);
            int(&mut out, p.shift);
        }
        count(&mut out, s.lines.len())?;
        for l in &s.lines {
            count(&mut out, l.offset)?;
            count(&mut out, l.count)?;
            string(&mut out, &l.file, false)?;
            out.extend(l.line.to_le_bytes());
        }
    }
    out.push(2);
    short(&mut out, object.symbols.len())?;
    for s in &object.symbols {
        string(&mut out, &s.name, false)?;
        out.extend([s.kind, s.visibility]);
        string(
            &mut out,
            s.section
                .map(|id| object.sections[id].name.as_str())
                .unwrap_or(""),
            false,
        )?;
        out.extend(i64::from(s.value).to_le_bytes());
    }
    out.push(255);
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Line, Patch, Section, Symbol};

    fn fixture() -> Object {
        Object {
            sections: vec![Section {
                name: "Code".into(),
                data: vec![0xc3, 0, 0, 0, 0],
                patches: vec![Patch {
                    offset: 1,
                    kind: 2,
                    pc_after: 5,
                    pc_offset: 0,
                    expression: Expr::Symbol("target".into()),
                    file: "main.asm".into(),
                    line: 7,
                    symbol: Some("target".into()),
                    addend: 3,
                    shift: 8,
                }],
                lines: vec![Line {
                    offset: 0,
                    count: 5,
                    file: "main.asm".into(),
                    line: 7,
                }],
                ..Section::default()
            }],
            symbols: vec![Symbol {
                name: "target".into(),
                kind: 0,
                visibility: 2,
                section: None,
                value: 0,
            }],
        }
    }

    struct Reader {
        bytes: Vec<u8>,
        pos: usize,
    }
    impl Reader {
        fn take(&mut self, count: usize) -> Vec<u8> {
            let result = self.bytes[self.pos..self.pos + count].to_vec();
            self.pos += count;
            result
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
            let mut result = Vec::new();
            loop {
                let b = self.byte();
                if b == 0 {
                    break;
                }
                result.push(b);
            }
            String::from_utf8(result).unwrap()
        }
        fn netstr(&mut self) -> String {
            let mut len = 0usize;
            let mut shift = 0;
            loop {
                let b = self.byte();
                len |= usize::from(b & 127) << shift;
                shift += 7;
                if b < 128 {
                    break;
                }
            }
            String::from_utf8(self.take(len)).unwrap()
        }
        fn done(&self) {
            assert_eq!(self.pos, self.bytes.len());
        }
    }

    #[test]
    fn kobj_v4_layout_preserves_relocations_symbols_and_lines() {
        let mut r = Reader {
            bytes: write_kobj(&fixture()).unwrap(),
            pos: 0,
        };
        assert_eq!(r.take(6), b"KOH\0\x04\x01");
        assert_eq!(r.short(), 1);
        assert_eq!(r.netstr(), "Code");
        assert_eq!(r.take(3), [0, 0, 0]);
        assert_eq!(r.int(), 5);
        assert_eq!(r.take(5), [0xc3, 0, 0, 0, 0]);
        assert_eq!(r.short(), 1);
        assert_eq!(r.int(), 1);
        assert_eq!(r.byte(), 2);
        assert_eq!(r.int(), 5);
        assert_eq!(r.int(), 0);
        assert_eq!(r.int(), 0);
        assert_eq!(r.netstr(), "target");
        assert_eq!(r.int(), 3);
        assert_eq!(r.int(), 8);
        assert_eq!(r.int(), 1);
        assert_eq!(r.int(), 0);
        assert_eq!(r.int(), 5);
        assert_eq!(r.netstr(), "main.asm");
        assert_eq!(r.int(), 7);
        assert_eq!(r.byte(), 2);
        assert_eq!(r.short(), 1);
        assert_eq!(r.netstr(), "target");
        assert_eq!(r.take(2), [0, 2]);
        assert_eq!(r.netstr(), "");
        assert_eq!(r.take(8), [0; 8]);
        assert_eq!(r.byte(), 255);
        r.done();
    }

    #[test]
    fn rgb9_rev13_layout_preserves_patch_width_pc_and_source() {
        let mut r = Reader {
            bytes: write_rgbds(&fixture()).unwrap(),
            pos: 0,
        };
        assert_eq!(r.take(4), b"RGB9");
        assert_eq!(r.int(), 13);
        assert_eq!(r.int(), 1);
        assert_eq!(r.int(), 1);
        assert_eq!(r.int(), 2);
        for file in ["main.asm", "koh"] {
            assert_eq!(r.int(), -1);
            assert_eq!(r.int(), 0);
            assert_eq!(r.byte(), 1);
            assert_eq!(r.zstr(), file);
        }
        assert_eq!(r.zstr(), "target");
        assert_eq!(r.byte(), 1);
        assert_eq!(r.zstr(), "Code");
        assert_eq!(r.int(), 0);
        assert_eq!(r.int(), 0);
        assert_eq!(r.int(), 5);
        assert_eq!(r.byte(), 3);
        assert_eq!(r.int(), -1);
        assert_eq!(r.int(), -1);
        assert_eq!(r.byte(), 0);
        assert_eq!(r.int(), 0);
        assert_eq!(r.take(5), [0xc3, 0, 0, 0, 0]);
        assert_eq!(r.int(), 1);
        assert_eq!(r.int(), 1);
        assert_eq!(r.int(), 7);
        assert_eq!(r.int(), 1);
        assert_eq!(r.int(), 0);
        assert_eq!(r.int(), 0);
        assert_eq!(r.byte(), 2);
        assert_eq!(r.int(), 5);
        assert_eq!(r.take(5), [0x81, 0, 0, 0, 0]);
        assert_eq!(r.int(), 0);
        r.done();
    }

    #[test]
    fn strings_use_utf8_byte_lengths_and_seven_bit_prefixes() {
        let name = "é".repeat(70);
        let mut bytes = Vec::new();
        string(&mut bytes, &name, false).unwrap();
        assert_eq!(&bytes[..2], &[0x8c, 1]);
        let mut r = Reader { bytes, pos: 0 };
        assert_eq!(r.netstr(), name);
        r.done();
        assert!(string(&mut Vec::new(), "a\0b", true).is_err());
    }

    #[test]
    fn every_operator_and_function_has_its_rgbds_opcode() {
        let symbols = HashMap::from([("target", 4)]);
        for (operator, opcode) in [
            ("+", 0),
            ("-", 1),
            ("*", 2),
            ("/", 3),
            ("%", 4),
            ("**", 6),
            ("|", 0x10),
            ("&", 0x11),
            ("^", 0x12),
            ("&&", 0x21),
            ("||", 0x22),
            ("==", 0x30),
            ("!=", 0x31),
            (">", 0x32),
            ("<", 0x33),
            (">=", 0x34),
            ("<=", 0x35),
            ("<<", 0x40),
            (">>", 0x41),
            (">>>", 0x42),
        ] {
            let mut bytes = Vec::new();
            rpn(
                &Expr::Binary(
                    operator.into(),
                    Box::new(Expr::Number(2)),
                    Box::new(Expr::Pc),
                ),
                &symbols,
                &mut bytes,
            )
            .unwrap();
            assert_eq!(bytes, [0x80, 2, 0, 0, 0, 0x81, 255, 255, 255, 255, opcode]);
        }
        for (name, opcode) in [
            ("HRAM", 0x60),
            ("HIGH", 0x70),
            ("LOW", 0x71),
            ("BITWIDTH", 0x72),
            ("TZCOUNT", 0x73),
        ] {
            let mut bytes = Vec::new();
            rpn(
                &Expr::Call(name.into(), vec![Expr::Number(1)]),
                &symbols,
                &mut bytes,
            )
            .unwrap();
            assert_eq!(bytes, [0x80, 1, 0, 0, 0, opcode]);
        }
        let mut bytes = Vec::new();
        rpn(
            &Expr::Call("BANK".into(), vec![Expr::Symbol("target".into())]),
            &symbols,
            &mut bytes,
        )
        .unwrap();
        assert_eq!(bytes, [0x50, 4, 0, 0, 0]);
        bytes.clear();
        rpn(
            &Expr::Call("BANK".into(), vec![Expr::Pc]),
            &symbols,
            &mut bytes,
        )
        .unwrap();
        assert_eq!(bytes, [0x52]);
        for (operator, opcode) in [("-", 5), ("~", 0x13), ("!", 0x23)] {
            bytes.clear();
            rpn(
                &Expr::Unary(operator.into(), Box::new(Expr::Number(1))),
                &symbols,
                &mut bytes,
            )
            .unwrap();
            assert_eq!(bytes, [0x80, 1, 0, 0, 0, opcode]);
        }
    }

    #[test]
    fn unsupported_relocations_and_invalid_records_fail() {
        let mut object = fixture();
        object.sections[0].patches[0].symbol = None;
        assert!(write_kobj(&object).is_err());
        object.sections[0].patches[0].expression = Expr::Symbol("missing".into());
        assert!(write_rgbds(&object).is_err());
        object = fixture();
        object.sections[0].patches[0].offset = 2;
        assert!(write_rgbds(&object).is_err());
        object = fixture();
        object.symbols[0].section = Some(4);
        assert!(write_kobj(&object).is_err());
        object = fixture();
        object.sections[0].align = 4;
        assert!(write_kobj(&object).is_err());
        object.sections[0].address = Some(0x100);
        assert!(write_kobj(&object).is_ok());
        object.sections[0].align_offset = 16;
        assert!(write_rgbds(&object).is_err());
    }

    #[test]
    fn all_section_types_and_defined_symbols_are_serialized() {
        for (kind, rgb_kind) in [3, 2, 1, 6, 0, 5, 7, 4].into_iter().enumerate() {
            let object = Object {
                sections: vec![Section {
                    name: "s".into(),
                    kind: kind as u8,
                    data: vec![0; 3],
                    ..Section::default()
                }],
                symbols: vec![],
            };
            let mut r = Reader {
                bytes: write_rgbds(&object).unwrap(),
                pos: 0,
            };
            r.take(20);
            r.take(9);
            assert_eq!(r.zstr(), "koh");
            assert_eq!(r.zstr(), "s");
            r.take(12);
            assert_eq!(r.byte(), rgb_kind);
            r.take(13);
            if kind <= 1 {
                assert_eq!(r.take(3), [0; 3]);
                assert_eq!(r.int(), 0);
            }
            assert_eq!(r.int(), 0);
            r.done();
        }
        let mut object = fixture();
        object.symbols[0].visibility = 1;
        object.symbols[0].section = Some(0);
        object.symbols[0].value = -5;
        let bytes = write_rgbds(&object).unwrap();
        let start = bytes.windows(7).position(|w| w == b"target\0").unwrap() + 7;
        assert_eq!(
            &bytes[start..start + 17],
            &[2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 251, 255, 255, 255]
        );
    }
}

fn rpn(expr: &Expr, symbols: &HashMap<&str, usize>, out: &mut Vec<u8>) -> Result<(), String> {
    match expr {
        Expr::String(_) => return Err("String is not a numeric relocation".into()),
        Expr::Number(value) => {
            out.push(0x80);
            int(out, *value);
        }
        Expr::Pc => {
            out.push(0x81);
            int(out, -1);
        }
        Expr::Symbol(name) => {
            let id = symbols
                .get(name.as_str())
                .ok_or_else(|| format!("Undefined symbol '{name}'"))?;
            out.push(0x81);
            count(out, *id)?;
        }
        Expr::Unary(op, value) => {
            rpn(value, symbols, out)?;
            match op.as_str() {
                "+" => {}
                "-" => out.push(5),
                "~" => out.push(0x13),
                "!" => out.push(0x23),
                _ => return Err(format!("Unsupported relocation operator '{op}'")),
            }
        }
        Expr::Binary(op, lhs, rhs) => {
            rpn(lhs, symbols, out)?;
            rpn(rhs, symbols, out)?;
            out.push(match op.as_str() {
                "+" => 0,
                "-" => 1,
                "*" => 2,
                "/" => 3,
                "%" => 4,
                "**" => 6,
                "|" => 0x10,
                "&" => 0x11,
                "^" => 0x12,
                "&&" => 0x21,
                "||" => 0x22,
                "==" => 0x30,
                "!=" => 0x31,
                ">" => 0x32,
                "<" => 0x33,
                ">=" => 0x34,
                "<=" => 0x35,
                "<<" => 0x40,
                ">>" => 0x41,
                ">>>" => 0x42,
                _ => return Err(format!("Unsupported relocation operator '{op}'")),
            });
        }
        Expr::Call(name, args) => {
            if args.len() != 1 {
                return Err(format!("{name} requires one argument"));
            }
            if name.eq_ignore_ascii_case("BANK") {
                match &args[0] {
                    Expr::String(section) => {
                        out.push(0x51);
                        string(out, section, true)?;
                    }
                    Expr::Pc => out.push(0x52),
                    Expr::Symbol(symbol) => {
                        let id = symbols
                            .get(symbol.as_str())
                            .ok_or_else(|| format!("Undefined symbol '{symbol}'"))?;
                        out.push(0x50);
                        count(out, *id)?;
                    }
                    _ => return Err("BANK requires a symbol".into()),
                }
            } else if name.eq_ignore_ascii_case("SIZEOF") || name.eq_ignore_ascii_case("STARTOF") {
                let start = name.eq_ignore_ascii_case("STARTOF");
                match &args[0] {
                    Expr::String(section) => {
                        out.push(if start { 0x54 } else { 0x53 });
                        string(out, section, true)?;
                    }
                    Expr::Symbol(kind) => {
                        let kind = match kind.to_ascii_uppercase().as_str() {
                            "WRAM0" => 0,
                            "VRAM" => 1,
                            "ROMX" => 2,
                            "ROM0" => 3,
                            "HRAM" => 4,
                            "WRAMX" => 5,
                            "SRAM" => 6,
                            "OAM" => 7,
                            _ => return Err(format!("Unknown section type '{kind}'")),
                        };
                        out.extend([if start { 0x56 } else { 0x55 }, kind]);
                    }
                    _ => return Err(format!("{name} requires a section name or type")),
                }
            } else {
                rpn(&args[0], symbols, out)?;
                out.push(match name.to_ascii_uppercase().as_str() {
                    "HRAM" => 0x60,
                    "HIGH" => 0x70,
                    "LOW" => 0x71,
                    "BITWIDTH" => 0x72,
                    "TZCOUNT" => 0x73,
                    _ => return Err(format!("Unsupported relocation function '{name}'")),
                });
            }
        }
    }
    Ok(())
}

pub fn write_rgbds(object: &Object) -> Result<Vec<u8>, String> {
    validate(object)?;
    let symbols = object
        .symbols
        .iter()
        .enumerate()
        .map(|(id, s)| (s.name.as_str(), id))
        .collect();
    let mut files = vec!["koh"];
    for s in &object.sections {
        for p in &s.patches {
            if !p.file.is_empty() && !files.contains(&p.file.as_str()) {
                files.push(&p.file);
            }
        }
    }
    let node = |file: &str| files.iter().position(|f| *f == file).unwrap_or(0) as i32;
    let mut out = b"RGB9".to_vec();
    int(&mut out, 13);
    count(&mut out, object.symbols.len())?;
    count(&mut out, object.sections.len())?;
    count(&mut out, files.len())?;
    for file in files.iter().rev() {
        int(&mut out, -1);
        int(&mut out, 0);
        out.push(1);
        string(&mut out, file, true)?;
    }
    for s in &object.symbols {
        if s.kind > 1 {
            return Err("RGBDS cannot encode nonnumeric symbols".into());
        }
        string(&mut out, &s.name, true)?;
        out.push(match s.visibility {
            1 => 2,
            2 => 1,
            _ => 0,
        });
        if s.visibility != 2 {
            int(&mut out, 0);
            int(&mut out, 0);
            if let Some(id) = s.section {
                count(&mut out, id)?;
            } else {
                int(&mut out, -1);
            }
            int(&mut out, s.value);
        }
    }
    for (id, s) in object.sections.iter().enumerate() {
        string(&mut out, &s.name, true)?;
        int(&mut out, 0);
        int(&mut out, 0);
        count(&mut out, s.data.len())?;
        out.push([3, 2, 1, 6, 0, 5, 7, 4][s.kind as usize]);
        int(&mut out, s.address.unwrap_or(-1));
        int(&mut out, s.bank.unwrap_or(-1));
        out.push(s.align);
        int(&mut out, s.align_offset);
        if s.kind <= 1 {
            out.extend(&s.data);
            count(&mut out, s.patches.len())?;
            for p in &s.patches {
                int(&mut out, node(&p.file));
                out.extend(p.line.to_le_bytes());
                count(&mut out, p.offset)?;
                count(&mut out, id)?;
                int(&mut out, p.pc_offset);
                out.push(p.kind);
                let mut expression = Vec::new();
                rpn(&p.expression, &symbols, &mut expression)?;
                count(&mut out, expression.len())?;
                out.extend(expression);
            }
        } else if !s.patches.is_empty() {
            return Err("RGBDS RAM sections cannot contain patches".into());
        }
    }
    int(&mut out, 0);
    Ok(out)
}
