use serde::{
    Deserialize, Deserializer,
    de::{self, MapAccess, Visitor},
};
use serde_json::{Map, Value};
use std::{
    collections::HashSet,
    fmt,
    io::{self, Read, Write},
};

pub fn command_line(input: &mut impl Read) -> io::Result<Option<String>> {
    let mut bytes = Vec::new();
    let mut units = 0;
    loop {
        let mut first = [0];
        if input.read(&mut first)? == 0 {
            return if bytes.is_empty() {
                Ok(None)
            } else {
                Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "incomplete command at EOF",
                ))
            };
        }
        if first[0] == b'\n' {
            return String::from_utf8(bytes)
                .map(Some)
                .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e));
        }
        let count = match first[0] {
            0..=0x7f => 1,
            0xc2..=0xdf => 2,
            0xe0..=0xef => 3,
            0xf0..=0xf4 => 4,
            _ => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "invalid UTF-8 command",
                ));
            }
        };
        let mut encoded = [0; 4];
        encoded[0] = first[0];
        input.read_exact(&mut encoded[1..count])?;
        let character = std::str::from_utf8(&encoded[..count])
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
        units += if count == 4 { 2 } else { 1 };
        if units > 16384 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "command exceeded 16384 characters",
            ));
        }
        bytes.extend_from_slice(character.as_bytes());
    }
}

struct Request(Value);
impl<'de> Deserialize<'de> for Request {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct ObjectVisitor;
        impl<'de> Visitor<'de> for ObjectVisitor {
            type Value = Request;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("a command object")
            }
            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Request, A::Error> {
                let mut names = HashSet::new();
                let mut result = Map::new();
                while let Some(name) = map.next_key::<String>()? {
                    if !names.insert(name.clone()) {
                        return Err(de::Error::custom("Duplicate command property"));
                    }
                    result.insert(name, map.next_value()?);
                }
                Ok(Request(Value::Object(result)))
            }
        }
        deserializer.deserialize_map(ObjectVisitor)
    }
}
fn depth(value: &Value, n: usize, limit: usize) -> bool {
    match value {
        Value::Object(map) => n <= limit && map.values().all(|v| depth(v, n + 1, limit)),
        Value::Array(array) => n <= limit && array.iter().all(|v| depth(v, n + 1, limit)),
        _ => true,
    }
}
pub fn parse_request(line: &str) -> Result<Value, String> {
    let request: Request = serde_json::from_str(line).map_err(|e| e.to_string())?;
    if !depth(&request.0, 1, 8) {
        return Err("JSON depth exceeded 8".into());
    }
    Ok(request.0)
}
#[derive(Debug, PartialEq)]
pub enum Frame {
    Request(Value),
    InvalidJson(String),
}
pub fn read_frame(input: &mut impl Read) -> io::Result<Option<Frame>> {
    let mut header = Vec::new();
    while !header.ends_with(b"\r\n\r\n") {
        if header.len() == 1024 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "DAP header exceeded 1 KiB",
            ));
        }
        let mut byte = [0];
        if input.read(&mut byte)? == 0 {
            return Ok(None);
        }
        header.push(byte[0]);
    }
    let mut length = None;
    for line in String::from_utf8_lossy(&header).split("\r\n") {
        if let Some((name, value)) = line.split_once(':')
            && name.trim().eq_ignore_ascii_case("Content-Length")
            && let Ok(n) = value.trim().parse::<i32>()
        {
            length = Some(n);
        }
    }
    let length = length.filter(|&n| n >= 0).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            "DAP frame missing Content-Length",
        )
    })? as usize;
    let mut body = Vec::new();
    body.try_reserve_exact(length)
        .map_err(|e| io::Error::other(e.to_string()))?;
    body.resize(length, 0);
    if let Err(e) = input.read_exact(&mut body) {
        if e.kind() == io::ErrorKind::UnexpectedEof {
            return Ok(None);
        }
        return Err(e);
    }
    Ok(Some(match serde_json::from_slice(&body) {
        Ok(value) if depth(&value, 1, 64) => Frame::Request(value),
        Ok(_) => Frame::InvalidJson("JSON depth exceeded 64".into()),
        Err(error) => Frame::InvalidJson(error.to_string()),
    }))
}
pub fn write_frame(output: &mut impl Write, value: &Value) -> io::Result<()> {
    let body = serde_json::to_vec(value)?;
    write!(output, "Content-Length: {}\r\n\r\n", body.len())?;
    output.write_all(&body)?;
    output.flush()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn utf16_budget_matches_astral_characters() {
        let input = format!("{}\n", "😀".repeat(8192));
        assert!(command_line(&mut input.as_bytes()).unwrap().is_some());
        assert!(command_line(&mut format!("{}x\n", "😀".repeat(8192)).as_bytes()).is_err());
    }
    #[test]
    fn duplicates_depth_and_trailing_input_rejected() {
        assert!(parse_request("{\"id\":1,\"id\":2}").is_err());
        assert!(parse_request("{} {}").is_err());
        assert!(parse_request(&format!("{}0{}", "[".repeat(9), "]".repeat(9))).is_err());
    }
    #[test]
    fn dap_frame_roundtrip_and_limits() {
        let mut nested = Value::Null;
        for _ in 0..64 {
            nested = serde_json::json!([nested]);
        }
        assert!(depth(&nested, 1, 64));
        nested = serde_json::json!([nested]);
        assert!(!depth(&nested, 1, 64));
        let value = serde_json::json!({"seq":1,"command":"threads"});
        let mut bytes = vec![];
        write_frame(&mut bytes, &value).unwrap();
        assert_eq!(
            read_frame(&mut bytes.as_slice()).unwrap(),
            Some(Frame::Request(value))
        );
        assert!(read_frame(&mut vec![b'x'; 1024].as_slice()).is_err());
        assert!(
            read_frame(&mut b"Content-Length: 2\r\n\r\n{".as_slice())
                .unwrap()
                .is_none()
        );
    }
}
