use crate::{
    draft::{Object, Patch, Section, Symbol},
    encode,
    expr::{self, Expr},
    model::{Line, Provenance, SourceLocation},
    source::{self, SourceLine},
};
use std::{
    collections::{HashMap, HashSet},
    path::Path,
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Diagnostic {
    pub file: String,
    pub line: u32,
    pub message: String,
}

impl std::fmt::Display for Diagnostic {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}:{}:1: error: {}", self.file, self.line, self.message)
    }
}

#[derive(Clone)]
struct Macro {
    body: Vec<SourceLine>,
}
#[derive(Default)]
struct Frame {
    args: Vec<String>,
    unique: usize,
    in_macro: bool,
}

struct Assembler {
    current_file: String,
    q: u8,
    pad: u8,
    options: Vec<(u8, u8)>,
    object: Object,
    constants: HashMap<String, i32>,
    deferred: HashMap<String, Expr>,
    mutable: HashSet<String>,
    strings: HashMap<String, String>,
    macros: HashMap<String, Macro>,
    section: Option<usize>,
    load: Option<(usize, usize, String)>,
    stack: Vec<(Option<usize>, Option<(usize, usize, String)>, String)>,
    unions: Vec<(usize, usize, usize, bool)>,
    union_sizes: HashMap<usize, usize>,
    anchor: String,
    exports: HashSet<String>,
    includes: HashSet<String>,
    maps: HashMap<String, Vec<(String, Vec<i32>)>>,
    map: String,
    map_stack: Vec<String>,
    rs: i32,
    unique: usize,
    anonymous: usize,
    steps: usize,
    errors: Vec<Diagnostic>,
    symbol_sources: HashMap<String, Provenance>,
    expansion: Vec<SourceLocation>,
    assertions: Vec<(Expr, SourceLine, Option<i32>, Option<usize>, bool)>,
}

pub fn assemble(
    text: &str,
    file: &str,
    allow_imports: bool,
) -> Result<crate::model::Object, Vec<Diagnostic>> {
    let (constants, mut strings) = crate::builtins::values().map_err(|message| {
        vec![Diagnostic {
            file: file.into(),
            line: 1,
            message,
        }]
    })?;
    for (name, value) in &mut strings {
        if name.starts_with("__ISO_") {
            *value = format!("\"{value}\"");
        }
    }
    let mut asm = Assembler {
        current_file: file.into(),
        q: 16,
        pad: 0,
        options: Vec::new(),
        object: Object::default(),
        constants,
        deferred: HashMap::new(),
        mutable: HashSet::new(),
        strings,
        macros: HashMap::new(),
        section: None,
        load: None,
        stack: Vec::new(),
        unions: Vec::new(),
        union_sizes: HashMap::new(),
        anchor: String::new(),
        exports: HashSet::new(),
        includes: HashSet::new(),
        maps: HashMap::from([("main".into(), Vec::new())]),
        map: "main".into(),
        map_stack: Vec::new(),
        rs: 0,
        unique: 0,
        anonymous: 0,
        steps: 0,
        errors: Vec::new(),
        symbol_sources: HashMap::new(),
        expansion: Vec::new(),
        assertions: Vec::new(),
    };
    let lines = source::lines(text, file);
    asm.prescan(&lines);
    asm.includes.insert(file.into());
    if asm.run(&lines, &mut Frame::default(), 0, false) {
        asm.error(
            &SourceLine {
                text: String::new(),
                file: file.into(),
                line: 1,
            },
            "BREAK outside loop",
        );
    }
    asm.end_implicit_unions();
    if !asm.unions.is_empty() {
        asm.errors.push(Diagnostic {
            file: file.into(),
            line: 1,
            message: "unclosed UNION".into(),
        });
    }
    asm.finish();
    if asm.errors.is_empty() {
        crate::lower::lower(asm.object, allow_imports).map_err(|message| {
            vec![Diagnostic {
                file: file.into(),
                line: 1,
                message,
            }]
        })
    } else {
        Err(asm.errors)
    }
}

impl Assembler {
    fn provenance(&self, line: &SourceLine) -> Provenance {
        Provenance {
            file: line.file.clone(),
            line: line.line,
            column: (line.text.len() - line.text.trim_start().len() + 1) as u32,
            expansion: self.expansion.clone(),
        }
    }
    fn error(&mut self, line: &SourceLine, message: impl Into<String>) {
        if self.errors.len() < 100 {
            self.errors.push(Diagnostic {
                file: line.file.clone(),
                line: line.line,
                message: message.into(),
            });
        }
    }

