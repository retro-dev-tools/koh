#[derive(Clone, Debug)]
pub struct SourceLine {
    pub text: String,
    pub file: String,
    pub line: u32,
}

pub fn lines(text: &str, file: &str) -> Vec<SourceLine> {
    let text = text.trim_start_matches('\u{feff}');
    let mut result = Vec::new();
    let mut pending = String::new();
    let mut start = 1;
    let mut line = 1;
    let mut i = 0;
    while i < text.len() {
        if let Some((end, raw, _)) = literal(text, i) {
            let token = &text[i..end];
            line += token.bytes().filter(|b| *b == b'\n').count() as u32;
            if raw {
                pending.push_str(token);
            } else {
                let mut j = 0;
                while j < token.len() {
                    if let Some(end) = continuation(token, j) {
                        j = end;
                    } else {
                        let c = token[j..].chars().next().unwrap();
                        pending.push(c);
                        j += c.len_utf8();
                        if c == '\\' && j < token.len() {
                            let c = token[j..].chars().next().unwrap();
                            pending.push(c);
                            j += c.len_utf8();
                        }
                    }
                }
            }
            i = end;
        } else if let Some(end) = continuation(text, i) {
            line += text[i..end].bytes().filter(|b| *b == b'\n').count() as u32;
            pending.push(' ');
            i = end;
        } else if text.as_bytes()[i] == b';' {
            i = text[i..].find('\n').map_or(text.len(), |n| i + n);
        } else if text.as_bytes()[i] == b'\n' {
            result.push(SourceLine {
                text: std::mem::take(&mut pending),
                file: file.into(),
                line: start,
            });
            line += 1;
            start = line;
            i += 1;
        } else if text.as_bytes()[i] == b'\r' {
            i += 1;
        } else {
            let c = text[i..].chars().next().unwrap();
            pending.push(c);
            i += c.len_utf8();
        }
    }
    if !pending.is_empty() {
        result.push(SourceLine {
            text: pending,
            file: file.into(),
            line: start,
        });
    }
    result
}

fn continuation(text: &str, start: usize) -> Option<usize> {
    if text.as_bytes().get(start) != Some(&b'\\') {
        return None;
    }
    let mut i = start + 1;
    while matches!(text.as_bytes().get(i), Some(b' ' | b'\t')) {
        i += 1;
    }
    if text.as_bytes().get(i) == Some(&b';') {
        i = text[i..].find('\n').map_or(text.len(), |n| i + n);
    }
    if text.as_bytes().get(i) == Some(&b'\r') {
        i += 1;
    }
    (text.as_bytes().get(i) == Some(&b'\n')).then_some(i + 1)
}

fn literal(text: &str, start: usize) -> Option<(usize, bool, bool)> {
    let raw = text[start..].starts_with("#\"");
    let opening = start + usize::from(raw);
    let quote = *text.as_bytes().get(opening)?;
    if !matches!(quote, b'\'' | b'"') {
        return None;
    }
    let triple = quote == b'"' && text[opening..].starts_with("\"\"\"");
    let delimiter = if triple { 3 } else { 1 };
    let mut i = opening + delimiter;
    while i < text.len() {
        if (triple && text[i..].starts_with("\"\"\"")) || (!triple && text.as_bytes()[i] == quote) {
            return Some((i + delimiter, raw, true));
        }
        if !raw && text.as_bytes()[i] == b'\\' {
            if let Some(end) = continuation(text, i) {
                i = end;
                continue;
            }
            i += 1;
            if i < text.len() {
                i += text[i..].chars().next().unwrap().len_utf8();
            }
        } else {
            if !triple && matches!(text.as_bytes()[i], b'\n' | b'\r') {
                return Some((i, raw, false));
            }
            i += text[i..].chars().next().unwrap().len_utf8();
        }
    }
    Some((i, raw, false))
}

pub fn strip_comment(text: &str) -> &str {
    let mut i = 0;
    while i < text.len() {
        if let Some((end, _, _)) = literal(text, i) {
            i = end;
        } else if text.as_bytes()[i] == b';' {
            return &text[..i];
        } else {
            i += text[i..].chars().next().unwrap().len_utf8();
        }
    }
    text
}

pub fn word(text: &str) -> (&str, &str) {
    let text = text.trim();
    let end = text.find(char::is_whitespace).unwrap_or(text.len());
    (&text[..end], text[end..].trim())
}

pub fn args(text: &str) -> Result<Vec<String>, String> {
    split_args(text, false)
}

pub fn macro_args(text: &str) -> Result<Vec<String>, String> {
    split_args(text, true)
}

