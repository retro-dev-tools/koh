#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Expr {
    Number(i32),
    String(String),
    Symbol(String),
    Pc,
    Unary(String, Box<Expr>),
    Binary(String, Box<Expr>, Box<Expr>),
    Call(String, Vec<Expr>),
}

struct Parser<'a> {
    source: &'a str,
    pos: usize,
}

pub fn parse(source: &str) -> Result<Expr, String> {
    let mut parser = Parser { source, pos: 0 };
    let result = parser.expression(0)?;
    parser.space();
    if parser.pos != source.len() {
        return Err(format!("Unexpected input at byte {}", parser.pos));
    }
    Ok(result)
}

fn precedence(op: &str) -> u8 {
    match op {
        "||" => 1,
        "&&" => 2,
        "==" | "!=" | "<" | ">" | "<=" | ">=" | "===" | "!==" => 3,
        "+" | "-" | "++" => 4,
        "&" | "|" | "^" => 5,
        "<<" | ">>" | ">>>" => 6,
        "*" | "/" | "%" => 7,
        "**" => 9,
        _ => 0,
    }
}

impl Parser<'_> {
    fn space(&mut self) {
        while self.source[self.pos..].starts_with(char::is_whitespace) {
            self.pos += self.source[self.pos..].chars().next().unwrap().len_utf8();
        }
    }
    fn take(&mut self, text: &str) -> bool {
        self.space();
        if self.source[self.pos..].starts_with(text) {
            self.pos += text.len();
            true
        } else {
            false
        }
    }
    fn expression(&mut self, minimum: u8) -> Result<Expr, String> {
        let mut left = self.atom()?;
        loop {
            self.space();
            let op = [
                ">>>", "===", "!==", "**", "++", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||",
                "+", "-", "*", "/", "%", "&", "|", "^", "<", ">",
            ]
            .into_iter()
            .find(|op| self.source[self.pos..].starts_with(op));
            let Some(op) = op else { break };
            let p = precedence(op);
            if p <= minimum {
                break;
            }
            self.pos += op.len();
            let right = self.expression(if op == "**" { p - 1 } else { p })?;
            left = Expr::Binary(op.into(), Box::new(left), Box::new(right));
        }
        Ok(left)
    }
    fn quoted(&mut self, quote: char, raw: bool) -> Result<String, String> {
        self.pos += quote.len_utf8();
        let mut result = String::new();
        while let Some(ch) = self.source[self.pos..].chars().next() {
            self.pos += ch.len_utf8();
            if ch == quote {
                return Ok(result);
            }
            if ch == '\\' && !raw {
                let escape = self.source[self.pos..]
                    .chars()
                    .next()
                    .ok_or("Unterminated escape")?;
                self.pos += escape.len_utf8();
                result.push(match escape {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    _ => return Err(format!("Unknown escape \\{escape}")),
                });
            } else {
                result.push(ch);
            }
        }
        Err("Unterminated string".into())
    }
    fn atom(&mut self) -> Result<Expr, String> {
        self.space();
        let ch = self.source[self.pos..]
            .chars()
            .next()
            .ok_or("Expected expression")?;
        if self.source[self.pos..].starts_with("#\"\"\"") {
            self.pos += 4;
            let end = self.source[self.pos..]
                .find("\"\"\"")
                .ok_or("Unterminated raw string")?;
            let value = self.source[self.pos..self.pos + end].to_string();
            self.pos += end + 3;
            return Ok(Expr::String(value));
        }
        if self.source[self.pos..].starts_with("#\"") {
            self.pos += 1;
            return Ok(Expr::String(self.quoted('"', true)?));
        }
        if matches!(ch, '+' | '-' | '!' | '~' | '#') {
            self.pos += 1;
            return Ok(Expr::Unary(ch.to_string(), Box::new(self.expression(8)?)));
        }
        if self.take("(") {
            let result = self.expression(0)?;
            if !self.take(")") {
                return Err("Expected ')'".into());
            }
            return Ok(result);
        }
        if ch == '@' {
            self.pos += 1;
            return Ok(Expr::Pc);
        }
        if ch == '$'
            && !self
                .source
                .as_bytes()
                .get(self.pos + 1)
                .is_some_and(u8::is_ascii_hexdigit)
        {
            self.pos += 1;
            return Ok(Expr::Pc);
        }
        if ch == '[' {
            let start = self.pos;
            let end = self.source[self.pos..].find(']').ok_or("Expected ']'")?;
            self.pos += end + 1;
            return Ok(Expr::Symbol(self.source[start..self.pos].to_string()));
        }
        if ch == '"' || ch == '\'' {
            let value = self.quoted(ch, false)?;
            if ch == '"' {
                return Ok(Expr::String(value));
            }
            let mut chars = value.chars();
            let c = chars.next().ok_or("Empty character constant")?;
            if chars.next().is_some() {
                return Err("Character constant requires one character".into());
            }
            return Ok(Expr::Number(c as i32));
        }
        if ch == '`' {
            self.pos += 1;
            let start = self.pos;
            let mut low = 0u8;
            let mut high = 0u8;
            let mut count = 0;
            while let Some(c) = self.source.as_bytes().get(self.pos).copied() {
                if c == b'_' {
                    self.pos += 1;
                    continue;
                }
                if !(b'0'..=b'3').contains(&c) {
                    break;
                }
                count += 1;
                if count > 8 {
                    return Err("Graphics literal exceeds eight pixels".into());
                }
                low = (low << 1) | ((c - b'0') & 1);
                high = (high << 1) | ((c - b'0') >> 1);
                self.pos += 1;
            }
            if self.pos == start || count == 0 {
                return Err("Empty graphics literal".into());
            }
            return Ok(Expr::Number(((high as i32) << 8) | low as i32));
        }
        if ch.is_ascii_digit() || matches!(ch, '$' | '%' | '&') {
            let start = self.pos;
            self.pos += 1;
            while self
                .source
                .as_bytes()
                .get(self.pos)
                .is_some_and(|c| c.is_ascii_alphanumeric() || *c == b'_' || *c == b'.')
            {
                self.pos += 1;
            }
            let text = self.source[start..self.pos].replace('_', "");
            let (digits, base) = if let Some(s) = text.strip_prefix('$') {
                (s, 16)
            } else if let Some(s) = text.strip_prefix('%') {
                (s, 2)
            } else if let Some(s) = text.strip_prefix('&') {
                (s, 8)
            } else if text.starts_with("0x") || text.starts_with("0X") {
                (&text[2..], 16)
            } else if text.starts_with("0b") || text.starts_with("0B") {
                (&text[2..], 2)
            } else if text.starts_with("0o") || text.starts_with("0O") {
                (&text[2..], 8)
            } else {
                (text.as_str(), 10)
            };
            if base == 10 && digits.contains('.') {
                let (value, q) = digits.split_once('q').unwrap_or((digits, "16"));
                let q: u32 = q.parse().map_err(|_| "Invalid precision")?;
                if q > 31 {
                    return Err("Precision exceeds 31".into());
                }
                let value: f64 = value.parse().map_err(|_| "Invalid fixed-point literal")?;
                return Ok(Expr::Number(
                    (value * (1u64 << q) as f64).round() as i64 as i32
                ));
            }
            let value = u32::from_str_radix(digits, base)
                .map_err(|_| format!("Invalid 32-bit number '{text}'"))?;
            return Ok(Expr::Number(value as i32));
        }
        let start = self.pos;
        while self.source[self.pos..]
            .chars()
            .next()
            .is_some_and(|c| c.is_alphanumeric() || matches!(c, '_' | '.' | '$' | ':' | '\\' | '?'))
        {
            self.pos += self.source[self.pos..].chars().next().unwrap().len_utf8();
        }
        if start == self.pos {
            return Err(format!("Expected expression at byte {}", self.pos));
        }
        let name = self.source[start..self.pos].to_string();
        if self.take("(") {
            let mut args = Vec::new();
            if !self.take(")") {
                loop {
                    args.push(self.expression(0)?);
                    if self.take(")") {
                        break;
                    }
                    if !self.take(",") {
                        return Err("Expected ',' or ')'".into());
                    }
                }
            }
            Ok(Expr::Call(name.to_ascii_uppercase(), args))
        } else {
            Ok(Expr::Symbol(name))
        }
    }
}