    fn pc_section(&self) -> Option<usize> {
        self.load.as_ref().map(|x| x.0).or(self.section)
    }
    fn offset(&self) -> usize {
        self.pc_section()
            .map_or(0, |s| self.object.sections[s].data.len())
    }
    fn pc(&self) -> i32 {
        self.pc_section().map_or(0, |s| {
            self.object.sections[s].address.unwrap_or(0) + self.offset() as i32
        })
    }
    fn qualify(&self, name: &str) -> String {
        let name = name.strip_prefix('#').unwrap_or(name);
        if name.starts_with('.') {
            format!("{}{name}", self.anchor)
        } else {
            name.into()
        }
    }
    fn lookup(&self, name: &str) -> Option<i32> {
        self.lookup_at(name, self.pc_section())
    }
    fn lookup_at(&self, name: &str, pc_section: Option<usize>) -> Option<i32> {
        if name == "_RS" {
            return Some(self.rs);
        }
        if let Some(name) = name.strip_prefix("BANK:") {
            let sec = if name == "@" {
                pc_section
            } else {
                self.object
                    .symbols
                    .iter()
                    .find(|s| s.name == name)
                    .and_then(|s| s.section)
            }?;
            return self.object.sections[sec]
                .bank
                .or(if self.object.sections[sec].kind == 0 {
                    Some(0)
                } else {
                    None
                });
        }
        for (prefix, size) in [("SIZEOF:", true), ("STARTOF:", false)] {
            if let Some(name) = name.strip_prefix(prefix) {
                let s = self.object.sections.iter().find(|s| s.name == name)?;
                return if size {
                    Some(s.data.len() as i32)
                } else {
                    s.address
                };
            }
        }
        self.constants.get(name).copied().or_else(|| {
            self.object
                .symbols
                .iter()
                .find(|s| s.name == name)
                .and_then(|s| {
                    if let Some(section) = s.section {
                        self.object.sections[section]
                            .address
                            .map(|a| a.wrapping_add(s.value))
                    } else {
                        Some(s.value)
                    }
                })
        })
    }
    fn tree(&self, text: &str) -> Result<Expr, String> {
        let tree = expr::parse_with_precision(text, self.q)?;
        crate::context::fold(
            self.freeze(tree),
            self.maps.get(&self.map).map(Vec::as_slice).unwrap_or(&[]),
            &self.current_file,
            &|n| self.lookup(n),
            self.pc(),
        )
    }
    fn freeze(&self, tree: Expr) -> Expr {
        self.freeze_inner(tree, 0)
    }
    fn freeze_inner(&self, tree: Expr, depth: usize) -> Expr {
        if depth > 64 {
            return tree;
        }
        match tree {
            Expr::Symbol(s) => {
                let name = self.qualify(&s);
                if let Some(v) = self.constants.get(&name) {
                    Expr::Number(*v)
                } else if let Some(v) = self.deferred.get(&name) {
                    self.freeze_inner(v.clone(), depth + 1)
                } else if name == "_RS" {
                    Expr::Number(self.rs)
                } else {
                    Expr::Symbol(name)
                }
            }
            Expr::Unary(op, a) if op == "#" => {
                if let Expr::Symbol(name) = *a {
                    if let Some(value) = self.strings.get(&name) {
                        Expr::String(value.clone())
                    } else {
                        self.freeze_inner(Expr::Symbol(name), depth + 1)
                    }
                } else {
                    Expr::Unary(op, a)
                }
            }
            Expr::Unary(op, a) => Expr::Unary(op, Box::new(self.freeze_inner(*a, depth + 1))),
            Expr::Binary(op, a, b) => Expr::Binary(
                op,
                Box::new(self.freeze_inner(*a, depth + 1)),
                Box::new(self.freeze_inner(*b, depth + 1)),
            ),
            Expr::Call(op, args) if op == "DEF" && args.len() == 1 => {
                if let Expr::Symbol(name) = &args[0] {
                    let name = self.qualify(name);
                    Expr::Number(i32::from(
                        self.constants.contains_key(&name)
                            || self.deferred.contains_key(&name)
                            || self.strings.contains_key(&name)
                            || self.macros.contains_key(&name.to_ascii_uppercase())
                            || self.object.symbols.iter().any(|s| s.name == name),
                    ))
                } else {
                    Expr::Call(op, args)
                }
            }
            Expr::Call(op, args) if matches!(op.as_str(), "BANK" | "SIZEOF" | "STARTOF") => {
                Expr::Call(
                    op,
                    args.into_iter()
                        .map(|a| {
                            if let Expr::Symbol(s) = a {
                                Expr::Symbol(self.qualify(&s))
                            } else {
                                a
                            }
                        })
                        .collect(),
                )
            }
            Expr::Call(op, args) => Expr::Call(
                op,
                args.into_iter()
                    .map(|a| self.freeze_inner(a, depth + 1))
                    .collect(),
            ),
            other => other,
        }
    }
    fn value(&self, text: &str) -> Result<i32, String> {
        let tree = self.tree(text)?;
        let tree = self.numeric_tree(tree)?;
        if has_pc(&tree)
            && self
                .pc_section()
                .is_none_or(|s| self.object.sections[s].address.is_none())
        {
            return Err("current address is not known until section placement".into());
        }
        expr::eval(&tree, &|name| self.lookup(name), self.pc())
            .or_else(|e| self.invariant(&tree).ok_or(e))
    }
    fn string(&self, text: &str) -> Result<String, String> {
        let text = text.trim();
        if let Some(value) = self.strings.get(text) {
            return Ok(value.clone());
        }
        expr::eval_string(&self.tree(text)?, &|n| self.lookup(n), self.pc())
    }
    fn numeric_tree(&self, tree: Expr) -> Result<Expr, String> {
        Ok(match tree {
            Expr::String(s) => {
                let values = self.encode_string(&s)?;
                if values.len() > 4 {
                    return Err("numeric string exceeds four bytes".into());
                }
                Expr::Number(values.into_iter().fold(0i32, |a, b| a.wrapping_shl(8) | b))
            }
            Expr::Unary(op, a) => Expr::Unary(op, Box::new(self.numeric_tree(*a)?)),
            Expr::Binary(op, a, b) if !matches!(op.as_str(), "===" | "!==" | "++") => Expr::Binary(
                op,
                Box::new(self.numeric_tree(*a)?),
                Box::new(self.numeric_tree(*b)?),
            ),
            tree => tree,
        })
    }
    fn prescan(&mut self, lines: &[SourceLine]) {
        for _ in 0..lines.len().min(64) {
            let before = self.constants.len();
            let mut depth = 0;
            for line in lines {
                let (_, raw) = source::label(&line.text);
                let (head, _) = source::word(raw);
                if matches!(
                    head.to_ascii_uppercase().as_str(),
                    "MACRO" | "REPT" | "FOR" | "IF"
                ) {
                    depth += 1;
                }
                if matches!(head.to_ascii_uppercase().as_str(), "ENDM" | "ENDR" | "ENDC") {
                    depth -= 1;
                }
                if depth != 0 {
                    continue;
                }
                let (first, rest) = source::word(raw);
                let (name, rest) = if first.eq_ignore_ascii_case("DEF") {
                    source::word(rest)
                } else {
                    (first, rest)
                };
                let (kind, value) = source::word(rest);
                if kind.eq_ignore_ascii_case("EQU")
                    && !self.constants.contains_key(name)
                    && !name.contains(['{', '#'])
                    && !value.contains('\'')
                {
                    if let Ok(v) = self.value(value) {
                        self.constants.insert(name.into(), v);
                    }
                }
            }
            if before == self.constants.len() {
                break;
            }
        }
    }
    fn expand_text(&self, text: &str, frame: &Frame) -> Result<String, String> {
        let mut result = String::new();
        let mut chars = text.chars().peekable();
        while let Some(c) = chars.next() {
            if c == '#' && chars.peek() == Some(&'"') {
                result.push('#');
                result.push(chars.next().unwrap());
                let triple = chars.clone().take(2).collect::<String>() == "\"\"";
                if triple {
                    result.push(chars.next().unwrap());
                    result.push(chars.next().unwrap());
                }
                let mut quotes = 0;
                for c in chars.by_ref() {
                    result.push(c);
                    quotes = if c == '"' { quotes + 1 } else { 0 };
                    if quotes == if triple { 3 } else { 1 } {
                        break;
                    }
                }
            } else if c == '\\' {
                match chars.next() {
                    Some('@') => result.push_str(&format!("_{}", frame.unique)),
                    Some('#') if frame.in_macro => result.push_str(&frame.args.join(",")),
                    Some(n @ '1'..='9') if frame.in_macro => result.push_str(
                        frame
                            .args
                            .get(n as usize - '1' as usize)
                            .ok_or("missing macro argument")?,
                    ),
                    Some('<') if frame.in_macro => {
                        let mut n = String::new();
                        for c in chars.by_ref() {
                            if c == '>' {
                                break;
                            }
                            n.push(c);
                        }
                        let i = self.value(&n)?;
                        result.push_str(
                            frame
                                .args
                                .get(i.wrapping_sub(1) as usize)
                                .ok_or("missing macro argument")?,
                        );
                    }
                    Some(c) => {
                        result.push('\\');
                        result.push(c);
                    }
                    None => result.push('\\'),
                }
            } else if c == '{' {
                let mut name = String::new();
                let mut closed = false;
                for c in chars.by_ref() {
                    if c == '}' {
                        closed = true;
                        break;
                    }
                    name.push(c);
                }
                if !closed {
                    return Err("unclosed interpolation".into());
                }
                let (format, key) = name.split_once(':').unwrap_or(("", &name));
                let key = key.trim();
                let value = if let Some(s) = self.strings.get(key) {
                    Expr::String(s.clone())
                } else if key.to_ascii_uppercase().starts_with("SECTION(") && key.ends_with(')') {
                    let name = key[8..key.len() - 1].trim();
                    let section = if name == "@" {
                        self.pc_section()
                    } else {
                        let name = self.qualify(name);
                        self.object
                            .symbols
                            .iter()
                            .find(|s| s.name == name)
                            .and_then(|s| s.section)
                    }
                    .ok_or("SECTION requires a defined label or current section")?;
                    Expr::String(self.object.sections[section].name.clone())
                } else {
                    Expr::Number(self.value(key)?)
                };
                let format = if format.is_empty() {
                    match &value {
                        Expr::String(_) => "s",
                        _ if key == "@" => "#X",
                        _ => "d",
                    }
                } else {
                    format
                };
                result.push_str(&expr::eval_string(
                    &Expr::Call(
                        "STRFMT".into(),
                        vec![Expr::String(format!("%{format}")), value],
                    ),
                    &|n| self.lookup(n),
                    self.pc(),
                )?);
            } else {
                result.push(c);
            }
        }
        if result.contains('\n') && source::lines(&result, "").len() > 1 {
            return Ok(result);
        }
        let (_, body) = source::label(&result);
        let (head, tail) = source::word(body);
        if head.eq_ignore_ascii_case("PURGE") {
            return Ok(result);
        }
        let protected = if head.eq_ignore_ascii_case("DEF") || head.eq_ignore_ascii_case("REDEF") {
            let (name, _) = source::word(tail);
            result.len() - tail.len() + name.len()
        } else {
            let (kind, _) = source::word(tail);
            if matches!(
                kind.to_ascii_uppercase().as_str(),
                "EQU" | "EQUS" | "SET" | "="
            ) {
                result.len() - tail.len()
            } else {
                0
            }
        };
        Ok(result[..protected].to_owned()
            + &source::replace_words(&result[protected..], |name| self.strings.get(name).cloned()))
    }
    fn run(
        &mut self,
        lines: &[SourceLine],
        frame: &mut Frame,
        depth: usize,
        in_loop: bool,
    ) -> bool {
        if depth > 64 {
            if let Some(line) = lines.first() {
                self.error(line, "expansion depth exceeded");
            }
            return false;
        }
        let mut cond: Vec<(bool, bool, bool)> = Vec::new();
        let mut lines = lines.to_vec();
        let mut replay_depths = vec![0; lines.len()];
        let mut i = 0;
        while i < lines.len() && self.errors.len() < 100 {
            self.steps += 1;
            if self.steps > 1_000_000 {
                self.error(&lines[i], "expansion budget exceeded");
                return false;
            }
            let line = &lines[i];
            self.current_file = line.file.clone();
            let location = SourceLocation {
                file: line.file.clone(),
                line: line.line,
                column: (line.text.len() - line.text.trim_start().len() + 1) as u32,
            };
            i += 1;
            let enabled = cond.iter().all(|c| c.1);
            let text = if enabled {
                match self.expand_text(&line.text, frame) {
                    Ok(t) => t,
                    Err(e) => {
                        self.error(line, e);
                        continue;
                    }
                }
            } else {
                line.text.clone()
            };
            if enabled {
                let mut replay = source::lines(&text, &line.file);
                if replay.len() > 1 || text != line.text {
                    let replay_depth = replay_depths[i - 1] + 1;
                    if depth + replay_depth > 64 {
                        self.error(line, "string expansion depth exceeded");
                        continue;
                    }
                    for part in &mut replay {
                        part.line = line.line;
                    }
                    replay_depths.splice(i - 1..i, std::iter::repeat_n(replay_depth, replay.len()));
                    lines.splice(i - 1..i, replay);
                    i -= 1;
                    continue;
                }
            }
            let (label, body) = source::label(&text);
            let (head, tail) = source::word(body);
            let head_upper = head.to_ascii_uppercase();
            match head_upper.as_str() {
                "IF" => {
                    let yes = enabled
                        && match self.value(tail) {
                            Ok(v) => v != 0,
                            Err(e) => {
                                self.error(line, e);
                                false
                            }
                        };
                    cond.push((enabled, yes, yes));
                    continue;
                }
                "ELIF" | "ELSE" => {
                    if let Some(c) = cond.last_mut() {
                        let yes = c.0
                            && !c.2
                            && (head_upper == "ELSE"
                                || match self.value(tail) {
                                    Ok(v) => v != 0,
                                    Err(e) => {
                                        self.error(line, e);
                                        false
                                    }
                                });
                        c.1 = yes;
                        c.2 |= yes;
                    } else {
                        self.error(line, "conditional without IF");
                    }
                    continue;
                }
                "ENDC" => {
                    if cond.pop().is_none() {
                        self.error(line, "ENDC without IF");
                    }
                    continue;
                }
                "MACRO" | "REPT" | "FOR" => {
                    let close = if head_upper == "MACRO" {
                        "ENDM"
                    } else {
                        "ENDR"
                    };
                    let start = i;
                    let mut nesting = 1;
                    while i < lines.len() {
                        let (_, body) = source::label(&lines[i].text);
                        let (h, _) = source::word(body);
                        let h = h.to_ascii_uppercase();
                        if (close == "ENDM" && h == "MACRO")
                            || (close == "ENDR" && matches!(h.as_str(), "REPT" | "FOR"))
                        {
                            nesting += 1;
                        }
                        if h == close {
                            nesting -= 1;
                        }
                        i += 1;
                        if nesting == 0 {
                            break;
                        }
                    }
                    if nesting != 0 {
                        self.error(line, format!("missing {close}"));
                        continue;
                    }
                    if !enabled {
                        continue;
                    }
                    let block = &lines[start..i - 1];
                    if head_upper == "MACRO" {
                        let name = label.map(|l| l.0).unwrap_or(tail).trim();
                        if name.is_empty() || name.contains(char::is_whitespace) {
                            self.error(line, "MACRO requires a name");
                            continue;
                        }
                        if self
                            .macros
                            .insert(
                                name.to_ascii_uppercase(),
                                Macro {
                                    body: block.to_vec(),
                                },
                            )
                            .is_some()
                        {
                            self.error(line, "macro already defined");
                        }
                    } else {
                        let values = match source::args(tail) {
                            Ok(v) => v,
                            Err(e) => {
                                self.error(line, e);
                                continue;
                            }
                        };
                        let (var, mut current, end, step) = if head_upper == "REPT" {
                            let count = match self.value(tail) {
                                Ok(v) if v >= 0 => v,
                                Ok(_) => {
                                    self.error(line, "negative REPT count");
                                    continue;
                                }
                                Err(e) => {
                                    self.error(line, e);
                                    continue;
                                }
                            };
                            (None, 0, count, 1)
                        } else {
                            if !(2..=4).contains(&values.len()) {
                                self.error(line, "FOR requires variable, [start,] end[, step]");
                                continue;
                            }
                            let nums: Result<Vec<_>, _> =
                                values[1..].iter().map(|v| self.value(v)).collect();
                            let nums = match nums {
                                Ok(v) => v,
                                Err(e) => {
                                    self.error(line, e);
                                    continue;
                                }
                            };
                            let (start, end, step) = match nums.as_slice() {
                                [end] => (0, *end, 1),
                                [start, end] => (*start, *end, 1),
                                [start, end, step] => (*start, *end, *step),
                                _ => unreachable!(),
                            };
                            if step == 0 {
                                self.error(line, "FOR step must not be zero");
                                continue;
                            }
                            (Some(values[0].clone()), start, end, step)
                        };
                        while if step > 0 {
                            current < end
                        } else {
                            current > end
                        } {
                            if let Some(var) = &var {
                                self.constants.insert(var.clone(), current);
                            }
                            self.unique += 1;
                            let old_unique = frame.unique;
                            frame.unique = self.unique;
                            self.expansion.push(location.clone());
                            let stop = self.run(block, frame, depth + 1, true);
                            self.expansion.pop();
                            frame.unique = old_unique;
                            if stop || self.steps > 1_000_000 {
                                break;
                            }
                            match current.checked_add(step) {
                                Some(v) => current = v,
                                None => break,
                            }
                        }
                        if let Some(var) = var {
                            self.symbol_sources
                                .insert(var.clone(), self.provenance(line));
                            self.constants.insert(var, current);
                        }
                    }
                    continue;
                }
                _ => (),
            }
            if !enabled {
                continue;
            }
            if head_upper == "BREAK" {
                if in_loop {
                    return true;
                }
                self.error(line, "BREAK outside loop");
                continue;
            }
            if head_upper == "SHIFT" {
                if !frame.in_macro {
                    self.error(line, "SHIFT outside macro");
                    continue;
                }
                let n = if tail.is_empty() {
                    Ok(1)
                } else {
                    self.value(tail)
                };
                match n {
                    Ok(n) if n >= 0 && n as usize <= frame.args.len() => {
                        frame.args.drain(..n as usize);
                        self.constants
                            .insert("_NARG".into(), frame.args.len() as i32);
                    }
                    _ => self.error(line, "SHIFT exceeds arguments"),
                };
                continue;
            }
            if let Some(mac) = self.macros.get(&head_upper).cloned() {
                if let Some((name, export)) = label {
                    if let Err(e) = self.define_label(name, export, line) {
                        self.error(line, e);
                    }
                }
                match source::macro_args(tail) {
                    Ok(args) => {
                        self.unique += 1;
                        let old = self.constants.insert("_NARG".into(), args.len() as i32);
                        let old_anchor = self.anchor.clone();
                        let synthetic_anchor = format!("__koh_macro_{}", self.unique);
                        let scoped =
                            self.anchor.is_empty() || self.anchor.starts_with("__koh_macro_");
                        if scoped {
                            self.anchor = synthetic_anchor.clone();
                        }
                        self.expansion.push(location.clone());
                        let stop = self.run(
                            &mac.body,
                            &mut Frame {
                                args,
                                unique: self.unique,
                                in_macro: true,
                            },
                            depth + 1,
                            in_loop,
                        );
                        self.expansion.pop();
                        if scoped && self.anchor == synthetic_anchor {
                            self.anchor = old_anchor;
                        }
                        if let Some(old) = old {
                            self.constants.insert("_NARG".into(), old);
                        } else {
                            self.constants.remove("_NARG");
                        }
                        if stop {
                            return true;
                        }
                    }
                    Err(e) => self.error(line, e),
                }
                continue;
            }
            if head_upper == "INCLUDE" {
                let result: Result<bool, String> = (|| {
                    let name = self.string(tail)?;
                    let path = Path::new(&line.file)
                        .parent()
                        .unwrap_or(Path::new("."))
                        .join(name);
                    let path = path
                        .canonicalize()
                        .map_err(|e| format!("cannot include {}: {e}", path.display()))?;
                    let file = path.to_string_lossy().into_owned();
                    if !self.includes.insert(file.clone()) {
                        return Err("circular INCLUDE".into());
                    }
                    let text = std::fs::read_to_string(&path)
                        .map_err(|e| format!("cannot include {file}: {e}"))?;
                    let body = source::lines(&text, &file);
                    self.prescan(&body);
                    self.expansion.push(location.clone());
                    let stop = self.run(&body, frame, depth + 1, in_loop);
                    self.expansion.pop();
                    self.includes.remove(&file);
                    Ok(stop)
                })();
                match result {
                    Ok(true) => return true,
                    Ok(false) => (),
                    Err(e) => self.error(line, e),
                }
                continue;
            }
            if let Err(e) = self.statement(label, head, tail, line) {
                self.error(line, e);
            }
        }
        if !cond.is_empty() {
            if let Some(line) = lines.last() {
                self.error(line, "unclosed IF: missing ENDC");
            }
        }
        false
    }

