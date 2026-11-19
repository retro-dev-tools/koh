use std::io;

#[derive(Clone)]
pub struct StateReader<'a> {
    data: &'a [u8],
}

impl<'a> StateReader<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Self { data }
    }

    pub fn read_bytes(&mut self, count: usize) -> io::Result<&'a [u8]> {
        let bytes = self.data.get(..count).ok_or_else(|| {
            io::Error::new(io::ErrorKind::UnexpectedEof, "truncated emulator state")
        })?;
        self.data = &self.data[count..];
        Ok(bytes)
    }

    pub fn remaining(&self) -> usize {
        self.data.len()
    }

    pub fn read_byte(&mut self) -> io::Result<u8> {
        Ok(self.read_bytes(1)?[0])
    }

    pub fn read_sbyte(&mut self) -> io::Result<i8> {
        Ok(self.read_byte()? as i8)
    }

    pub fn read_bool(&mut self) -> io::Result<bool> {
        Ok(self.read_byte()? != 0)
    }
}

macro_rules! state_numbers {
    ($(($read:ident, $write:ident, $type:ty)),* $(,)?) => {
        impl StateReader<'_> {
            $(pub fn $read(&mut self) -> io::Result<$type> {
                Ok(<$type>::from_le_bytes(self.read_bytes(size_of::<$type>())?.try_into().unwrap()))
            })*
        }
        impl StateWriter {
            $(pub fn $write(&mut self, value: $type) {
                self.bytes.extend_from_slice(&value.to_le_bytes());
            })*
        }
    };
}

state_numbers! {
    (read_u16, write_u16, u16),
    (read_i32, write_i32, i32),
    (read_u32, write_u32, u32),
    (read_u64, write_u64, u64),
    (read_i64, write_i64, i64),
}

#[derive(Clone, Default)]
pub struct StateWriter {
    pub bytes: Vec<u8>,
}

impl StateWriter {
    pub fn write_byte(&mut self, value: u8) {
        self.bytes.push(value);
    }

    pub fn write_sbyte(&mut self, value: i8) {
        self.write_byte(value as u8);
    }

    pub fn write_bool(&mut self, value: bool) {
        self.write_byte(value.into());
    }

    pub fn write_bytes(&mut self, value: &[u8]) {
        self.bytes.extend_from_slice(value);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_little_endian_values_and_advances() {
        let mut reader = StateReader::new(&[0x34, 0x12, 0xcd, 0xab]);
        assert_eq!(reader.read_u16().unwrap(), 0x1234);
        assert_eq!(reader.read_u16().unwrap(), 0xabcd);
    }

    #[test]
    fn truncated_values_are_rejected() {
        for data in [b"".as_slice(), b"\x12".as_slice()] {
            assert_eq!(
                StateReader::new(data).read_u16().unwrap_err().kind(),
                io::ErrorKind::UnexpectedEof
            );
        }
    }

    #[test]
    fn writes_the_existing_binary_field_order() {
        let mut writer = StateWriter::default();
        writer.write_u16(0x1234);
        writer.write_u16(0xabcd);
        assert_eq!(writer.bytes, [0x34, 0x12, 0xcd, 0xab]);
    }

    #[test]
    fn signed_values_and_flags_match_binary_writer_bytes() {
        let bytes = [
            0xab, 0xff, 0x34, 0x12, 0xfe, 0xff, 0xff, 0xff, 0xef, 0xcd, 0xab, 0x89, 0xef, 0xcd,
            0xab, 0x89, 0x67, 0x45, 0x23, 0x01, 0xfd, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 1,
            0, 0, 0xff,
        ];
        let mut w = StateWriter::default();
        w.write_byte(0xab);
        w.write_sbyte(-1);
        w.write_u16(0x1234);
        w.write_i32(-2);
        w.write_u32(0x89abcdef);
        w.write_u64(0x0123456789abcdef);
        w.write_i64(-3);
        w.write_bool(true);
        w.write_bool(false);
        w.write_bytes(&[0, 0xff]);
        assert_eq!(w.bytes, bytes);
        let mut r = StateReader::new(&bytes);
        assert_eq!(r.read_byte().unwrap(), 0xab);
        assert_eq!(r.read_sbyte().unwrap(), -1);
        assert_eq!(r.read_u16().unwrap(), 0x1234);
        assert_eq!(r.read_i32().unwrap(), -2);
        assert_eq!(r.read_u32().unwrap(), 0x89abcdef);
        assert_eq!(r.read_u64().unwrap(), 0x0123456789abcdef);
        assert_eq!(r.read_i64().unwrap(), -3);
        assert!(r.read_bool().unwrap());
        assert!(!r.read_bool().unwrap());
        assert_eq!(r.read_bytes(2).unwrap(), [0, 0xff]);
        assert_eq!(r.remaining(), 0);
    }

    #[test]
    fn oversized_read_does_not_consume_remaining_bytes() {
        let mut reader = StateReader::new(&[42]);
        assert_eq!(
            reader.read_bytes(usize::MAX).unwrap_err().kind(),
            io::ErrorKind::UnexpectedEof
        );
        assert_eq!(reader.read_byte().unwrap(), 42);
    }
}