pub fn eval(expr: &Expr, symbols: &dyn Fn(&str) -> Option<i32>, pc: i32) -> Result<i32, String> {
    let number = |e: &Expr| eval(e, symbols, pc);
    match expr {
        Expr::Number(n) => Ok(*n),
        Expr::Pc => Ok(pc),
        Expr::String(_) => Err("Expected numeric expression".into()),
        Expr::Symbol(name) => symbols(name).ok_or_else(|| format!("Unresolved symbol '{name}'")),
        Expr::Unary(op, e) => {
            if op == "#" {
                if let Expr::Symbol(name) = e.as_ref() {
                    return symbols(&format!("#{name}"))
                        .ok_or_else(|| format!("Unresolved raw symbol '#{name}'"));
                }
            }
            let n = number(e)?;
            match op.as_str() {
                "+" => Ok(n),
                "-" => Ok(n.wrapping_neg()),
                "!" => Ok((n == 0) as i32),
                "~" => Ok(!n),
                _ => Err(format!("Unknown unary operator {op}")),
            }
        }
        Expr::Binary(op, a, b) => {
            if matches!(op.as_str(), "===" | "!==") {
                let equal = eval_string(a, symbols, pc)? == eval_string(b, symbols, pc)?;
                return Ok((equal == (op == "===")) as i32);
            }
            let left = number(a);
            let right = number(b);
            if matches!(op.as_str(), "&&" | "&") && (left == Ok(0) || right == Ok(0)) {
                return Ok(0);
            }
            if op == "||"
                && (left.as_ref().is_ok_and(|n| *n != 0) || right.as_ref().is_ok_and(|n| *n != 0))
            {
                return Ok(1);
            }
            let a = left?;
            let b = right?;
            Ok(match op.as_str() {
                "+" => a.wrapping_add(b),
                "-" => a.wrapping_sub(b),
                "*" => a.wrapping_mul(b),
                "/" | "%" => {
                    if b == 0 {
                        return Err("Division by zero".into());
                    }
                    let a = a as i64;
                    let b = b as i64;
                    let mut q = a / b;
                    let mut r = a % b;
                    if r != 0 && (r < 0) != (b < 0) {
                        q -= 1;
                        r += b;
                    }
                    if op == "/" { q as i32 } else { r as i32 }
                }
                "**" => {
                    if b < 0 {
                        return Err("Negative exponent".into());
                    }
                    a.wrapping_pow(b as u32)
                }
                "&" => a & b,
                "|" => a | b,
                "^" => a ^ b,
                "<<" | ">>" | ">>>" => shift(a, b, op),
                "==" => (a == b) as i32,
                "!=" => (a != b) as i32,
                "<" => (a < b) as i32,
                ">" => (a > b) as i32,
                "<=" => (a <= b) as i32,
                ">=" => (a >= b) as i32,
                "&&" => (a != 0 && b != 0) as i32,
                "||" => (a != 0 || b != 0) as i32,
                _ => return Err(format!("Unknown binary operator {op}")),
            })
        }
        Expr::Call(name, args) => eval_call(name, args, symbols, pc),
    }
}