    fn define_label(&mut self, name: &str, export: bool, line: &SourceLine) -> Result<(), String> {
        let name = if name.is_empty() {
            self.anonymous += 1;
            format!("__koh_anon_{}", self.anonymous)
        } else {
            self.qualify(name)
        };
        if name.starts_with('.') {
            return Err("local label requires global scope".into());
        }
        if self.constants.contains_key(&name) || self.object.symbols.iter().any(|s| s.name == name)
        {
            return Err(format!("symbol already defined: {name}"));
        }
        let section = self.pc_section().ok_or("label outside SECTION")?;
        if !name.contains('.') && !name.starts_with("__koh_anon_") {
            self.anchor = name.clone();
        }
        self.object.symbols.push(Symbol {
            name: name.clone(),
            kind: 0,
            visibility: u8::from(export || self.exports.contains(&name)),
            section: Some(section),
            value: self.offset() as i32,
            source: Some(self.provenance(line)),
        });
        Ok(())
    }

    fn statement(
        &mut self,
        label: Option<(&str, bool)>,
        head: &str,
        tail: &str,
        line: &SourceLine,
    ) -> Result<(), String> {
        if let Some((name, export)) = label {
            self.define_label(name, export, line)?;
        }
        if head.is_empty() {
            return Ok(());
        }
        if tail.is_empty() && head.contains('.') && !head.starts_with('#') {
            return self.define_label(head, false, line);
        }
        let command = head.to_ascii_uppercase();
        let (name, rest) = if matches!(command.as_str(), "DEF" | "REDEF") {
            source::word(tail)
        } else {
            (head, tail)
        };
        let (op, value) = source::word(rest);
        if matches!(
            op.to_ascii_uppercase().as_str(),
            "EQU" | "EQUS" | "SET" | "=" | "RB" | "RW" | "RL"
        ) {
            let name = self.qualify(name);
            self.symbol_sources
                .insert(name.clone(), self.provenance(line));
            if op.eq_ignore_ascii_case("EQUS") {
                if command != "REDEF" && self.strings.contains_key(&name) {
                    return Err(format!("symbol already defined: {name}"));
                }
                let value = self.string(value)?;
                self.strings.insert(name, value);
                return Ok(());
            }
            let v = if matches!(op.to_ascii_uppercase().as_str(), "RB" | "RW" | "RL") {
                let old = self.rs;
                let count = self.value(value)?;
                if count < 0 {
                    return Err("negative reservation".into());
                }
                self.rs = self
                    .rs
                    .checked_add(
                        count
                            .checked_mul(if op.eq_ignore_ascii_case("RW") {
                                2
                            } else if op.eq_ignore_ascii_case("RL") {
                                4
                            } else {
                                1
                            })
                            .ok_or("reservation overflow")?,
                    )
                    .ok_or("reservation overflow")?;
                old
            } else {
                match self.value(value) {
                    Ok(v) => v,
                    Err(_) if op.eq_ignore_ascii_case("EQU") => {
                        let tree = self.tree(value)?;
                        self.deferred.insert(name, tree);
                        return Ok(());
                    }
                    Err(e) => return Err(e),
                }
            };
            if command != "REDEF" && op.eq_ignore_ascii_case("EQU") {
                if self.constants.get(&name).is_some_and(|old| *old != v) {
                    return Err(format!("constant already defined: {name}"));
                }
            } else {
                self.mutable.insert(name.clone());
            }
            self.constants.insert(name, v);
            return Ok(());
        }
        match command.as_str() {
            "SECTION" | "LOAD" => self.open_section(tail, command == "LOAD", line),
            "ENDSECTION" => {
                self.section = None;
                self.load = None;
                Ok(())
            }
            "PUSHS" => {
                self.stack
                    .push((self.section, self.load.take(), self.anchor.clone()));
                self.section = None;
                if !tail.is_empty() {
                    self.open_section(tail, false, line)?;
                }
                Ok(())
            }
            "POPS" => {
                let (s, l, a) = self.stack.pop().ok_or("POPS without PUSHS")?;
                self.section = s;
                self.load = l;
                self.anchor = a;
                Ok(())
            }
            "ENDL" => {
                let (_, _, anchor) = self.load.take().ok_or("ENDL without LOAD")?;
                self.anchor = anchor;
                Ok(())
            }
            "UNION" => {
                let s = self.pc_section().ok_or("UNION outside SECTION")?;
                if self.object.sections[s].kind < 2 {
                    return Err("UNION requires RAM section".into());
                }
                self.unions.push((s, self.offset(), self.offset(), false));
                Ok(())
            }
            "NEXTU" | "ENDU" => {
                let (s, start, max, implicit) =
                    self.unions.pop().ok_or("union directive without UNION")?;
                if Some(s) != self.pc_section() {
                    return Err("UNION section changed".into());
                }
                let max = max.max(self.offset());
                if command == "NEXTU" {
                    self.object.sections[s].data.truncate(start);
                    self.unions.push((s, start, max, implicit));
                } else {
                    self.object.sections[s].data.resize(max, 0);
                }
                Ok(())
            }
            "DB" | "DW" | "DL" => {
                let width = if command == "DB" {
                    1
                } else if command == "DW" {
                    2
                } else {
                    4
                };
                let values = source::args(tail)?;
                if values.is_empty() {
                    self.emit(&vec![0; width], line)?;
                }
                for value in values {
                    if let Ok(text) = self.string(&value) {
                        for n in self.encode_string(&text)? {
                            self.emit_number(Expr::Number(n), width, false, line, None)?;
                        }
                    } else {
                        let tree = self.numeric_tree(self.tree(&value)?)?;
                        self.emit_number(tree, width, false, line, None)?;
                    }
                }
                Ok(())
            }
            "DS" => {
                let values = source::args(tail)?;
                let count_text = values.first().ok_or("DS requires count")?;
                let count = if count_text.to_ascii_uppercase().starts_with("ALIGN[") {
                    let inner = count_text[6..]
                        .strip_suffix(']')
                        .ok_or("DS ALIGN requires ]")?;
                    self.alignment_padding(&source::args(inner)?)?
                } else {
                    self.value(count_text)?
                };
                if !(0..=65536).contains(&count) {
                    return Err("DS count outside 0..65536".into());
                }
                if values.len() > 1 {
                    for i in 0..count as usize {
                        let tree =
                            self.numeric_tree(self.tree(&values[1 + i % (values.len() - 1)])?)?;
                        self.emit_number(tree, 1, false, line, None)?;
                    }
                    Ok(())
                } else {
                    let fill = if self
                        .section
                        .is_some_and(|s| self.object.sections[s].kind < 2)
                    {
                        self.pad
                    } else {
                        0
                    };
                    self.emit(&vec![fill; count as usize], line)
                }
            }
            "INCBIN" => {
                let values = source::args(tail)?;
                let name = self.string(values.first().ok_or("INCBIN requires file")?)?;
                let path = Path::new(&line.file)
                    .parent()
                    .unwrap_or(Path::new("."))
                    .join(name);
                let data = std::fs::read(&path)
                    .map_err(|e| format!("cannot read {}: {e}", path.display()))?;
                let start = if values.len() > 1 {
                    self.value(&values[1])?
                } else {
                    0
                };
                let count = if values.len() > 2 {
                    self.value(&values[2])?
                } else {
                    data.len() as i32 - start
                };
                if start < 0 || count < 0 {
                    return Err("invalid INCBIN range".into());
                }
                let data = data
                    .get(
                        start as usize
                            ..(start as usize)
                                .checked_add(count as usize)
                                .ok_or("INCBIN range overflow")?,
                    )
                    .ok_or("INCBIN range outside file")?;
                self.emit(data, line)
            }
            "ALIGN" => {
                let values = source::args(tail)?;
                let pad = self.alignment_padding(&values)?;
                self.emit(&vec![0; pad as usize], line)
            }
            "EXPORT" | "GLOBAL" => {
                for name in source::args(tail)? {
                    self.exports.insert(self.qualify(&name));
                }
                Ok(())
            }
            "PURGE" => {
                for name in source::args(tail)? {
                    self.constants.remove(&name);
                    self.strings.remove(&name);
                    self.macros.remove(&name.to_ascii_uppercase());
                }
                Ok(())
            }
            "RSRESET" => {
                self.rs = 0;
                Ok(())
            }
            "RSSET" => {
                self.rs = self.value(tail)?;
                Ok(())
            }
            "ASSERT" | "STATIC_ASSERT" => {
                let values = source::args(tail)?;
                let warning = values
                    .first()
                    .is_some_and(|v| v.eq_ignore_ascii_case("WARN"));
                let index = usize::from(
                    warning
                        || values
                            .first()
                            .is_some_and(|v| v.eq_ignore_ascii_case("FATAL")),
                );
                self.assertions.push((
                    self.tree(values.get(index).ok_or("ASSERT requires expression")?)?,
                    line.clone(),
                    self.pc_section()
                        .and_then(|s| self.object.sections[s].address)
                        .map(|base| base + self.offset() as i32),
                    self.pc_section(),
                    warning,
                ));
                Ok(())
            }
            "FAIL" => Err(format!("FAIL: {tail}")),
            "WARN" => {
                eprintln!("{}:{}:1: warning: {}", line.file, line.line, tail);
                Ok(())
            }
            "PRINT" | "PRINTLN" => {
                for value in source::args(tail)? {
                    if let Ok(s) = self.string(&value) {
                        eprint!("{s}");
                    } else {
                        eprint!("{}", self.value(&value)?);
                    }
                }
                if command == "PRINTLN" {
                    eprintln!();
                }
                Ok(())
            }
            "NEWCHARMAP" | "SETCHARMAP" | "PUSHC" | "POPC" | "PRECHARMAP" | "PRECHMAP"
            | "POPCHARMAP" | "CHARMAP" => self.charmap(&command, tail),
            "PUSHO" => {
                self.options.push((self.q, self.pad));
                Ok(())
            }
            "POPO" => {
                let (q, pad) = self.options.pop().ok_or("POPO without PUSHO")?;
                self.q = q;
                self.pad = pad;
                Ok(())
            }
            "OPT" => {
                for opt in source::args(tail)? {
                    let lower = opt.to_ascii_lowercase();
                    if let Some(q) = lower.strip_prefix("q.") {
                        let q: u8 = q.parse().map_err(|_| "invalid Q precision")?;
                        if q > 31 {
                            return Err("invalid Q precision".into());
                        }
                        self.q = q;
                    } else if let Some(pad) = lower.strip_prefix('p') {
                        self.pad = self.value(pad)? as u8;
                    } else if !lower.starts_with('w') {
                        return Err(format!("unsupported OPT: {opt}"));
                    }
                }
                Ok(())
            }
            "ENDM" | "ENDR" | "ELSE" | "ELIF" | "ENDC" => Err(format!("unexpected {command}")),
            _ => {
                let operands = source::args(tail)?;
                let mut operands: Vec<String> = operands
                    .iter()
                    .map(|v| {
                        self.anonymous_refs(&source::replace_words(v, |name| {
                            self.constants.get(name).map(|v| v.to_string())
                        }))
                    })
                    .collect();
                for operand in &mut operands {
                    if operand.contains('\'') {
                        if let Ok(value) = self.value(operand) {
                            *operand = value.to_string();
                        }
                    }
                }
                if matches!(command.as_str(), "RST" | "BIT" | "RES" | "SET") {
                    if let Some(first) = operands.first_mut() {
                        if let Ok(value) = self.value(first) {
                            *first = value.to_string();
                        }
                    }
                }
                let encoded = encode::encode(head, &operands)?;
                let section = self.section.ok_or("instruction outside SECTION")?;
                let start = self.object.sections[section].data.len();
                let pc = self.offset();
                self.emit(&encoded.bytes, line)?;
                for p in encoded.patches {
                    let tree =
                        self.numeric_tree(self.tree(&self.anonymous_refs(&p.expression))?)?;
                    let pc_section = self.pc_section().ok_or("missing PC section")?;
                    let source = self.provenance(line);
                    self.object.sections[section].patches.push(Patch {
                        offset: start + p.offset,
                        kind: if p.relative {
                            3
                        } else if p.width == 2 {
                            1
                        } else {
                            0
                        },
                        pc_after: (pc + encoded.bytes.len()) as i32,
                        pc_offset: pc as i32,
                        expression: tree,
                        pc_section,
                        source,
                        constraint: Some(p.kind),
                    });
                }
                Ok(())
            }
        }
    }

