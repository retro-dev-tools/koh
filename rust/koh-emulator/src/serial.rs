#[cfg(test)]
mod tests {
    use super::*;
    use crate::Interrupts;
    #[test]
    fn serial_capture_and_4096_cycle_irq() {
        let mut s = Serial::default();
        let mut irq = Interrupts::default();
        s.sb = b'H';
        s.write_sc(0x81);
        assert_eq!(s.read_buffer_as_string(), "H");
        for _ in 0..4095 {
            s.tick_t(&mut irq);
        }
        assert!(s.is_transferring());
        assert_eq!(irq.iflag, 0);
        s.tick_t(&mut irq);
        assert!(!s.is_transferring());
        assert_eq!(s.sb, 255);
        assert_eq!(s.read_sc() & 128, 0);
        assert_eq!(irq.iflag, Interrupts::SERIAL);
    }
    #[test]
    fn external_clock_does_not_start_and_peer_exchange_is_restorable() {
        struct Peer;
        impl SerialLink for Peer {
            fn exchange_byte(&mut self, sent: u8) -> u8 {
                assert_eq!(sent, 0x5a);
                0xa5
            }
        }
        let mut s = Serial::default();
        let mut irq = Interrupts::default();
        s.sb = 0x5a;
        s.write_sc(0x80);
        for _ in 0..4096 {
            s.tick_t(&mut irq);
        }
        assert!(!s.is_transferring());
        assert_eq!(irq.iflag, 0);
        s.link = Some(Box::new(Peer));
        s.write_sc(0x81);
        for _ in 0..777 {
            s.tick_t(&mut irq);
        }
        let mut w = StateWriter::default();
        s.write_state(&mut w);
        let mut restored = Serial::default();
        restored
            .read_state(&mut StateReader::new(&w.bytes))
            .unwrap();
        for _ in 777..4096 {
            restored.tick_t(&mut irq);
        }
        assert_eq!(restored.sb, 0xa5);
        assert_eq!(irq.iflag, Interrupts::SERIAL);
        assert_eq!(restored.read_buffer_as_string(), "Z");
        restored.clear_buffer();
        assert!(restored.buffer.is_empty());
        for n in 0..w.bytes.len() {
            assert!(
                Serial::default()
                    .read_state(&mut StateReader::new(&w.bytes[..n]))
                    .is_err()
            );
        }
    }
}
use crate::{
    Interrupts,
    state::{StateReader, StateWriter},
};
use std::io;

pub trait SerialLink: Send {
    fn exchange_byte(&mut self, sent: u8) -> u8;
}
pub struct Serial {
    pub sb: u8,
    pub sc: u8,
    pub buffer: Vec<u8>,
    pub link: Option<Box<dyn SerialLink>>,
    transferring: bool,
    bits_remaining: i32,
    countdown: i32,
    incoming: u8,
}
impl Default for Serial {
    fn default() -> Self {
        Self {
            sb: 0,
            sc: 0,
            buffer: Vec::new(),
            link: None,
            transferring: false,
            bits_remaining: 0,
            countdown: 0,
            incoming: 255,
        }
    }
}
impl Serial {
    pub fn read_sc(&self) -> u8 {
        self.sc | 0x7e
    }
    pub fn is_transferring(&self) -> bool {
        self.transferring
    }
    pub fn write_sc(&mut self, value: u8) {
        self.sc = value;
        if value & 0x81 == 0x81 {
            self.buffer.push(self.sb);
            self.incoming = self
                .link
                .as_mut()
                .map_or(255, |link| link.exchange_byte(self.sb));
            self.transferring = true;
            self.bits_remaining = 8;
            self.countdown = 512;
        }
    }
    pub fn tick_t(&mut self, irq: &mut Interrupts) {
        if !self.transferring {
            return;
        }
        self.countdown -= 1;
        if self.countdown > 0 {
            return;
        }
        self.countdown = 512;
        self.bits_remaining -= 1;
        self.sb = (self.sb << 1) | ((self.incoming >> self.bits_remaining) & 1);
        if self.bits_remaining == 0 {
            self.transferring = false;
            self.sc &= 0x7f;
            irq.raise(Interrupts::SERIAL);
        }
    }
    pub fn read_buffer_as_string(&self) -> String {
        self.buffer
            .iter()
            .map(|&b| if b < 128 { b as char } else { '?' })
            .collect()
    }
    pub fn clear_buffer(&mut self) {
        self.buffer.clear();
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_bytes(&[self.sb, self.sc]);
        w.write_bool(self.transferring);
        w.write_i32(self.bits_remaining);
        w.write_i32(self.countdown);
        w.write_byte(self.incoming);
        w.write_i32(self.buffer.len() as i32);
        w.write_bytes(&self.buffer);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.sb = r.read_byte()?;
        self.sc = r.read_byte()?;
        self.transferring = r.read_bool()?;
        self.bits_remaining = r.read_i32()?;
        self.countdown = r.read_i32()?;
        self.incoming = r.read_byte()?;
        let count = r.read_i32()?;
        if count < 0
            || !(0..=8).contains(&self.bits_remaining)
            || !(0..=512).contains(&self.countdown)
            || (self.transferring && (self.bits_remaining == 0 || self.countdown == 0))
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid serial state",
            ));
        }
        self.buffer = r.read_bytes(count as usize)?.to_vec();
        Ok(())
    }
}
