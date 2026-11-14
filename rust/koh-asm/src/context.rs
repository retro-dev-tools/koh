use crate::expr::{self, Expr};
use std::{fs::File, io::Read, path::Path};

pub fn fold(
    tree: Expr,
    maps: &[(String, Vec<i32>)],
    base_file: &str,
    resolver: &dyn Fn(&str) -> Option<i32>,
    pc: i32,
) -> Result<Expr, String> {
    let child = |tree| fold(tree, maps, base_file, resolver, pc);
    let (name, args) = match tree {
        Expr::Unary(op, value) => return Ok(Expr::Unary(op, Box::new(child(*value)?))),
        Expr::Binary(op, left, right) => {
            return Ok(Expr::Binary(
                op,
                Box::new(child(*left)?),
                Box::new(child(*right)?),
            ));
        }
        Expr::Call(name, args) => (
            name,
            args.into_iter().map(child).collect::<Result<Vec<_>, _>>()?,
        ),
        Expr::Character(character) => {
            let key = character.to_string();
            if let Some((_, values)) = maps.iter().find(|(name, _)| name == &key) {
                if values.len() == 1 {
                    return Ok(Expr::Number(values[0]));
                }
                if values.is_empty() || values.len() > 4 {
                    return Err("Character constant requires one to four mapped bytes".into());
                }
                let mut value = 0u32;
                for byte in values {
                    let byte = u8::try_from(*byte)
                        .map_err(|_| "Multiple character constant values must be bytes")?;
                    value = (value << 8) | u32::from(byte);
                }
                return Ok(Expr::Number(value as i32));
            }
            return Ok(Expr::Number(character as i32));
        }
        other => return Ok(other),
    };
    let string = |index| expr::eval_string(&args[index], resolver, pc);
    let number = |index| expr::eval(&args[index], resolver, pc);
    let count = |minimum, maximum| {
        if (minimum..=maximum).contains(&args.len()) {
            Ok(())
        } else {
            Err(format!("{name} requires {minimum}..={maximum} arguments"))
        }
    };
    match name.as_str() {
        "READFILE" => {
            count(1, 2)?;
            let filename = string(0)?;
            let limit = if args.len() == 2 {
                u64::try_from(number(1)?).map_err(|_| "READFILE limit must be nonnegative")?
            } else {
                u64::MAX
            };
            let path = Path::new(&filename);
            let path = if path.is_absolute() {
                path.to_path_buf()
            } else {
                Path::new(base_file)
                    .parent()
                    .unwrap_or(Path::new("."))
                    .join(path)
            };
            let mut bytes = Vec::new();
            File::open(&path)
                .and_then(|file| file.take(limit).read_to_end(&mut bytes))
                .map_err(|error| format!("READFILE '{}': {error}", path.display()))?;
            Ok(Expr::String(String::from_utf8(bytes).map_err(|_| {
                format!("READFILE '{}': contents are not UTF-8", path.display())
            })?))
        }
        "INCHARMAP" => {
            count(1, 1)?;
            let text = string(0)?;
            Ok(Expr::Number(
                (!text.is_empty() && maps.iter().any(|(key, _)| key == &text)) as i32,
            ))
        }
        "CHARLEN" => {
            count(1, 1)?;
            Ok(Expr::Number(entries(&string(0)?, maps).len() as i32))
        }
        "BYTELEN" => {
            count(1, 1)?;
            Ok(Expr::Number(string(0)?.len() as i32))
        }
        "STRBYTE" => {
            count(2, 2)?;
            let text = string(0)?;
            let index = string_index(number(1)?, text.len());
            Ok(Expr::Number(
                text.as_bytes().get(index).copied().unwrap_or(0) as i32,
            ))
        }
        "STRCHAR" => {
            count(2, 2)?;
            let text = string(0)?;
            let pieces = entries(&text, maps);
            let index = string_index(number(1)?, pieces.len());
            Ok(Expr::String(
                pieces.get(index).map_or("", |(text, _)| text).into(),
            ))
        }
        "REVCHAR" => {
            count(1, usize::MAX)?;
            let values = args
                .iter()
                .map(|value| expr::eval(value, resolver, pc))
                .collect::<Result<Vec<_>, _>>()?;
            let mut matching = maps.iter().filter(|(_, mapped)| *mapped == values);
            let (key, _) = matching.next().ok_or("REVCHAR has no matching mapping")?;
            if matching.next().is_some() {
                return Err("REVCHAR has multiple matching mappings".into());
            }
            Ok(Expr::String(key.clone()))
        }
        "CHARSIZE" | "CHARVAL" => {
            count(1, if name == "CHARVAL" { 2 } else { 1 })?;
            let text = string(0)?;
            let (_, values) = maps
                .iter()
                .find(|(key, _)| key == &text)
                .ok_or_else(|| format!("{name} has no matching mapping"))?;
            if name == "CHARSIZE" {
                return Ok(Expr::Number(values.len() as i32));
            }
            let index = if args.len() == 2 {
                index(number(1)?, values.len()).ok_or("CHARVAL index out of range")?
            } else if values.len() == 1 {
                0
            } else {
                return Err("CHARVAL requires an index for multiple values".into());
            };
            Ok(Expr::Number(values[index]))
        }
        "CHARCMP" => {
            count(2, 2)?;
            let a = string(0)?;
            let b = string(1)?;
            let values = |text: &str| {
                entries(text, maps)
                    .into_iter()
                    .flat_map(|(_, values)| values)
                    .collect::<Vec<_>>()
            };
            Ok(Expr::Number(match values(&a).cmp(&values(&b)) {
                std::cmp::Ordering::Less => -1,
                std::cmp::Ordering::Equal => 0,
                std::cmp::Ordering::Greater => 1,
            }))
        }
        _ => Ok(Expr::Call(name, args)),
    }
}