    fn anonymous_refs(&self, text: &str) -> String {
        let mut result = String::new();
        let mut chars = text.chars().peekable();
        while let Some(c) = chars.next() {
            if c == ':' && chars.peek().is_some_and(|c| matches!(c, '+' | '-')) {
                let sign = chars.next().unwrap();
                let mut n = 1;
                while chars.peek() == Some(&sign) {
                    chars.next();
                    n += 1;
                }
                let id = if sign == '+' {
                    self.anonymous + n
                } else {
                    (self.anonymous + 1).saturating_sub(n)
                };
                result.push_str(&format!("__koh_anon_{id}"));
            } else {
                result.push(c);
            }
        }
        result
    }

    fn end_implicit_unions(&mut self) {
        while self.unions.last().is_some_and(|frame| frame.3) {
            let (s, _, max, _) = self.unions.pop().unwrap();
            let size = max.max(self.object.sections[s].data.len());
            self.object.sections[s].data.resize(size, 0);
        }
    }
    fn open_section(&mut self, tail: &str, load: bool, line: &SourceLine) -> Result<(), String> {
        self.end_implicit_unions();
        let tail = tail.trim();
        let (modifier, rest) = source::word(tail);
        let (union, fragment, tail) = if modifier.eq_ignore_ascii_case("UNION") {
            (true, false, rest)
        } else if modifier.eq_ignore_ascii_case("FRAGMENT") {
            (false, true, rest)
        } else {
            (false, false, tail)
        };
        let args = source::args(tail)?;
        if args.len() < 2 {
            return Err("SECTION requires name and memory type".into());
        }
        let name = self.string(&args[0])?;
        let kind_text = args[1]
            .split('[')
            .next()
            .unwrap()
            .trim()
            .to_ascii_uppercase();
        let kind = match kind_text.as_str() {
            "ROM0" => 0,
            "ROMX" => 1,
            "VRAM" => 2,
            "SRAM" => 3,
            "WRAM0" => 4,
            "WRAMX" => 5,
            "OAM" => 6,
            "HRAM" => 7,
            _ => return Err("invalid memory type".into()),
        };
        if union && kind < 2 {
            return Err("SECTION UNION requires RAM".into());
        }
        let combination = if union {
            crate::model::SectionCombination::Union
        } else if fragment {
            crate::model::SectionCombination::Fragment
        } else {
            crate::model::SectionCombination::Normal
        };
        let bracket = |s: &str| -> Result<Option<String>, String> {
            if let Some((_, rest)) = s.split_once('[') {
                Ok(Some(rest.strip_suffix(']').ok_or("missing ]")?.into()))
            } else {
                Ok(None)
            }
        };
        let address = bracket(&args[1])?.map(|v| self.value(&v)).transpose()?;
        let mut bank = None;
        let mut align = 0;
        let mut align_offset = 0;
        for arg in &args[2..] {
            if arg.to_ascii_uppercase().starts_with("BANK[") {
                bank = Some(self.value(&bracket(arg)?.ok_or("BANK requires brackets")?)?);
            } else if arg.to_ascii_uppercase().starts_with("ALIGN[") {
                let values = source::args(&bracket(arg)?.ok_or("ALIGN requires brackets")?)?;
                let bits = self.value(&values[0])?;
                if !(0..=16).contains(&bits) {
                    return Err("invalid section alignment".into());
                }
                align = bits as u8;
                if values.len() > 1 {
                    align_offset = self.value(&values[1])?;
                }
                if align_offset < 0 || align_offset >= (1 << align) {
                    return Err("invalid alignment offset".into());
                }
            } else {
                return Err(format!("invalid SECTION option: {arg}"));
            }
        }
        let (base, size, max_bank) = match kind {
            0 => (0, 0x4000, 0),
            1 => (0x4000, 0x4000, 511),
            2 => (0x8000, 0x2000, 1),
            3 => (0xa000, 0x2000, 15),
            4 => (0xc000, 0x1000, 0),
            5 => (0xd000, 0x1000, 7),
            6 => (0xfe00, 0xa0, 0),
            _ => (0xff80, 0x7f, 0),
        };
        if address.is_some_and(|a| a < base || a >= base + size) {
            return Err("section address outside memory region".into());
        }
        if bank.is_some_and(|b| b < 0 || b > max_bank || ((kind == 1 || kind == 5) && b == 0)) {
            return Err("invalid section bank".into());
        }
        if address.is_some_and(|a| a.rem_euclid(1 << align) != align_offset) {
            return Err("fixed address conflicts with ALIGN".into());
        }
        let index = if let Some(index) = self.object.sections.iter().position(|s| s.name == name) {
            let s = &self.object.sections[index];
            if s.kind != kind
                || s.address != address
                || s.bank != bank
                || s.combination != combination
            {
                return Err("incompatible section redeclaration".into());
            }
            if union {
                let old = self.object.sections[index].data.len();
                self.union_sizes
                    .entry(index)
                    .and_modify(|max| *max = (*max).max(old))
                    .or_insert(old);
                self.object.sections[index].data.clear();
            }
            index
        } else {
            let index = self.object.sections.len();
            self.object.sections.push(Section {
                name,
                kind,
                address,
                bank,
                align,
                align_offset,
                combination,
                source: Some(self.provenance(line)),
                ..Section::default()
            });
            index
        };
        if load {
            if kind < 2 {
                return Err("LOAD requires RAM target".into());
            }
            let enclosing = self.section.ok_or("LOAD outside SECTION")?;
            self.load = Some((index, enclosing, self.anchor.clone()));
        } else {
            self.load = None;
            self.section = Some(index);
            if union {
                self.unions.push((index, 0, 0, true));
            }
        }
        Ok(())
    }

