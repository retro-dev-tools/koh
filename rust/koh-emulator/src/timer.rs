use crate::{
    Interrupts,
    state::{StateReader, StateWriter},
};
use std::io;

#[derive(Clone, Default)]
pub struct Timer {
    counter: u16,
    pub tima: u8,
    pub tma: u8,
    pub tac: u8,
    reload_delay: i32,
    just_reloaded: bool,
    last_selected_bit: bool,
    last_div_apu_bit: bool,
}

impl Timer {
    pub fn div(&self) -> u8 {
        (self.counter >> 8) as u8
    }
    fn selected_bit(tac: u8) -> u32 {
        [9, 3, 5, 7][usize::from(tac & 3)]
    }
    fn increment_tima(&mut self) {
        if self.tima == 0xff {
            self.tima = 0;
            self.reload_delay = 4;
        } else {
            self.tima = self.tima.wrapping_add(1);
        }
    }
    pub fn div_apu_bit_high(&self) -> bool {
        self.last_div_apu_bit
    }
    pub fn tick_t(&mut self, interrupts: &mut Interrupts, double_speed: bool) -> bool {
        self.counter = self.counter.wrapping_add(1);
        let current_div = (self.counter >> if double_speed { 13 } else { 12 }) & 1 != 0;
        let edge = self.last_div_apu_bit && !current_div;
        self.last_div_apu_bit = current_div;
        self.just_reloaded = false;
        if self.reload_delay > 0 {
            self.reload_delay -= 1;
            if self.reload_delay == 0 {
                self.tima = self.tma;
                interrupts.raise(Interrupts::TIMER);
                self.just_reloaded = true;
            }
        }
        let bit = self.tac & 4 != 0 && self.counter >> Self::selected_bit(self.tac) & 1 != 0;
        if self.last_selected_bit && !bit {
            self.increment_tima();
        }
        self.last_selected_bit = bit;
        edge
    }
    fn fell_on_last_tick(&self, bit: u32) -> bool {
        self.counter >> bit & 1 == 0 && self.counter.wrapping_sub(1) >> bit & 1 != 0
    }
    pub fn write_tac(&mut self, value: u8) {
        let old = self.tac;
        self.tac = value & 7;
        let new = self.tac & 4 != 0 && self.counter >> Self::selected_bit(self.tac) & 1 != 0;
        if self.last_selected_bit && !new
            || self.tac & 4 != 0
                && self.counter != 0
                && self.fell_on_last_tick(Self::selected_bit(self.tac))
                && !(old & 4 != 0 && self.fell_on_last_tick(Self::selected_bit(old)))
        {
            self.increment_tima();
        }
        self.last_selected_bit = new;
    }
    pub fn write_tima(&mut self, value: u8) {
        if self.just_reloaded {
            return;
        }
        if self.reload_delay > 0 {
            self.reload_delay = 0;
        }
        self.tima = value;
    }
    pub fn write_tma(&mut self, value: u8) {
        self.tma = value;
        if self.just_reloaded {
            self.tima = value;
        }
    }
    pub fn write_div(&mut self) -> bool {
        self.counter = 0;
        if self.last_selected_bit {
            self.increment_tima();
        }
        self.last_selected_bit = false;
        let edge = self.last_div_apu_bit;
        self.last_div_apu_bit = false;
        edge
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_u16(self.counter);
        w.write_byte(self.tima);
        w.write_byte(self.tma);
        w.write_byte(self.tac);
        w.write_i32(self.reload_delay);
        w.write_bool(self.just_reloaded);
        w.write_bool(self.last_selected_bit);
        w.write_bool(self.last_div_apu_bit);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.counter = r.read_u16()?;
        self.tima = r.read_byte()?;
        self.tma = r.read_byte()?;
        self.tac = r.read_byte()?;
        self.reload_delay = r.read_i32()?;
        self.just_reloaded = r.read_bool()?;
        self.last_selected_bit = r.read_bool()?;
        self.last_div_apu_bit = r.read_bool()?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn ticks(timer: &mut Timer, count: usize, interrupts: &mut Interrupts) -> usize {
        (0..count)
            .filter(|_| timer.tick_t(interrupts, false))
            .count()
    }

    #[test]
    fn divider_and_tima_follow_their_selected_edges() {
        let mut timer = Timer::default();
        let mut irq = Interrupts::default();
        timer.write_tac(5);
        ticks(&mut timer, 255, &mut irq);
        assert_eq!(timer.div(), 0);
        assert_eq!(timer.tima, 15);
        ticks(&mut timer, 1, &mut irq);
        assert_eq!(timer.div(), 1);
        assert_eq!(timer.tima, 16);
    }

    #[test]
    fn overflow_waits_four_ticks_before_reload_and_interrupt() {
        let mut timer = Timer::default();
        let mut irq = Interrupts::default();
        timer.write_tac(5);
        timer.write_tma(0x42);
        timer.write_tima(0xff);
        ticks(&mut timer, 16, &mut irq);
        assert_eq!(timer.tima, 0);
        assert_eq!(irq.iflag, 0);
        ticks(&mut timer, 3, &mut irq);
        assert_eq!(timer.tima, 0);
        ticks(&mut timer, 1, &mut irq);
        assert_eq!(timer.tima, 0x42);
        assert_eq!(irq.iflag, Interrupts::TIMER);
    }

    #[test]
    fn div_reset_clocks_apu_only_on_a_falling_tap_edge() {
        let mut timer = Timer::default();
        let mut irq = Interrupts::default();
        assert_eq!(ticks(&mut timer, 4096, &mut irq), 0);
        assert!(timer.write_div());
        assert!(!timer.write_div());
        assert_eq!(ticks(&mut timer, 8192, &mut irq), 1);
    }

    #[test]
    fn tima_write_cancels_pending_reload_but_not_the_reload_cycle() {
        let mut timer = Timer::default();
        let mut irq = Interrupts::default();
        timer.write_tac(5);
        timer.write_tma(0x42);
        timer.write_tima(0xff);
        ticks(&mut timer, 16, &mut irq);
        timer.write_tima(0x23);
        ticks(&mut timer, 4, &mut irq);
        assert_eq!(timer.tima, 0x23);
        assert_eq!(irq.iflag, 0);
        timer.write_tima(0xff);
        ticks(&mut timer, 12, &mut irq);
        ticks(&mut timer, 4, &mut irq);
        timer.write_tima(0x99);
        assert_eq!(timer.tima, 0x42);
        timer.write_tma(0x67);
        assert_eq!(timer.tima, 0x67);
    }

    #[test]
    fn all_tac_frequencies_and_disable_edges_are_preserved() {
        for (setting, period) in [(0, 1024), (1, 16), (2, 64), (3, 256)] {
            let mut t = Timer::default();
            let mut irq = Interrupts::default();
            t.write_tac(setting | 4);
            ticks(&mut t, period - 1, &mut irq);
            assert_eq!(t.tima, 0);
            t.write_tac(setting);
            assert_eq!(t.tima, 1);
        }
    }

    #[test]
    fn double_speed_moves_apu_tap_without_changing_tima_clock() {
        let mut t = Timer::default();
        let mut irq = Interrupts::default();
        for _ in 0..8192 {
            assert!(!t.tick_t(&mut irq, true));
        }
        assert!(t.div_apu_bit_high());
        assert!(t.write_div());
    }

    #[test]
    fn state_preserves_the_delayed_reload_edge() {
        let mut t = Timer::default();
        let mut irq = Interrupts::default();
        t.write_tac(5);
        t.write_tima(0xff);
        t.write_tma(0x42);
        ticks(&mut t, 17, &mut irq);
        let mut w = StateWriter::default();
        t.write_state(&mut w);
        assert_eq!(w.bytes, [17, 0, 0, 0x42, 5, 3, 0, 0, 0, 0, 0, 0]);
        let mut restored = Timer::default();
        restored
            .read_state(&mut StateReader::new(&w.bytes))
            .unwrap();
        ticks(&mut restored, 3, &mut irq);
        assert_eq!(restored.tima, 0x42);
        assert_eq!(irq.iflag, 4);
    }
}