fn string_index(value: i32, length: usize) -> usize {
    if value < 0 {
        (length as i64 + i64::from(value)).max(0) as usize
    } else {
        value as usize
    }
}

fn index(value: i32, length: usize) -> Option<usize> {
    let value = value as i64;
    let value = if value < 0 {
        length as i64 + value
    } else {
        value
    };
    if value < 0 || value >= length as i64 {
        None
    } else {
        Some(value as usize)
    }
}

fn entries<'a>(mut text: &'a str, maps: &[(String, Vec<i32>)]) -> Vec<(&'a str, Vec<i32>)> {
    let mut result = Vec::new();
    while !text.is_empty() {
        if let Some((key, values)) = maps
            .iter()
            .filter(|(key, _)| !key.is_empty() && text.starts_with(key.as_str()))
            .max_by_key(|(key, _)| key.len())
        {
            result.push((&text[..key.len()], values.clone()));
            text = &text[key.len()..];
        } else {
            let size = text.chars().next().unwrap().len_utf8();
            result.push((
                &text[..size],
                text.as_bytes()[..size]
                    .iter()
                    .map(|b| i32::from(*b))
                    .collect(),
            ));
            text = &text[size..];
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    fn map() -> Vec<(String, Vec<i32>)> {
        vec![
            ("a".into(), vec![1]),
            ("ab".into(), vec![0x1234, 2]),
            ("é".into(), vec![7]),
        ]
    }
    fn folded(text: &str) -> Expr {
        fold(expr::parse(text).unwrap(), &map(), "test.asm", &|_| None, 0).unwrap()
    }
    fn value(text: &str) -> i32 {
        expr::eval(&folded(text), &|_| None, 0).unwrap()
    }
    #[test]
    fn longest_map_and_unicode() {
        assert_eq!(value("STRBYTE(\"ab\",-3)"), 97);
        assert_eq!(value("STRBYTE(\"ab\",99)"), 0);
        assert_eq!(value("STRBYTE(\"\",-1)"), 0);
        assert_eq!(folded("STRCHAR(\"abc\",9)"), Expr::String(String::new()));
        assert_eq!(folded("STRCHAR(\"abc\",-9)"), Expr::String("ab".into()));
        assert_eq!(folded("STRCHAR(\"\",0)"), Expr::String(String::new()));
        assert_eq!(value("CHARLEN(\"abé🌍\")"), 3);
        assert_eq!(value("INCHARMAP(\"ab\")"), 1);
        assert_eq!(value("INCHARMAP(\"abc\")"), 0);
        assert_eq!(folded("STRCHAR(\"abé🌍\",0)"), Expr::String("ab".into()));
        assert_eq!(folded("STRCHAR(\"abé🌍\",-1)"), Expr::String("🌍".into()));
        assert_eq!(value("BYTELEN(\"abé\")"), 4);
        assert_eq!(value("STRBYTE(\"abé\",-1)"), 0xa9);
    }
    #[test]
    fn mapping_values() {
        assert_eq!(folded("REVCHAR($1234,2)"), Expr::String("ab".into()));
        assert_eq!(value("CHARSIZE(\"ab\")"), 2);
        assert_eq!(value("CHARVAL(\"ab\",-1)"), 2);
        assert_eq!(value("CHARCMP(\"a\",\"é\")"), -1);
        assert_eq!(value("STRLEN(REVCHAR(7))"), 1);
    }
    #[test]
    fn character_constants_keep_mapping_context() {
        assert_eq!(expr::parse("'a'").unwrap(), Expr::Character('a'));
        assert_eq!(folded("'a'"), Expr::Number(1));
        assert_eq!(folded("'é'"), Expr::Number(7));
        assert_eq!(folded("'b'"), Expr::Number(98));
        let fold_char = |values| {
            fold(
                Expr::Character('A'),
                &[("A".into(), values)],
                "test.asm",
                &|_| None,
                0,
            )
        };
        assert_eq!(fold_char(vec![0x12, 0x34]).unwrap(), Expr::Number(0x1234));
        assert_eq!(
            fold_char(vec![255, 255, 255, 255]).unwrap(),
            Expr::Number(-1)
        );
        assert_eq!(fold_char(vec![0x1234]).unwrap(), Expr::Number(0x1234));
        for values in [vec![], vec![1, 2, 3, 4, 5], vec![1, 256], vec![-1, 1]] {
            assert!(fold_char(values).is_err());
        }
    }
    #[test]
    fn contextual_children_preserve_relocations() {
        let tree = folded("label+CHARLEN(\"ab\")");
        assert_eq!(
            expr::eval(&tree, &|s| if s == "label" { Some(10) } else { None }, 0).unwrap(),
            11
        );
        assert_eq!(
            folded("SECTION(label)"),
            expr::parse("SECTION(label)").unwrap()
        );
    }
    #[test]
    fn invalid_arguments() {
        for text in [
            "INCHARMAP()",
            "REVCHAR(99)",
            "CHARVAL(\"ab\")",
            "CHARVAL(\"ab\",2)",
            "READFILE(\"missing\",-1)",
        ] {
            assert!(
                fold(expr::parse(text).unwrap(), &map(), "test.asm", &|_| None, 0).is_err(),
                "{text}"
            );
        }
        assert!(
            fold(
                expr::parse("REVCHAR(1)").unwrap(),
                &[("a".into(), vec![1]), ("b".into(), vec![1])],
                "test.asm",
                &|_| None,
                0
            )
            .is_err()
        );
    }
    #[test]
    fn readfile_relative_limits_and_encoding() {
        let directory = std::env::temp_dir().join(format!("koh-context-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        std::fs::write(directory.join("text.txt"), "hello é").unwrap();
        std::fs::write(directory.join("binary.bin"), [255]).unwrap();
        let base = directory.join("main.asm");
        let base = base.to_str().unwrap();
        let read = |text: &str| fold(expr::parse(text).unwrap(), &[], base, &|_| None, 0);
        assert_eq!(
            read("READFILE(\"text.txt\",5)").unwrap(),
            Expr::String("hello".into())
        );
        assert_eq!(
            read("READFILE(\"text.txt\",0)").unwrap(),
            Expr::String(String::new())
        );
        assert_eq!(
            expr::eval_string(
                &read("STRUPR(READFILE(\"text.txt\"))").unwrap(),
                &|_| None,
                0
            )
            .unwrap(),
            "HELLO é"
        );
        assert!(read("READFILE(\"binary.bin\")").is_err());
        assert!(read("READFILE(\"absent\")").is_err());
        std::fs::remove_dir_all(directory).unwrap();
    }
}
