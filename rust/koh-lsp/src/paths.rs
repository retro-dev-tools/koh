use serde_json::{Value, json};
use std::{
    io,
    path::{Component, Path, PathBuf},
};
use url::Url;

pub fn normalize(path: &Path) -> io::Result<PathBuf> {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()?.join(path)
    };
    let mut result = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                result.pop();
            }
            component => result.push(component.as_os_str()),
        }
    }
    Ok(result)
}

pub fn from_uri(uri: &str) -> io::Result<PathBuf> {
    for (index, byte) in uri.bytes().enumerate() {
        if byte == b'%'
            && !uri
                .as_bytes()
                .get(index + 1..index + 3)
                .is_some_and(|digits| digits.iter().all(u8::is_ascii_hexdigit))
        {
            return Err(invalid("invalid URI escape"));
        }
    }
    Url::parse(uri)
        .map_err(|_| invalid("invalid document URI"))?
        .to_file_path()
        .map_err(|_| invalid("document URI is not a local file"))
        .and_then(|path| normalize(&path))
}

pub fn to_uri(path: &Path) -> io::Result<String> {
    Url::from_file_path(normalize(path)?)
        .map(|uri| uri.to_string())
        .map_err(|_| invalid("invalid file path"))
}

pub fn key(path: &str) -> String {
    path.to_lowercase()
}

pub fn file_key(path: &str) -> Option<String> {
    normalize(Path::new(path))
        .ok()
        .map(|path| key(&path.to_string_lossy()))
}

pub fn same_file(left: &str, right: &str) -> bool {
    matches!((file_key(left), file_key(right)), (Some(left), Some(right)) if left == right)
}

pub(crate) fn lines(text: &str) -> Vec<(usize, usize)> {
    let units: Vec<_> = text.encode_utf16().collect();
    let mut lines = Vec::new();
    let mut start = 0;
    let mut i = 0;
    while i < units.len() {
        if units[i] == 13 || units[i] == 10 {
            lines.push((start, i));
            if units[i] == 13 && units.get(i + 1) == Some(&10) {
                i += 1;
            }
            start = i + 1;
        }
        i += 1;
    }
    lines.push((start, units.len()));
    lines
}

/// UTF-16 line table: build once per text, then convert in O(log n).
pub struct Lines(Vec<(usize, usize)>);

impl Lines {
    pub fn new(text: &str) -> Self {
        Self(lines(text))
    }

    fn len(&self) -> usize {
        self.0.last().map_or(0, |&(_, end)| end)
    }

    pub fn offset(&self, position: &Value) -> usize {
        let line = position["line"].as_i64().unwrap_or(0);
        if line < 0 {
            return 0;
        }
        let character = position["character"].as_u64().unwrap_or(0) as usize;
        self.0.get(line as usize).map_or_else(
            || self.len(),
            |&(start, end)| start + character.min(end - start),
        )
    }

    pub fn position(&self, offset: usize) -> Value {
        let offset = offset.min(self.len());
        let line = self
            .0
            .partition_point(|&(start, _)| start <= offset)
            .saturating_sub(1);
        json!({"line":line,"character":offset-self.0[line].0})
    }

    pub fn range(&self, start: usize, length: usize) -> Value {
        json!({"start":self.position(start),"end":self.position(start.saturating_add(length))})
    }
}

pub fn offset(text: &str, position: &Value) -> usize {
    Lines::new(text).offset(position)
}

pub fn position(text: &str, offset: usize) -> Value {
    Lines::new(text).position(offset)
}

pub fn range(text: &str, start: usize, length: usize) -> Value {
    Lines::new(text).range(start, length)
}

pub(crate) fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, message)
}