    fn emit(&mut self, bytes: &[u8], line: &SourceLine) -> Result<(), String> {
        let section = self.section.ok_or("data outside SECTION")?;
        let offset = self.object.sections[section].data.len();
        let limit = match self.object.sections[section].kind {
            0 | 1 => 0x4000,
            2 | 3 => 0x2000,
            4 | 5 => 0x1000,
            6 => 0xa0,
            _ => 0x7f,
        };
        if offset + bytes.len() > limit {
            return Err("section exceeds memory region".into());
        }
        self.object.sections[section].data.extend_from_slice(bytes);
        self.object.sections[section].lines.push(Line {
            offset,
            count: bytes.len(),
            file: line.file.clone(),
            line: line.line,
        });
        if let Some((load, _, _)) = &self.load {
            let len = self.object.sections[*load].data.len() + bytes.len();
            self.object.sections[*load].data.resize(len, 0);
        }
        Ok(())
    }
    fn emit_number(
        &mut self,
        tree: Expr,
        width: usize,
        relative: bool,
        line: &SourceLine,
        constraint: Option<encode::OperandKind>,
    ) -> Result<(), String> {
        let section = self.section.ok_or("data outside SECTION")?;
        let start = self.object.sections[section].data.len();
        let pc = self.offset();
        self.emit(&vec![0; width], line)?;
        let pc_section = self.pc_section().ok_or("missing PC section")?;
        let source = self.provenance(line);
        self.object.sections[section].patches.push(Patch {
            offset: start,
            kind: if relative {
                3
            } else if width == 4 {
                2
            } else if width == 2 {
                1
            } else {
                0
            },
            pc_after: (pc + width) as i32,
            pc_offset: pc as i32,
            expression: tree,
            pc_section,
            source,
            constraint,
        });
        Ok(())
    }
    fn alignment_padding(&mut self, values: &[String]) -> Result<i32, String> {
        let bits = self.value(values.first().ok_or("ALIGN requires exponent")?)?;
        if !(0..=16).contains(&bits) {
            return Err("ALIGN exponent outside 0..16".into());
        }
        let requested = if values.len() > 1 {
            self.value(&values[1])?
        } else {
            0
        };
        let boundary = 1i32 << bits;
        if requested < 0 || requested >= boundary {
            return Err("ALIGN offset outside alignment".into());
        }
        let section = self.pc_section().ok_or("ALIGN outside SECTION")?;
        let sec = &mut self.object.sections[section];
        let base = sec.address.unwrap_or(sec.align_offset);
        if sec.address.is_none() {
            sec.align = sec.align.max(bits as u8);
        }
        Ok((requested - base - sec.data.len() as i32).rem_euclid(boundary))
    }
    fn encode_string(&self, text: &str) -> Result<Vec<i32>, String> {
        let map = self.maps.get(&self.map).ok_or("unknown charmap")?;
        let mut result = Vec::new();
        let mut rest = text;
        while !rest.is_empty() {
            if let Some((key, value)) = map
                .iter()
                .filter(|(key, _)| rest.starts_with(key.as_str()))
                .max_by_key(|(key, _)| key.len())
            {
                result.extend(value);
                rest = &rest[key.len()..];
            } else {
                let c = rest.chars().next().unwrap();
                let mut buf = [0; 4];
                result.extend(c.encode_utf8(&mut buf).bytes().map(i32::from));
                rest = &rest[c.len_utf8()..];
            }
        }
        Ok(result)
    }
    fn charmap(&mut self, command: &str, tail: &str) -> Result<(), String> {
        let args = source::args(tail)?;
        match command {
            "PUSHC" | "PRECHARMAP" | "PRECHMAP" => self.map_stack.push(self.map.clone()),
            "POPC" | "POPCHARMAP" => {
                self.map = self.map_stack.pop().ok_or("charmap stack is empty")?
            }
            "NEWCHARMAP" => {
                let name = args
                    .first()
                    .ok_or("NEWCHARMAP requires name")?
                    .trim_matches('"')
                    .to_string();
                if self.maps.contains_key(&name) {
                    return Err("charmap already exists".into());
                }
                let base = if let Some(base) = args.get(1) {
                    self.maps
                        .get(base.trim_matches('"'))
                        .ok_or("unknown base charmap")?
                        .clone()
                } else {
                    Vec::new()
                };
                self.maps.insert(name.clone(), base);
                self.map = name;
            }
            "SETCHARMAP" => {
                let name = args
                    .first()
                    .ok_or("SETCHARMAP requires name")?
                    .trim_matches('"');
                let name = if name.is_empty() { "main" } else { name };
                if !self.maps.contains_key(name) {
                    return Err("unknown charmap".into());
                }
                self.map = name.into();
            }
            "CHARMAP" => {
                if args.len() < 2 {
                    return Err("CHARMAP requires key and values".into());
                }
                let key = self.string(&args[0])?;
                if key.is_empty() {
                    return Err("empty charmap key".into());
                }
                let values = args[1..]
                    .iter()
                    .map(|v| self.value(v))
                    .collect::<Result<Vec<_>, _>>()?;
                let map = self.maps.get_mut(&self.map).unwrap();
                map.retain(|(old, _)| *old != key);
                map.push((key, values));
            }
            _ => unreachable!(),
        }
        Ok(())
    }