fn shift(value: i32, amount: i32, op: &str) -> i32 {
    if amount < 0 {
        return shift(
            value,
            amount.saturating_neg(),
            if op == "<<" { ">>" } else { "<<" },
        );
    }
    if amount >= 32 {
        return if op == ">>" && value < 0 { -1 } else { 0 };
    }
    match op {
        "<<" => value.wrapping_shl(amount as u32),
        ">>" => value >> amount,
        _ => ((value as u32) >> amount) as i32,
    }
}

fn arity(name: &str, args: &[Expr], minimum: usize, maximum: usize) -> Result<(), String> {
    if (minimum..=maximum).contains(&args.len()) {
        Ok(())
    } else {
        Err(format!("{name} requires {minimum}..={maximum} arguments"))
    }
}

fn eval_call(
    name: &str,
    args: &[Expr],
    symbols: &dyn Fn(&str) -> Option<i32>,
    pc: i32,
) -> Result<i32, String> {
    let n = |i: usize| eval(&args[i], symbols, pc);
    let s = |i: usize| eval_string(&args[i], symbols, pc);
    match name {
        "DEF" => {
            arity(name, args, 1, 1)?;
            if let Expr::Symbol(key) = &args[0] {
                Ok(symbols(key).is_some() as i32)
            } else {
                Err("DEF requires a symbol".into())
            }
        }
        "ISCONST" => {
            arity(name, args, 1, 1)?;
            Ok(n(0).is_ok() as i32)
        }
        "BANK" | "STARTOF" | "SIZEOF" => {
            arity(name, args, 1, 1)?;
            if name == "SIZEOF"
                && matches!(&args[0],Expr::Call(op,args) if matches!(op.as_str(),"HIGH"|"LOW") && args.len()==1)
            {
                return Ok(1);
            }
            let key = match &args[0] {
                Expr::Symbol(s) | Expr::String(s) => s.as_str(),
                Expr::Pc => "@",
                _ => return Err(format!("{name} requires symbol or section")),
            };
            if name == "SIZEOF" {
                if key.starts_with('[') && key.ends_with(']') {
                    return Ok(1);
                }
                match key.to_ascii_uppercase().as_str() {
                    "A" | "B" | "C" | "D" | "E" | "H" | "L" | "F" => return Ok(1),
                    "AF" | "BC" | "DE" | "HL" | "SP" => return Ok(2),
                    _ => {}
                }
            }
            symbols(&format!("{name}:{key}")).ok_or_else(|| format!("Unresolved {name}({key})"))
        }
        "HIGH" | "LOW" | "BITWIDTH" | "TZCOUNT" => {
            arity(name, args, 1, 1)?;
            let v = n(0)?;
            Ok(match name {
                "HIGH" => (v >> 8) & 255,
                "LOW" => v & 255,
                "BITWIDTH" => (32 - (v as u32).leading_zeros()) as i32,
                _ => (v as u32).trailing_zeros() as i32,
            })
        }
        "STRLEN" | "CHARLEN" | "BYTELEN" => {
            arity(name, args, 1, 1)?;
            let value = s(0)?;
            Ok(if name == "BYTELEN" {
                value.len()
            } else {
                value.chars().count()
            } as i32)
        }
        "STRCMP" | "STRFIND" | "STRRFIND" => {
            arity(name, args, 2, 2)?;
            let a = s(0)?;
            let b = s(1)?;
            Ok(match name {
                "STRCMP" => match a.cmp(&b) {
                    std::cmp::Ordering::Less => -1,
                    std::cmp::Ordering::Equal => 0,
                    std::cmp::Ordering::Greater => 1,
                },
                _ => {
                    let index = if name == "STRFIND" {
                        a.find(&b)
                    } else {
                        a.rfind(&b)
                    };
                    index.map(|i| a[..i].chars().count() as i32).unwrap_or(-1)
                }
            })
        }
        "STRBYTE" => {
            arity(name, args, 2, 2)?;
            let a = s(0)?;
            let i = n(1)? as i64;
            let i = if i < 0 { a.len() as i64 + i } else { i };
            Ok(if i < 0 {
                0
            } else {
                a.as_bytes().get(i as usize).copied().unwrap_or(0) as i32
            })
        }
        "INCHARMAP" => {
            arity(name, args, 1, 1)?;
            Err("INCHARMAP requires an active character map".into())
        }
        "MUL" | "DIV" | "FMOD" | "POW" | "LOG" | "ROUND" | "CEIL" | "FLOOR" | "SIN" | "COS"
        | "TAN" | "ASIN" | "ACOS" | "ATAN" | "ATAN2" => {
            let binary = matches!(name, "MUL" | "DIV" | "FMOD" | "POW" | "LOG" | "ATAN2");
            let count = if binary { 2 } else { 1 };
            arity(name, args, count, count + 1)?;
            let q = if args.len() > count { n(count)? } else { 16 };
            if !(0..=31).contains(&q) {
                return Err("Precision must be 0..31".into());
            }
            let scale = (1u64 << q) as f64;
            let a = n(0)?;
            let b = if binary { n(1)? } else { 0 };
            if name == "MUL" {
                return Ok(((a as i64 * b as i64) >> q) as i32);
            }
            if name == "DIV" {
                return Ok(if b == 0 {
                    if a == 0 {
                        0
                    } else if a < 0 {
                        i32::MIN
                    } else {
                        i32::MAX
                    }
                } else {
                    (((a as i64) << q) / (b as i64)) as i32
                });
            }
            let x = a as f64 / scale;
            let y = b as f64 / scale;
            let tau = std::f64::consts::TAU;
            let result = match name {
                "FMOD" => x % y,
                "POW" => x.powf(y),
                "LOG" => x.log(y),
                "ROUND" => x.round(),
                "CEIL" => x.ceil(),
                "FLOOR" => x.floor(),
                "SIN" => (x * tau).sin(),
                "COS" => (x * tau).cos(),
                "TAN" => (x * tau).tan(),
                "ASIN" => x.asin() / tau,
                "ACOS" => x.acos() / tau,
                "ATAN" => x.atan() / tau,
                _ => x.atan2(y) / tau,
            };
            Ok((result * scale).round() as i32)
        }
        _ => Err(format!("Unknown numeric function '{name}'")),
    }
}