fn split_args(text: &str, macros: bool) -> Result<Vec<String>, String> {
    if text.trim().is_empty() {
        return Ok(Vec::new());
    }
    let mut result = Vec::new();
    let mut current = String::new();
    let mut stack = Vec::new();
    let mut angle = false;
    let mut i = 0;
    while i < text.len() {
        if let Some((end, _, closed)) = literal(text, i) {
            if !closed {
                return Err("unterminated string".into());
            }
            current.push_str(&text[i..end]);
            i = end;
            continue;
        }
        let c = text[i..].chars().next().unwrap();
        i += c.len_utf8();
        if c == '\\' && macros && text[i..].starts_with(',') {
            current.push(',');
            i += 1;
            continue;
        }
        if c == '<' && macros && current.trim().is_empty() && stack.is_empty() && !angle {
            angle = true;
            continue;
        }
        if c == '>' && angle {
            angle = false;
            continue;
        }
        if angle {
            current.push(c);
            continue;
        }
        match c {
            '(' => stack.push(')'),
            '[' => stack.push(']'),
            '{' => stack.push('}'),
            ')' | ']' | '}' => {
                if stack.pop() != Some(c) {
                    return Err("unmatched delimiter".into());
                }
            }
            ',' if stack.is_empty() => {
                result.push(current.trim().to_owned());
                current.clear();
                continue;
            }
            _ => (),
        }
        current.push(c);
    }
    if angle || !stack.is_empty() {
        return Err("unterminated string or delimiter".into());
    }
    if !macros || !current.trim().is_empty() || result.is_empty() {
        result.push(current.trim().to_owned());
    }
    if !macros && result.iter().any(String::is_empty) {
        return Err("missing argument".into());
    }
    Ok(result)
}

pub fn label(text: &str) -> (Option<(&str, bool)>, &str) {
    let text = text.trim();
    let end = text
        .find(|c: char| c == ':' || c.is_whitespace())
        .unwrap_or(text.len());
    if text.as_bytes().get(end) == Some(&b':') {
        let export = text.as_bytes().get(end + 1) == Some(&b':');
        (
            Some((&text[..end], export)),
            text[end + 1 + usize::from(export)..].trim(),
        )
    } else {
        (None, text)
    }
}

pub fn ident(c: char) -> bool {
    c.is_alphanumeric() || matches!(c, '_' | '.' | '#' | '$')
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn macro_quoting_and_expression_comparisons() {
        assert_eq!(
            macro_args("<$aa, $bb>, a<b, 1\\,2, ,z").unwrap(),
            ["$aa, $bb", "a<b", "1,2", "", "z"]
        );
        assert_eq!(macro_args("<>, value,").unwrap(), ["", "value"]);
        assert!(macro_args("<unclosed, value").is_err());
        assert_eq!(
            args("a<b, a>b, FUNC(1,2)").unwrap(),
            ["a<b", "a>b", "FUNC(1,2)"]
        );
    }

    #[test]
    fn literal_boundaries_are_shared() {
        let input = "#\"slash\\\", 2 ; comment";
        assert_eq!(strip_comment(input), "#\"slash\\\", 2 ");
        assert_eq!(args(strip_comment(input)).unwrap(), ["#\"slash\\\"", "2"]);
        assert_eq!(
            replace_words(input, |s| (s == "slash").then(|| "changed".into())),
            input
        );
        assert!(args("([)]").is_err());
        assert!(args("\"unclosed").is_err());
        assert!(args("#\"\"\"unclosed").is_err());
    }

    #[test]
    fn multiline_literals_keep_comments_and_line_numbers() {
        let text = "\u{feff}db #\"\"\"first;literal\nsecond\\\nthird\"\"\",1 ; removed\nnop\n";
        let parsed = lines(text, "test.asm");
        assert_eq!(parsed.len(), 2);
        assert_eq!(
            parsed[0].text,
            "db #\"\"\"first;literal\nsecond\\\nthird\"\"\",1 "
        );
        assert_eq!(parsed[0].line, 1);
        assert_eq!(parsed[1].line, 4);
        assert_eq!(args(&parsed[0].text[3..]).unwrap().len(), 2);
    }

    #[test]
    fn continuations_preserve_token_and_string_content() {
        let parsed = lines(
            "db 1, \\ ; continuation\n 2\n db \"ab\\\ncd;inside\" ; removed\nnop",
            "test.asm",
        );
        assert_eq!(parsed.len(), 3);
        assert_eq!(args(&parsed[0].text[3..]).unwrap(), ["1", "2"]);
        assert_eq!(parsed[1].text.trim(), "db \"abcd;inside\"");
        assert_eq!(parsed[1].line, 3);
        assert_eq!(parsed[2].line, 5);
    }
}

pub fn replace_words(text: &str, mut f: impl FnMut(&str) -> Option<String>) -> String {
    let mut result = String::new();
    let mut start = 0;
    while start < text.len() {
        if let Some((end, _, _)) = literal(text, start) {
            result.push_str(&text[start..end]);
            start = end;
            continue;
        }
        let c = text[start..].chars().next().unwrap();
        if c == '\\' {
            result.push(c);
            start += 1;
            if start < text.len() {
                let next = text[start..].chars().next().unwrap();
                result.push(next);
                start += next.len_utf8();
            }
            continue;
        }
        if ident(c) && !c.is_ascii_digit() && c != '$' {
            let mut end = start + c.len_utf8();
            while end < text.len() {
                let next = text[end..].chars().next().unwrap();
                if !ident(next) {
                    break;
                }
                end += next.len_utf8();
            }
            let name = &text[start..end];
            result.push_str(&f(name).unwrap_or_else(|| name.into()));
            start = end;
        } else {
            result.push(c);
            start += c.len_utf8();
        }
    }
    result
}