    fn finish(&mut self) {
        for (s, size) in &self.union_sizes {
            let size = (*size).max(self.object.sections[*s].data.len());
            self.object.sections[*s].data.resize(size, 0);
        }
        for (name, tree) in self.deferred.clone() {
            let tree = self.freeze(tree);
            if let Some(value) = self.invariant(&tree) {
                self.constants.insert(name, value);
            } else if let Some((offset, bases)) = self.affine(&tree) {
                if bases.len() == 1 && bases.values().next() == Some(&1) && offset >= 0 {
                    let section = *bases.keys().next().unwrap();
                    self.object.symbols.push(Symbol {
                        name: name.clone(),
                        kind: 0,
                        visibility: u8::from(self.exports.contains(&name)),
                        section: Some(section),
                        value: offset,
                        source: self.symbol_sources.get(&name).cloned(),
                    });
                } else {
                    let source = self.symbol_sources.get(&name);
                    self.errors.push(Diagnostic {
                        file: source
                            .map(|s| s.file.clone())
                            .unwrap_or_else(|| self.current_file.clone()),
                        line: source.map_or(1, |s| s.line),
                        message: format!("constant '{name}' is not an assembly-time value"),
                    });
                }
            } else {
                let source = self.symbol_sources.get(&name);
                let message = expr::eval(&tree, &|n| self.lookup(n), 0)
                    .err()
                    .unwrap_or_else(|| "unresolved expression".into());
                self.errors.push(Diagnostic {
                    file: source
                        .map(|s| s.file.clone())
                        .unwrap_or_else(|| self.current_file.clone()),
                    line: source.map_or(1, |s| s.line),
                    message: format!("constant '{name}': {message}"),
                });
            }
        }
        for symbol in &mut self.object.symbols {
            if self.exports.contains(&symbol.name) {
                symbol.visibility = 1;
            }
        }
        let assertions = std::mem::take(&mut self.assertions);
        for (tree, line, pc, section, warning) in assertions {
            let result = if has_pc(&tree) && pc.is_none() {
                Err("ASSERT requires a placed current address".into())
            } else {
                expr::eval(&tree, &|n| self.lookup_at(n, section), pc.unwrap_or(0))
            };
            match result {
                Ok(v) if v != 0 => (),
                Ok(_) if warning => {
                    eprintln!("{}:{}:1: warning: assertion failed", line.file, line.line)
                }
                Ok(_) => self.error(&line, "assertion failed"),
                Err(e) => self.error(&line, e),
            }
        }
        for s in 0..self.object.sections.len() {
            let patches = std::mem::take(&mut self.object.sections[s].patches);
            for mut p in patches {
                p.expression = self.freeze(p.expression);
                let pc = self.object.sections[p.pc_section].address.unwrap_or(0) + p.pc_offset;
                let value = if self.object.sections[p.pc_section].address.is_none()
                    && has_pc(&p.expression)
                {
                    Err("unplaced current address".into())
                } else {
                    expr::eval(
                        &p.expression,
                        &|n| self.lookup_at(n, Some(p.pc_section)),
                        pc,
                    )
                };
                let relative_same = if p.kind == 3 {
                    self.relative_value(&p.expression, p.pc_section, p.pc_offset)
                } else {
                    None
                };
                let value = relative_same.map(|v| v - p.pc_after).or_else(|| {
                    if p.kind == 3 && self.object.sections[p.pc_section].address.is_none() {
                        return None;
                    }
                    value
                        .ok()
                        .or_else(|| self.invariant(&p.expression))
                        .map(|v| {
                            if p.kind == 3 {
                                v - (self.object.sections[p.pc_section].address.unwrap_or(0)
                                    + p.pc_after)
                            } else {
                                v
                            }
                        })
                });
                if let Some(value) = value {
                    let constraint = p.constraint;
                    let valid = match constraint {
                        Some(encode::OperandKind::Signed8)
                        | Some(encode::OperandKind::Relative8) => (-128..=127).contains(&value),
                        Some(encode::OperandKind::High8) => (0xff00..=0xffff).contains(&value),
                        Some(encode::OperandKind::Unsigned8) => (-128..=255).contains(&value),
                        Some(encode::OperandKind::Unsigned16) => (-32768..=65535).contains(&value),
                        None => p.kind != 3 || (-128..=127).contains(&value),
                    };
                    if !valid {
                        self.errors.push(Diagnostic {
                            file: p.source.file,
                            line: p.source.line,
                            message: format!("operand out of range: {value}"),
                        });
                        continue;
                    }
                    let width = match p.kind {
                        1 => 2,
                        2 => 4,
                        _ => 1,
                    };
                    for i in 0..width {
                        self.object.sections[s].data[p.offset + i] =
                            (value as u32 >> (8 * i)) as u8;
                    }
                    continue;
                }
                self.object.sections[s].patches.push(p);
            }
        }
        for (name, value) in &self.constants {
            if name != "_NARG" && self.symbol_sources.contains_key(name) {
                self.object.symbols.push(Symbol {
                    name: name.clone(),
                    kind: 1,
                    visibility: u8::from(self.exports.contains(name)),
                    section: None,
                    value: *value,
                    source: self.symbol_sources.get(name).cloned(),
                });
            }
        }
        self.object.symbols.sort_by(|a, b| a.name.cmp(&b.name));
    }
    fn affine(&self, tree: &Expr) -> Option<(i32, HashMap<usize, i32>)> {
        match tree {
            Expr::Number(n) => Some((*n, HashMap::new())),
            Expr::Pc => None,
            Expr::Symbol(name) => {
                let sym = self.object.symbols.iter().find(|s| s.name == *name)?;
                if let Some(section) = sym.section {
                    if let Some(base) = self.object.sections[section].address {
                        Some((base.wrapping_add(sym.value), HashMap::new()))
                    } else {
                        Some((sym.value, HashMap::from([(section, 1)])))
                    }
                } else {
                    Some((sym.value, HashMap::new()))
                }
            }
            Expr::Binary(op, a, b) if op == "+" || op == "-" => {
                let (a, mut ac) = self.affine(a)?;
                let (b, bc) = self.affine(b)?;
                let sign = if op == "+" { 1 } else { -1 };
                for (k, v) in bc {
                    let old = ac.get(&k).copied().unwrap_or(0);
                    ac.insert(k, old.wrapping_add(v.wrapping_mul(sign)));
                }
                ac.retain(|_, v| *v != 0);
                Some((a.wrapping_add(b.wrapping_mul(sign)), ac))
            }
            Expr::Unary(op, a) if op == "+" || op == "-" => {
                let (v, c) = self.affine(a)?;
                let sign = if op == "+" { 1 } else { -1 };
                Some((
                    v.wrapping_mul(sign),
                    c.into_iter()
                        .map(|(k, v)| (k, v.wrapping_mul(sign)))
                        .collect(),
                ))
            }
            _ => expr::eval(tree, &|n| self.lookup(n), 0)
                .ok()
                .map(|v| (v, HashMap::new())),
        }
    }
    fn invariant(&self, tree: &Expr) -> Option<i32> {
        if tree.references_pc() {
            return None;
        }
        let (value, bases) = self.affine(tree)?;
        if bases.is_empty() { Some(value) } else { None }
    }
    fn relative_value(&self, tree: &Expr, section: usize, pc: i32) -> Option<i32> {
        match tree {
            Expr::Symbol(name) => self
                .object
                .symbols
                .iter()
                .find(|s| s.name == *name && s.section == Some(section))
                .map(|s| s.value),
            Expr::Binary(op, a, b) if op == "+" || op == "-" => {
                let a = self.relative_value(a, section, pc)?;
                let b = expr::eval(b, &|n| self.constants.get(n).copied(), 0).ok()?;
                Some(if op == "+" {
                    a.wrapping_add(b)
                } else {
                    a.wrapping_sub(b)
                })
            }
            Expr::Pc => Some(pc),
            _ => None,
        }
    }
}

fn has_pc(tree: &Expr) -> bool {
    match tree {
        Expr::Pc => true,
        Expr::Unary(_, a) => has_pc(a),
        Expr::Binary(_, a, b) => has_pc(a) || has_pc(b),
        Expr::Call(name, _) if name == "BANK" => false,
        Expr::Call(_, args) => args.iter().any(has_pc),
        _ => false,
    }
}