pub fn eval_string(
    expr: &Expr,
    symbols: &dyn Fn(&str) -> Option<i32>,
    pc: i32,
) -> Result<String, String> {
    match expr {
        Expr::String(value) => Ok(value.clone()),
        Expr::Unary(op, value) if op == "#" => eval_string(value, symbols, pc),
        Expr::Binary(op, a, b) if op == "++" => {
            Ok(eval_string(a, symbols, pc)? + &eval_string(b, symbols, pc)?)
        }
        Expr::Call(name, args) => {
            let s = |i: usize| eval_string(&args[i], symbols, pc);
            match name.as_str() {
                "STRCAT" => args.iter().map(|a| eval_string(a, symbols, pc)).collect(),
                "STRUPR" | "STRLWR" => {
                    arity(name, args, 1, 1)?;
                    let value = s(0)?;
                    Ok(if name == "STRUPR" {
                        value.to_ascii_uppercase()
                    } else {
                        value.to_ascii_lowercase()
                    })
                }
                "STRRPL" => {
                    arity(name, args, 3, 3)?;
                    Ok(s(0)?.replace(&s(1)?, &s(2)?))
                }
                "STRSLICE" => {
                    arity(name, args, 2, 3)?;
                    let value = s(0)?;
                    let length = value.chars().count() as i64;
                    let normalize = |index: i32| {
                        let index = index as i64;
                        if index < 0 {
                            (length + index).clamp(0, length)
                        } else {
                            index.min(length)
                        }
                    };
                    let start = normalize(eval(&args[1], symbols, pc)?);
                    let stop = if args.len() == 3 {
                        normalize(eval(&args[2], symbols, pc)?)
                    } else {
                        length
                    };
                    Ok(value
                        .chars()
                        .skip(start as usize)
                        .take(stop.saturating_sub(start).max(0) as usize)
                        .collect())
                }
                "STRSUB" => {
                    arity(name, args, 2, 3)?;
                    let value = s(0)?;
                    let start = eval(&args[1], symbols, pc)? as i64;
                    let length = value.chars().count() as i64;
                    let start = if start < 0 { length + start } else { start - 1 };
                    if start < 0 {
                        return Err("STRSUB index out of range".into());
                    }
                    let count = if args.len() == 3 {
                        eval(&args[2], symbols, pc)?
                    } else {
                        i32::MAX
                    };
                    if count < 0 {
                        return Err("Negative STRSUB length".into());
                    }
                    Ok(value
                        .chars()
                        .skip(start as usize)
                        .take(count as usize)
                        .collect())
                }
                "STRFMT" => {
                    arity(name, args, 1, usize::MAX)?;
                    format_string(&s(0)?, &args[1..], symbols, pc)
                }
                "READFILE" => Err("READFILE requires the assembler file resolver".into()),
                _ => Err(format!("Unknown string function '{name}'")),
            }
        }
        _ => Err("Expected string expression".into()),
    }
}

fn format_string(
    format: &str,
    args: &[Expr],
    symbols: &dyn Fn(&str) -> Option<i32>,
    pc: i32,
) -> Result<String, String> {
    let mut chars = format.chars().peekable();
    let mut result = String::new();
    let mut index = 0;
    while let Some(ch) = chars.next() {
        if ch != '%' {
            result.push(ch);
            continue;
        }
        if chars.peek() == Some(&'%') {
            chars.next();
            result.push('%');
            continue;
        }
        let mut left = false;
        let mut plus = false;
        let mut space = false;
        let mut zero = false;
        let mut alternate = false;
        loop {
            match chars.peek() {
                Some('-') => left = true,
                Some('+') => plus = true,
                Some(' ') => space = true,
                Some('0') => zero = true,
                Some('#') => alternate = true,
                _ => break,
            }
            chars.next();
        }
        let mut width = 0usize;
        while let Some(digit) = chars.peek().and_then(|c| c.to_digit(10)) {
            width = width
                .checked_mul(10)
                .and_then(|n| n.checked_add(digit as usize))
                .ok_or("Format width overflow")?;
            chars.next();
        }
        if width > 1_000_000 {
            return Err("Format width exceeds output limit".into());
        }
        let mut precision = None;
        if chars.peek() == Some(&'.') {
            chars.next();
            let mut p = 0usize;
            while let Some(digit) = chars.peek().and_then(|c| c.to_digit(10)) {
                p = p
                    .checked_mul(10)
                    .and_then(|n| n.checked_add(digit as usize))
                    .ok_or("Format precision overflow")?;
                chars.next();
            }
            if p > 255 {
                return Err("Format precision exceeds 255".into());
            }
            precision = Some(p);
        }
        let kind = chars.next().ok_or("Incomplete format specifier")?;
        let arg = args.get(index).ok_or("Not enough STRFMT arguments")?;
        index += 1;
        let mut text = if kind == 's' {
            eval_string(arg, symbols, pc)?
        } else {
            let n = eval(arg, symbols, pc)?;
            let mut text = match kind {
                'd' | 'i' => n.to_string(),
                'u' => (n as u32).to_string(),
                'x' => format!("{:x}", n as u32),
                'X' => format!("{:X}", n as u32),
                'o' => format!("{:o}", n as u32),
                'b' => format!("{:b}", n as u32),
                'f' => format!("{:.*}", precision.unwrap_or(6), n as f64 / 65536.0),
                _ => return Err(format!("Unknown STRFMT specifier '{kind}'")),
            };
            if alternate {
                let prefix = match kind {
                    'x' | 'X' => "$",
                    'o' => "&",
                    'b' => "%",
                    _ => "",
                };
                text.insert_str(0, prefix);
            }
            if matches!(kind, 'd' | 'i' | 'f') && n >= 0 {
                if plus {
                    text.insert(0, '+');
                } else if space {
                    text.insert(0, ' ');
                }
            }
            text
        };
        if kind == 's' {
            if let Some(p) = precision {
                text = text.chars().take(p).collect();
            }
        }
        let padding = width.saturating_sub(text.chars().count());
        if left {
            result.push_str(&text);
            result.extend(std::iter::repeat_n(' ', padding));
        } else if zero && kind != 's' {
            let prefix = if text.starts_with(['+', '-', ' ']) {
                text.remove(0).to_string()
            } else {
                String::new()
            };
            result.push_str(&prefix);
            result.extend(std::iter::repeat_n('0', padding));
            result.push_str(&text);
        } else {
            result.extend(std::iter::repeat_n(' ', padding));
            result.push_str(&text);
        }
    }
    if index != args.len() {
        return Err("Too many STRFMT arguments".into());
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn value(source: &str) -> i32 {
        eval(&parse(source).unwrap(), &|_| None, 0x1234).unwrap()
    }
    #[test]
    fn rgbds_precedence() {
        assert_eq!(value("1 + 2 << 3"), 17);
        assert_eq!(value("1 | 2 & 4"), 0);
        assert_eq!(value("-2 ** 2"), -4);
        assert_eq!(value("2 ** 3 ** 2"), 512);
        assert_eq!(value("1 == 2 < 3"), 1);
    }
    #[test]
    fn literals() {
        for source in ["$FFFFFFFF", "0xffffffff", "4_294_967_295"] {
            assert_eq!(value(source), -1);
        }
        assert_eq!(value("%1010 + &17 + 0o7 + 0b11"), 35);
        assert_eq!(value("`01012323"), 0x0f55);
        assert_eq!(value("'\\n'"), 10);
        assert_eq!(value("1.5q8"), 384);
        assert_eq!(value("@"), 0x1234);
    }
    #[test]
    fn signed_arithmetic() {
        assert_eq!(value("-5 / 2"), -3);
        assert_eq!(value("5 / -2"), -3);
        assert_eq!(value("-5 % 2"), 1);
        assert_eq!(value("5 % -2"), -1);
        assert_eq!(value("$80000000 / -1"), i32::MIN);
        assert_eq!(value("$7fffffff + 1"), i32::MIN);
        assert_eq!(value("-$80000000"), i32::MIN);
    }
    #[test]
    fn shifts() {
        assert_eq!(value("-1 >>> 1"), i32::MAX);
        assert_eq!(value("1 << 32"), 0);
        assert_eq!(value("-1 >> 100"), -1);
        assert_eq!(value("4 << -1"), 2);
        assert_eq!(value("1 >> -2"), 4);
    }
    #[test]
    fn functions() {
        assert_eq!(value("HIGH($1234)+LOW($1234)"), 0x46);
        assert_eq!(value("BITWIDTH(-1)"), 32);
        assert_eq!(value("TZCOUNT(0)"), 32);
        assert_eq!(value("DEF(missing)"), 0);
        assert_eq!(value("ISCONST(missing)"), 0);
        assert_eq!(value("SIZEOF(hl)"), 2);
        assert_eq!(value("SIZEOF([hl+])"), 1);
        assert_eq!(value("SIZEOF(HIGH(af))"), 1);
        assert_eq!(value("$+2"), 0x1236);
        assert_eq!(value("MUL(1.5,2.0)"), 3 << 16);
        assert_eq!(value("SIN(0.25)"), 1 << 16);
        assert_eq!(value("ROUND(-1.5)"), -2 << 16);
    }
    #[test]
    fn strings() {
        assert_eq!(
            eval_string(&parse("STRSLICE(\"abcd\",1,-1)").unwrap(), &|_| None, 0).unwrap(),
            "bc"
        );
        assert_eq!(
            eval_string(
                &parse("STRRPL(\"aaaa\",\"aa\",\"x\")").unwrap(),
                &|_| None,
                0
            )
            .unwrap(),
            "xx"
        );
        assert_eq!(value("STRLEN(\"hé\")"), 2);
        assert_eq!(value("BYTELEN(\"hé\")"), 3);
        assert_eq!(value("STRBYTE(\"abc\",-1)"), 99);
        assert_eq!(value("STRFIND(\"héllo\",\"l\")"), 2);
        assert_eq!(value("\"a\" === STRLWR(\"A\")"), 1);
        assert_eq!(
            eval_string(
                &parse("STRSUB(STRCAT(\"ab\",\"cd\"),2,2) ++ \"!\"").unwrap(),
                &|_| None,
                0
            )
            .unwrap(),
            "bc!"
        );
    }
    #[test]
    fn formatting() {
        assert_eq!(
            eval_string(&parse("#\"\"\"a\nb\"\"\"").unwrap(), &|_| None, 0).unwrap(),
            "a\nb"
        );
        assert_eq!(
            eval_string(
                &parse("STRFMT(\"%+05d %04X %-4s %%\",42,255,\"ok\")").unwrap(),
                &|_| None,
                0
            )
            .unwrap(),
            "+0042 00FF ok   %"
        );
        assert_eq!(
            eval_string(&parse("#\"a\\nb\"").unwrap(), &|_| None, 0).unwrap(),
            "a\\nb"
        );
        assert!(eval_string(&parse("STRFMT(\"%d\")").unwrap(), &|_| None, 0).is_err());
    }
    #[test]
    fn invalid_inputs() {
        for text in [
            "",
            "1+",
            "(2",
            "1 2",
            "0x",
            "'ab'",
            "\"unterminated",
            "`123012301",
            "1.0q32",
        ] {
            assert!(parse(text).is_err(), "{text}");
        }
        for text in [
            "1/0",
            "2 ** -1",
            "HIGH()",
            "LOW(1,2)",
            "DEF(1)",
            "UNKNOWN(1)",
        ] {
            assert!(eval(&parse(text).unwrap(), &|_| None, 0).is_err(), "{text}");
        }
    }
    #[test]
    fn metadata() {
        assert_eq!(
            eval(
                &parse("#ld").unwrap(),
                &|key| if key == "#ld" { Some(7) } else { None },
                0
            )
            .unwrap(),
            7
        );
        assert_eq!(
            eval_string(
                &Expr::Unary("#".into(), Box::new(Expr::String("raw".into()))),
                &|_| None,
                0
            )
            .unwrap(),
            "raw"
        );
        assert_eq!(
            eval(
                &parse("BANK(label)").unwrap(),
                &|key| if key == "BANK:label" { Some(3) } else { None },
                0
            )
            .unwrap(),
            3
        );
        assert_eq!(value("unknown & 0"), 0);
    }
}
