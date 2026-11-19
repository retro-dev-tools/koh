use crate::{
    HardwareMode,
    state::{StateReader, StateWriter},
};
use std::io;

#[derive(Clone)]
pub struct SampleBuffer {
    buffer: [i16; 8192],
    write_index: usize,
    read_index: usize,
}

impl Default for SampleBuffer {
    fn default() -> Self {
        Self {
            buffer: [0; 8192],
            write_index: 0,
            read_index: 0,
        }
    }
}

impl SampleBuffer {
    pub fn available(&self) -> usize {
        (self.write_index + 8192 - self.read_index) % 8192
    }
    pub fn push(&mut self, sample: i16) {
        self.buffer[self.write_index] = sample;
        self.write_index = (self.write_index + 1) % 8192;
        if self.write_index == self.read_index {
            self.read_index = (self.read_index + 1) % 8192;
        }
    }
    pub fn drain(&mut self, destination: &mut [i16]) -> usize {
        let count = destination.len().min(self.available());
        for sample in &mut destination[..count] {
            *sample = self.buffer[self.read_index];
            self.read_index = (self.read_index + 1) % 8192;
        }
        count
    }
}

#[derive(Clone)]
pub struct LengthCounter {
    pub counter: i32,
    pub enabled: bool,
    pub max_length: i32,
}

impl LengthCounter {
    pub fn new(max_length: i32) -> Self {
        Self {
            counter: 0,
            enabled: false,
            max_length,
        }
    }
    pub fn tick(&mut self, enabled: &mut bool) {
        if !self.enabled || self.counter == 0 {
            return;
        }
        self.counter -= 1;
        if self.counter == 0 {
            *enabled = false;
        }
    }
    fn trigger(&mut self, control: u8, skips: bool) {
        self.enabled = control & 0x40 != 0;
        if self.counter == 0 {
            self.counter = self.max_length;
            if self.enabled && skips {
                self.counter -= 1;
            }
        }
    }
    fn enable_write(&mut self, control: u8, skips: bool, channel_enabled: &mut bool) {
        let enabled = control & 0x40 != 0;
        if !self.enabled && enabled && skips && self.counter > 0 {
            self.counter -= 1;
            if self.counter == 0 && control & 0x80 == 0 {
                *channel_enabled = false;
            }
        }
        self.enabled = enabled;
    }
}

#[derive(Clone, Default)]
pub struct VolumeEnvelope {
    pub volume: i32,
    pub increase_direction: bool,
    pub period_reload: i32,
    period: i32,
}

impl VolumeEnvelope {
    pub fn trigger(&mut self, control: u8) {
        self.volume = (control >> 4) as i32;
        self.increase_direction = control & 8 != 0;
        self.period_reload = (control & 7) as i32;
        self.period = self.period_reload;
    }
    pub fn tick(&mut self) {
        if self.period_reload == 0 {
            return;
        }
        self.period -= 1;
        if self.period > 0 {
            return;
        }
        self.period = self.period_reload;
        if self.increase_direction && self.volume < 15 {
            self.volume += 1;
        } else if !self.increase_direction && self.volume > 0 {
            self.volume -= 1;
        }
    }
}

#[derive(Clone, Default)]
pub struct FrequencySweep {
    pub shadow_frequency: i32,
    pub enabled: bool,
    period: i32,
    last_increase_direction: bool,
    negated_since_trigger: bool,
}

impl FrequencySweep {
    pub fn trigger(&mut self, nr10: u8, frequency: i32, enabled: &mut bool) {
        self.shadow_frequency = frequency;
        let period = (nr10 >> 4) & 7;
        self.enabled = period != 0 || nr10 & 7 != 0;
        self.period = if period == 0 { 8 } else { period as i32 };
        self.negated_since_trigger = false;
        self.last_increase_direction = nr10 & 8 == 0;
        if nr10 & 7 != 0 {
            self.calculate(nr10, enabled);
        }
    }
    pub fn on_nr10_write(&mut self, nr10: u8, enabled: &mut bool) {
        let increase = nr10 & 8 == 0;
        if increase && !self.last_increase_direction && self.negated_since_trigger {
            *enabled = false;
        }
        self.last_increase_direction = increase;
    }
    pub fn tick(&mut self, nr10: u8, enabled: &mut bool) -> Option<i32> {
        if !self.enabled {
            return None;
        }
        self.period -= 1;
        if self.period > 0 {
            return None;
        }
        let period = (nr10 >> 4) & 7;
        self.period = if period == 0 { 8 } else { period as i32 };
        if period == 0 {
            return None;
        }
        let frequency = self.calculate(nr10, enabled);
        if frequency <= 2047 && nr10 & 7 > 0 {
            self.shadow_frequency = frequency;
            self.calculate(nr10, enabled);
            return Some(frequency);
        }
        None
    }
    fn calculate(&mut self, nr10: u8, enabled: &mut bool) -> i32 {
        let delta = self.shadow_frequency >> (nr10 & 7);
        self.last_increase_direction = nr10 & 8 == 0;
        let frequency = if self.last_increase_direction {
            self.shadow_frequency + delta
        } else {
            self.negated_since_trigger = true;
            self.shadow_frequency - delta
        };
        if frequency > 2047 {
            self.enabled = false;
            *enabled = false;
        }
        frequency
    }
}

#[derive(Clone, Default)]
pub struct FrameSequencer {
    pub step: i32,
    pub skip_next: bool,
}

impl FrameSequencer {
    pub fn advance(&mut self) -> (bool, bool, bool) {
        if self.skip_next {
            self.skip_next = false;
            return (false, false, false);
        }
        self.step = (self.step + 1) & 7;
        (
            self.step & 1 == 0,
            self.step == 2 || self.step == 6,
            self.step == 7,
        )
    }
    pub fn reset(&mut self) {
        self.step = 0;
    }
}

#[derive(Clone)]
pub struct SquareChannel {
    pub length: LengthCounter,
    pub envelope: VolumeEnvelope,
    pub sweep: Option<FrequencySweep>,
    pub enabled: bool,
    pub frequency: i32,
    pub duty_step: i32,
    pub duty_pattern: i32,
    freq_cycle_counter: i32,
}

impl SquareChannel {
    pub fn new(has_sweep: bool) -> Self {
        Self {
            length: LengthCounter::new(64),
            envelope: VolumeEnvelope::default(),
            sweep: has_sweep.then(FrequencySweep::default),
            enabled: false,
            frequency: 0,
            duty_step: 0,
            duty_pattern: 0,
            freq_cycle_counter: 0,
        }
    }
    pub fn tick_t(&mut self) {
        if !self.enabled {
            return;
        }
        self.freq_cycle_counter -= 1;
        if self.freq_cycle_counter > 0 {
            return;
        }
        self.freq_cycle_counter = (2048 - self.frequency) * 4;
        self.duty_step = (self.duty_step + 1) & 7;
    }
    pub fn tick_length(&mut self) {
        self.length.tick(&mut self.enabled);
    }
    pub fn tick_envelope(&mut self) {
        self.envelope.tick();
    }
    pub fn tick_sweep(&mut self, nr10: u8) -> Option<i32> {
        let frequency = self.sweep.as_mut()?.tick(nr10, &mut self.enabled);
        if let Some(f) = frequency {
            self.frequency = f;
        }
        frequency
    }
    pub fn output(&self) -> i32 {
        const DUTY: [[i32; 8]; 4] = [
            [0, 0, 0, 0, 0, 0, 0, 1],
            [1, 0, 0, 0, 0, 0, 0, 1],
            [1, 0, 0, 0, 0, 1, 1, 1],
            [0, 1, 1, 1, 1, 1, 1, 0],
        ];
        if !self.enabled {
            return 0;
        }
        DUTY[self.duty_pattern as usize][self.duty_step as usize] * self.envelope.volume
    }
    pub fn trigger(&mut self, nrx0: u8, nrx1: u8, nrx2: u8, nrx3: u8, nrx4: u8, skips: bool) {
        self.enabled = true;
        self.length.trigger(nrx4, skips);
        self.frequency = (((nrx4 & 7) as i32) << 8) | nrx3 as i32;
        self.envelope.trigger(nrx2);
        self.duty_pattern = ((nrx1 >> 6) & 3) as i32;
        self.freq_cycle_counter = (2048 - self.frequency) * 4;
        if let Some(sweep) = &mut self.sweep {
            sweep.trigger(nrx0, self.frequency, &mut self.enabled);
        }
        if nrx2 & 0xf8 == 0 {
            self.enabled = false;
        }
    }
}

#[derive(Clone)]
pub struct WaveChannel {
    pub length: LengthCounter,
    pub dac_enabled: bool,
    pub enabled: bool,
    pub frequency: i32,
    pub volume_shift: i32,
    pub wave_pattern: [u8; 16],
    is_cgb: bool,
    wave_index: i32,
    freq_cycle_counter: i32,
    sample_buffer: i32,
    just_read_countdown: i32,
}

impl WaveChannel {
    pub fn new(is_cgb: bool) -> Self {
        Self {
            length: LengthCounter::new(256),
            dac_enabled: false,
            enabled: false,
            frequency: 0,
            volume_shift: 0,
            wave_pattern: [0; 16],
            is_cgb,
            wave_index: 0,
            freq_cycle_counter: 0,
            sample_buffer: 0,
            just_read_countdown: 0,
        }
    }
    pub fn current_byte_position(&self) -> usize {
        (self.wave_index / 2) as usize
    }
    pub fn just_read(&self) -> bool {
        self.just_read_countdown > 0
    }
    pub fn reset_on_power_off(&mut self) {
        self.wave_index = 0;
        self.sample_buffer = 0;
        self.just_read_countdown = 0;
    }
    pub fn tick_t(&mut self) {
        if self.just_read_countdown > 0 {
            self.just_read_countdown -= 1;
        }
        if !self.enabled {
            return;
        }
        self.freq_cycle_counter -= 1;
        if self.freq_cycle_counter > 0 {
            return;
        }
        self.freq_cycle_counter = (2048 - self.frequency) * 2;
        self.wave_index = (self.wave_index + 1) & 31;
        let byte = self.wave_pattern[self.current_byte_position()];
        self.sample_buffer = if self.wave_index & 1 == 0 {
            byte >> 4
        } else {
            byte & 15
        } as i32;
        self.just_read_countdown = 1;
    }
    pub fn tick_length(&mut self) {
        self.length.tick(&mut self.enabled);
    }
    pub fn output(&self) -> i32 {
        if !self.enabled || !self.dac_enabled || self.volume_shift == 0 {
            return 0;
        }
        self.sample_buffer >> (self.volume_shift - 1)
    }
    pub fn trigger(&mut self, nr30: u8, _nr31: u8, nr32: u8, nr33: u8, nr34: u8, skips: bool) {
        self.dac_enabled = nr30 & 0x80 != 0;
        self.length.trigger(nr34, skips);
        self.volume_shift = ((nr32 >> 5) & 3) as i32;
        self.frequency = (((nr34 & 7) as i32) << 8) | nr33 as i32;
        if !self.is_cgb && self.enabled && self.just_read() {
            let pos = self.current_byte_position();
            if pos < 4 {
                self.wave_pattern[0] = self.wave_pattern[pos];
            } else {
                let aligned = pos & !3;
                self.wave_pattern.copy_within(aligned..aligned + 4, 0);
            }
        }
        self.wave_index = 0;
        self.freq_cycle_counter = (2048 - self.frequency) * 2;
        self.enabled = self.dac_enabled;
    }
}

#[derive(Clone)]
pub struct NoiseChannel {
    pub length: LengthCounter,
    pub envelope: VolumeEnvelope,
    pub enabled: bool,
    pub shift_register: i32,
    pub clock_shift: i32,
    pub width_mode: bool,
    pub divisor_code: i32,
    freq_cycle_counter: i32,
}

impl Default for NoiseChannel {
    fn default() -> Self {
        Self {
            length: LengthCounter::new(64),
            envelope: VolumeEnvelope::default(),
            enabled: false,
            shift_register: 0x7fff,
            clock_shift: 0,
            width_mode: false,
            divisor_code: 0,
            freq_cycle_counter: 0,
        }
    }
}

impl NoiseChannel {
    fn period(&self) -> i32 {
        [8, 16, 32, 48, 64, 80, 96, 112][self.divisor_code as usize] << self.clock_shift
    }
    pub fn tick_t(&mut self) {
        if !self.enabled {
            return;
        }
        self.freq_cycle_counter -= 1;
        if self.freq_cycle_counter > 0 {
            return;
        }
        self.freq_cycle_counter = self.period();
        let xor = (self.shift_register & 1) ^ ((self.shift_register >> 1) & 1);
        self.shift_register = (self.shift_register >> 1) | (xor << 14);
        if self.width_mode {
            self.shift_register = (self.shift_register & !(1 << 6)) | (xor << 6);
        }
    }
    pub fn tick_length(&mut self) {
        self.length.tick(&mut self.enabled);
    }
    pub fn tick_envelope(&mut self) {
        self.envelope.tick();
    }
    pub fn output(&self) -> i32 {
        if !self.enabled {
            return 0;
        }
        (!self.shift_register & 1) * self.envelope.volume
    }
    pub fn trigger(&mut self, _nr41: u8, nr42: u8, nr43: u8, nr44: u8, skips: bool) {
        self.length.trigger(nr44, skips);
        self.envelope.trigger(nr42);
        self.clock_shift = ((nr43 >> 4) & 15) as i32;
        self.width_mode = nr43 & 8 != 0;
        self.divisor_code = (nr43 & 7) as i32;
        self.shift_register = 0x7fff;
        self.freq_cycle_counter = self.period();
        self.enabled = nr42 & 0xf8 != 0;
    }
}

#[derive(Clone)]
pub struct Apu {
    pub frame_sequencer: FrameSequencer,
    pub ch1: SquareChannel,
    pub ch2: SquareChannel,
    pub ch3: WaveChannel,
    pub ch4: NoiseChannel,
    pub enabled: bool,
    nr: [u8; 0x30],
    sample_accum: i32,
    pub sample_buffer: SampleBuffer,
    is_cgb: bool,
}

impl Apu {
    pub fn new(mode: HardwareMode) -> Self {
        let is_cgb = mode == HardwareMode::Cgb;
        Self {
            frame_sequencer: FrameSequencer::default(),
            ch1: SquareChannel::new(true),
            ch2: SquareChannel::new(false),
            ch3: WaveChannel::new(is_cgb),
            ch4: NoiseChannel::default(),
            enabled: false,
            nr: [0; 0x30],
            sample_accum: 0,
            sample_buffer: SampleBuffer::default(),
            is_cgb,
        }
    }
    pub fn tick_t(&mut self) {
        if !self.enabled {
            return;
        }
        self.ch1.tick_t();
        self.ch2.tick_t();
        self.ch3.tick_t();
        self.ch4.tick_t();
        self.sample_accum += 44100;
        if self.sample_accum >= 4194304 {
            self.sample_accum -= 4194304;
            self.sample_buffer.push(
                ((self.ch1.output() + self.ch2.output() + self.ch3.output() + self.ch4.output())
                    * 800) as i16,
            );
        }
    }
    pub fn advance_frame_sequencer(&mut self) {
        let (length, sweep, envelope) = self.frame_sequencer.advance();
        if !self.enabled {
            return;
        }
        if length {
            self.ch1.tick_length();
            self.ch2.tick_length();
            self.ch3.tick_length();
            self.ch4.tick_length();
        }
        if sweep {
            if let Some(f) = self.ch1.tick_sweep(self.nr[0]) {
                self.nr[3] = f as u8;
                self.nr[4] = (self.nr[4] & !7) | ((f >> 8) as u8 & 7);
            }
        }
        if envelope {
            self.ch1.tick_envelope();
            self.ch2.tick_envelope();
            self.ch4.tick_envelope();
        }
    }
    pub fn read(&self, address: u16) -> u8 {
        if (0xff30..=0xff3f).contains(&address) {
            if self.ch3.enabled {
                return if self.is_cgb || self.ch3.just_read() {
                    self.ch3.wave_pattern[self.ch3.current_byte_position()]
                } else {
                    0xff
                };
            }
            return self.ch3.wave_pattern[(address - 0xff30) as usize];
        }
        match address {
            0xff10 => self.nr[0] | 0x80,
            0xff11 => self.nr[1] | 0x3f,
            0xff12 => self.nr[2],
            0xff14 => self.nr[4] | 0xbf,
            0xff16 => self.nr[6] | 0x3f,
            0xff17 => self.nr[7],
            0xff19 => self.nr[9] | 0xbf,
            0xff1a => self.nr[10] | 0x7f,
            0xff1c => self.nr[12] | 0x9f,
            0xff1e => self.nr[14] | 0xbf,
            0xff21 => self.nr[17],
            0xff22 => self.nr[18],
            0xff23 => self.nr[19] | 0xbf,
            0xff24 => self.nr[20],
            0xff25 => self.nr[21],
            0xff26 => {
                0x70 | ((self.enabled as u8) << 7)
                    | self.ch1.enabled as u8
                    | ((self.ch2.enabled as u8) << 1)
                    | ((self.ch3.enabled as u8) << 2)
                    | ((self.ch4.enabled as u8) << 3)
            }
            _ => 0xff,
        }
    }
    pub fn write(&mut self, address: u16, value: u8, div_apu_bit_high: bool) {
        if (0xff30..=0xff3f).contains(&address) {
            if self.ch3.enabled {
                if self.is_cgb || self.ch3.just_read() {
                    let pos = self.ch3.current_byte_position();
                    self.ch3.wave_pattern[pos] = value;
                }
            } else {
                self.ch3.wave_pattern[(address - 0xff30) as usize] = value;
            }
            return;
        }
        if !self.enabled && (0xff10..=0xff25).contains(&address) {
            match address {
                0xff11 => self.ch1.length.counter = 64 - (value & 0x3f) as i32,
                0xff16 => self.ch2.length.counter = 64 - (value & 0x3f) as i32,
                0xff1b => self.ch3.length.counter = 256 - value as i32,
                0xff20 => self.ch4.length.counter = 64 - (value & 0x3f) as i32,
                _ => (),
            }
            return;
        }
        if !(0xff10..=0xff3f).contains(&address) {
            return;
        }
        self.nr[(address - 0xff10) as usize] = value;
        let skips = self.frame_sequencer.step & 1 == 0;
        match address {
            0xff10 => self
                .ch1
                .sweep
                .as_mut()
                .unwrap()
                .on_nr10_write(value, &mut self.ch1.enabled),
            0xff11 => self.ch1.length.counter = 64 - (value & 0x3f) as i32,
            0xff12 => {
                self.ch1.envelope.trigger(value);
                if value & 0xf8 == 0 {
                    self.ch1.enabled = false;
                }
            }
            0xff13 => self.ch1.frequency = (self.ch1.frequency & 0x700) | value as i32,
            0xff14 => {
                self.ch1.frequency = (self.ch1.frequency & 0xff) | (((value & 7) as i32) << 8);
                self.ch1
                    .length
                    .enable_write(value, skips, &mut self.ch1.enabled);
                if value & 0x80 != 0 {
                    self.ch1
                        .trigger(self.nr[0], self.nr[1], self.nr[2], self.nr[3], value, skips);
                }
            }
            0xff16 => self.ch2.length.counter = 64 - (value & 0x3f) as i32,
            0xff17 => {
                self.ch2.envelope.trigger(value);
                if value & 0xf8 == 0 {
                    self.ch2.enabled = false;
                }
            }
            0xff18 => self.ch2.frequency = (self.ch2.frequency & 0x700) | value as i32,
            0xff19 => {
                self.ch2.frequency = (self.ch2.frequency & 0xff) | (((value & 7) as i32) << 8);
                self.ch2
                    .length
                    .enable_write(value, skips, &mut self.ch2.enabled);
                if value & 0x80 != 0 {
                    self.ch2
                        .trigger(0, self.nr[6], self.nr[7], self.nr[8], value, skips);
                }
            }
            0xff1a => {
                self.ch3.dac_enabled = value & 0x80 != 0;
                if !self.ch3.dac_enabled {
                    self.ch3.enabled = false;
                }
            }
            0xff1b => self.ch3.length.counter = 256 - value as i32,
            0xff1c => self.ch3.volume_shift = ((value >> 5) & 3) as i32,
            0xff1d => self.ch3.frequency = (self.ch3.frequency & 0x700) | value as i32,
            0xff1e => {
                self.ch3.frequency = (self.ch3.frequency & 0xff) | (((value & 7) as i32) << 8);
                self.ch3
                    .length
                    .enable_write(value, skips, &mut self.ch3.enabled);
                if value & 0x80 != 0 {
                    self.ch3.trigger(
                        self.nr[10],
                        self.nr[11],
                        self.nr[12],
                        self.nr[13],
                        value,
                        skips,
                    );
                }
            }
            0xff20 => self.ch4.length.counter = 64 - (value & 0x3f) as i32,
            0xff21 => {
                self.ch4.envelope.trigger(value);
                if value & 0xf8 == 0 {
                    self.ch4.enabled = false;
                }
            }
            0xff22 => {
                self.ch4.clock_shift = ((value >> 4) & 15) as i32;
                self.ch4.width_mode = value & 8 != 0;
                self.ch4.divisor_code = (value & 7) as i32;
            }
            0xff23 => {
                self.ch4
                    .length
                    .enable_write(value, skips, &mut self.ch4.enabled);
                if value & 0x80 != 0 {
                    self.ch4
                        .trigger(self.nr[16], self.nr[17], self.nr[18], value, skips);
                }
            }
            0xff26 => {
                let enabled = value & 0x80 != 0;
                if !enabled && self.enabled {
                    self.power_off();
                }
                if enabled && !self.enabled && div_apu_bit_high {
                    self.frame_sequencer.skip_next = true;
                }
                self.enabled = enabled;
            }
            _ => (),
        }
    }
    fn power_off(&mut self) {
        self.nr[..=0x15].fill(0);
        self.ch1.enabled = false;
        self.ch1.length.enabled = false;
        self.ch1.duty_step = 0;
        self.ch2.enabled = false;
        self.ch2.length.enabled = false;
        self.ch2.duty_step = 0;
        self.ch3.enabled = false;
        self.ch3.length.enabled = false;
        self.ch3.reset_on_power_off();
        self.ch4.enabled = false;
        self.ch4.length.enabled = false;
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_bool(self.enabled);
        w.write_bytes(&self.nr);
        w.write_bytes(&self.ch3.wave_pattern);
        w.write_i32(self.sample_accum);
        w.write_i32(self.frame_sequencer.step);
        w.write_bool(self.frame_sequencer.skip_next);
        for ch in [&self.ch1, &self.ch2] {
            w.write_bool(ch.enabled);
            w.write_i32(ch.envelope.volume);
            w.write_i32(ch.frequency);
            w.write_i32(ch.duty_step);
            w.write_i32(ch.duty_pattern);
            w.write_i32(ch.length.counter);
            w.write_bool(ch.length.enabled);
        }
        w.write_bool(self.ch3.enabled);
        w.write_bool(self.ch3.dac_enabled);
        w.write_i32(self.ch3.frequency);
        w.write_i32(self.ch3.volume_shift);
        w.write_i32(self.ch3.length.counter);
        w.write_bool(self.ch3.length.enabled);
        w.write_bool(self.ch4.enabled);
        w.write_i32(self.ch4.envelope.volume);
        w.write_i32(self.ch4.shift_register);
        w.write_i32(self.ch4.clock_shift);
        w.write_bool(self.ch4.width_mode);
        w.write_i32(self.ch4.divisor_code);
        w.write_i32(self.ch4.length.counter);
        w.write_bool(self.ch4.length.enabled);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        let mut next = self.clone();
        next.read_state_inner(r)?;
        let squares_valid = [&next.ch1, &next.ch2].iter().all(|ch| {
            (0..=15).contains(&ch.envelope.volume)
                && (0..=2047).contains(&ch.frequency)
                && (0..8).contains(&ch.duty_step)
                && (0..4).contains(&ch.duty_pattern)
                && (0..=64).contains(&ch.length.counter)
        });
        if !squares_valid
            || !(0..4194304).contains(&next.sample_accum)
            || !(0..8).contains(&next.frame_sequencer.step)
            || !(0..=2047).contains(&next.ch3.frequency)
            || !(0..4).contains(&next.ch3.volume_shift)
            || !(0..=256).contains(&next.ch3.length.counter)
            || !(0..=15).contains(&next.ch4.envelope.volume)
            || !(0..=0x7fff).contains(&next.ch4.shift_register)
            || !(0..16).contains(&next.ch4.clock_shift)
            || !(0..8).contains(&next.ch4.divisor_code)
            || !(0..=64).contains(&next.ch4.length.counter)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid APU state",
            ));
        }
        *self = next;
        Ok(())
    }
    fn read_state_inner(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.enabled = r.read_bool()?;
        self.nr.copy_from_slice(r.read_bytes(0x30)?);
        self.ch3.wave_pattern.copy_from_slice(r.read_bytes(16)?);
        self.sample_accum = r.read_i32()?;
        self.frame_sequencer.step = r.read_i32()?;
        self.frame_sequencer.skip_next = r.read_bool()?;
        for ch in [&mut self.ch1, &mut self.ch2] {
            ch.enabled = r.read_bool()?;
            ch.envelope.volume = r.read_i32()?;
            ch.frequency = r.read_i32()?;
            ch.duty_step = r.read_i32()?;
            ch.duty_pattern = r.read_i32()?;
            ch.length.counter = r.read_i32()?;
            ch.length.enabled = r.read_bool()?;
        }
        self.ch3.enabled = r.read_bool()?;
        self.ch3.dac_enabled = r.read_bool()?;
        self.ch3.frequency = r.read_i32()?;
        self.ch3.volume_shift = r.read_i32()?;
        self.ch3.length.counter = r.read_i32()?;
        self.ch3.length.enabled = r.read_bool()?;
        self.ch4.enabled = r.read_bool()?;
        self.ch4.envelope.volume = r.read_i32()?;
        self.ch4.shift_register = r.read_i32()?;
        self.ch4.clock_shift = r.read_i32()?;
        self.ch4.width_mode = r.read_bool()?;
        self.ch4.divisor_code = r.read_i32()?;
        self.ch4.length.counter = r.read_i32()?;
        self.ch4.length.enabled = r.read_bool()?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn powered(mode: HardwareMode) -> Apu {
        let mut a = Apu::new(mode);
        a.write(0xff26, 0x80, false);
        a
    }

    #[test]
    fn managed_oracle_exact_pcm_registers_and_raw_state_dmg_and_cgb() {
        for (mode, expected) in [
            (HardwareMode::Dmg, include_bytes!("apu/dmg.bin").as_slice()),
            (HardwareMode::Cgb, include_bytes!("apu/cgb.bin").as_slice()),
        ] {
            let mut a = Apu::new(mode);
            a.write(0xff26, 0x80, true);
            for i in 0..16 {
                a.write(0xff30 + i, (i * 17) as u8, false);
            }
            for (address, value) in [
                (0xff10, 0x19),
                (0xff11, 0x80),
                (0xff12, 0xf3),
                (0xff13, 0xf8),
                (0xff14, 0x83),
                (0xff16, 0xc0),
                (0xff17, 0xa9),
                (0xff18, 0xf8),
                (0xff19, 0x87),
                (0xff1a, 0x80),
                (0xff1c, 0x20),
                (0xff1d, 0xff),
                (0xff1e, 0x87),
                (0xff21, 0xd2),
                (0xff22, 8),
                (0xff23, 0x80),
                (0xff24, 0x77),
                (0xff25, 0xff),
            ] {
                a.write(address, value, false);
            }
            let mut output = StateWriter::default();
            for block in 0..16 {
                if block == 3 {
                    a.write(0xff10, 0x11, false);
                    a.write(0xff1e, 0x87, false);
                }
                if block == 6 {
                    let mut w = StateWriter::default();
                    a.write_state(&mut w);
                    a = Apu::new(mode);
                    a.read_state(&mut StateReader::new(&w.bytes)).unwrap();
                }
                if block == 8 {
                    a.write(0xff26, 0, false);
                }
                if block == 9 {
                    a.write(0xff11, 63, false);
                    a.write(0xff26, 0x80, false);
                    a.write(0xff12, 0xf0, false);
                    a.write(0xff14, 0xc7, false);
                }
                if block == 12 {
                    a.write(0xff21, 0xf0, false);
                    a.write(0xff22, 0, false);
                    a.write(0xff23, 0x80, false);
                }
                for tick in 0..4096 {
                    a.tick_t();
                    if (block * 4096 + tick + 1) % 8192 == 0 {
                        a.advance_frame_sequencer();
                    }
                    if tick == 23 {
                        a.write(0xff35, (block * 9) as u8, false);
                    }
                }
                a.write_state(&mut output);
                for address in 0xff10..=0xff3f {
                    output.write_byte(a.read(address));
                }
                let mut pcm = vec![0; a.sample_buffer.available()];
                a.sample_buffer.drain(&mut pcm);
                output.write_i32(pcm.len() as i32);
                for sample in pcm {
                    output.write_u16(sample as u16);
                }
                assert_eq!(
                    &output.bytes,
                    &expected[..output.bytes.len()],
                    "mode {mode:?}, block {block}"
                );
            }
            assert_eq!(output.bytes, expected);
        }
    }

    #[test]
    fn state_order_roundtrip_omits_queue_and_internal_channel_phase() {
        let mut a = powered(HardwareMode::Dmg);
        a.write(0xff12, 0xf3, false);
        a.write(0xff14, 0x82, false);
        a.ch1.duty_step = 5;
        a.ch1.duty_pattern = 2;
        a.ch1.length.counter = 17;
        a.frame_sequencer.step = 3;
        a.frame_sequencer.skip_next = true;
        a.sample_buffer.push(-123);
        let mut w = StateWriter::default();
        a.write_state(&mut w);
        assert_eq!(w.bytes.len(), 156);
        assert_eq!(&w.bytes[65..75], &[0, 0, 0, 0, 3, 0, 0, 0, 1, 1]);
        assert_eq!(
            &w.bytes[75..96],
            &[
                15, 0, 0, 0, 0, 2, 0, 0, 5, 0, 0, 0, 2, 0, 0, 0, 17, 0, 0, 0, 0
            ]
        );
        let mut restored = Apu::new(HardwareMode::Dmg);
        restored.ch1.freq_cycle_counter = 123;
        restored.sample_buffer.push(456);
        restored
            .read_state(&mut StateReader::new(&w.bytes))
            .unwrap();
        assert_eq!(restored.ch1.freq_cycle_counter, 123);
        assert_eq!(restored.sample_buffer.drain(&mut [0; 0]), 0);
        let mut sample = [0];
        restored.sample_buffer.drain(&mut sample);
        assert_eq!(sample, [456]);
        let mut again = StateWriter::default();
        restored.write_state(&mut again);
        assert_eq!(again.bytes, w.bytes);
    }

    #[test]
    fn truncated_and_invalid_state_returns_error_without_partial_restore() {
        let a = powered(HardwareMode::Dmg);
        let mut w = StateWriter::default();
        a.write_state(&mut w);
        for length in 0..w.bytes.len() {
            let mut restored = Apu::new(HardwareMode::Dmg);
            assert_eq!(
                restored
                    .read_state(&mut StateReader::new(&w.bytes[..length]))
                    .unwrap_err()
                    .kind(),
                io::ErrorKind::UnexpectedEof
            );
            assert!(!restored.enabled);
        }
        for offset in [
            65, 69, 75, 79, 83, 87, 91, 97, 101, 105, 109, 113, 120, 124, 128, 134, 138, 142, 147,
            151,
        ] {
            let mut bytes = w.bytes.clone();
            bytes[offset..offset + 4].copy_from_slice(&i32::MAX.to_le_bytes());
            let mut restored = Apu::new(HardwareMode::Dmg);
            assert_eq!(
                restored
                    .read_state(&mut StateReader::new(&bytes))
                    .unwrap_err()
                    .kind(),
                io::ErrorKind::InvalidData,
                "offset {offset}"
            );
            assert!(!restored.enabled);
        }
    }

    #[test]
    fn frame_sequencer_step_pattern_fires_expected_clocks() {
        let mut fs = FrameSequencer::default();
        let mut clocks = (0, 0, 0);
        for _ in 0..8 {
            let (l, s, e) = fs.advance();
            clocks.0 += l as i32;
            clocks.1 += s as i32;
            clocks.2 += e as i32;
        }
        assert_eq!(clocks, (4, 2, 1));
        fs.skip_next = true;
        assert_eq!(fs.advance(), (false, false, false));
        assert_eq!(fs.step, 0);
        assert!(!fs.skip_next);
        fs.step = 7;
        fs.reset();
        assert_eq!(fs.step, 0);
    }

    #[test]
    fn square_channel_produces_nonzero_output_after_trigger() {
        let mut ch = SquareChannel::new(false);
        ch.trigger(0, 0x80, 0xf3, 0, 0x87, false);
        let mut max = 0;
        for _ in 0..20000 {
            ch.tick_t();
            max = max.max(ch.output());
        }
        assert_eq!(max, 15);
    }

    #[test]
    fn length_counter_disables_when_reaches_zero() {
        let mut l = LengthCounter::new(64);
        l.counter = 2;
        l.enabled = true;
        let mut enabled = true;
        l.tick(&mut enabled);
        assert!(enabled);
        l.tick(&mut enabled);
        assert!(!enabled);
        l.tick(&mut enabled);
        assert_eq!(l.counter, 0);
    }

    #[test]
    fn volume_envelope_increases_toward_15_and_decreases_to_zero() {
        let mut env = VolumeEnvelope::default();
        env.trigger(0x0c);
        for _ in 0..3 {
            env.tick();
        }
        assert_eq!(env.volume, 0);
        env.tick();
        assert_eq!(env.volume, 1);
        for _ in 0..100 {
            env.tick();
        }
        assert_eq!(env.volume, 15);
        env.trigger(0xf1);
        for _ in 0..100 {
            env.tick();
        }
        assert_eq!(env.volume, 0);
        env.trigger(0xf0);
        for _ in 0..100 {
            env.tick();
        }
        assert_eq!(env.volume, 15);
    }

    #[test]
    fn noise_channel_output_varies_and_width_bit_changes_lfsr() {
        for width in [0, 8] {
            let mut ch = NoiseChannel::default();
            ch.trigger(0, 0xf0, width, 0x80, false);
            let mut outputs = [false; 2];
            for _ in 0..1000 {
                ch.tick_t();
                outputs[(ch.output() != 0) as usize] = true;
            }
            assert_eq!(outputs, [true, true]);
        }
        let mut ch = NoiseChannel::default();
        ch.trigger(0, 0xf0, 8, 0x80, false);
        for _ in 0..8 {
            ch.tick_t();
        }
        assert_eq!(ch.shift_register, 0x3fbf);
    }

    #[test]
    fn wave_channel_emits_samples_from_pattern_and_delays_first_sample() {
        let mut ch = WaveChannel::new(false);
        ch.wave_pattern.fill(0xf0);
        ch.wave_pattern[0] = 0xab;
        ch.trigger(0x80, 0, 0x20, 0, 0x80, false);
        assert_eq!(ch.output(), 0);
        for _ in 0..4096 {
            ch.tick_t();
        }
        assert_eq!(ch.output(), 11);
        assert!(ch.just_read());
        ch.tick_t();
        assert!(!ch.just_read());
        let mut max = 0;
        for _ in 0..20000 {
            ch.tick_t();
            max = max.max(ch.output());
        }
        assert_eq!(max, 15);
    }

    #[test]
    fn audio_sample_buffer_push_and_drain_roundtrip() {
        let mut b = SampleBuffer::default();
        for i in 0..100 {
            b.push(i);
        }
        assert_eq!(b.available(), 100);
        let mut dst = [0; 100];
        assert_eq!(b.drain(&mut dst), 100);
        assert_eq!(dst[0], 0);
        assert_eq!(dst[99], 99);
        assert_eq!(b.available(), 0);
        assert_eq!(b.drain(&mut dst), 0);
    }

    #[test]
    fn wave_ram_roundtrips_through_ff30_ff3f() {
        let mut a = powered(HardwareMode::Dmg);
        for i in 0..16 {
            a.write(0xff30 + i, 0xa0 + i as u8, false);
        }
        for i in 0..16 {
            assert_eq!(a.read(0xff30 + i), 0xa0 + i as u8);
        }
    }

    #[test]
    fn trigger_length_running_zero_and_skipped_step_reload() {
        let mut ch = SquareChannel::new(false);
        ch.length.counter = 10;
        ch.trigger(0, 0, 0xf0, 0, 0x80, false);
        assert_eq!(ch.length.counter, 10);
        ch.length.counter = 0;
        ch.trigger(0, 0, 0xf0, 0, 0x80, false);
        assert_eq!(ch.length.counter, 64);
        ch.length.counter = 0;
        ch.trigger(0, 0, 0xf0, 0, 0xc0, true);
        assert_eq!(ch.length.counter, 63);
        let mut wave = WaveChannel::new(false);
        wave.trigger(0x80, 0, 0x20, 0, 0xc0, true);
        assert_eq!(wave.length.counter, 255);
        wave.trigger(0x80, 0, 0x20, 0, 0xc0, true);
        assert_eq!(wave.length.counter, 255);
        let mut noise = NoiseChannel::default();
        noise.trigger(0, 0xf0, 0, 0xc0, true);
        assert_eq!(noise.length.counter, 63);
    }

    #[test]
    fn nrx4_extra_length_clock_can_disable_all_four_channels() {
        for (length_addr, env_addr, trigger_addr) in [
            (0xff11, 0xff12, 0xff14),
            (0xff16, 0xff17, 0xff19),
            (0xff1b, 0xff1a, 0xff1e),
            (0xff20, 0xff21, 0xff23),
        ] {
            let mut a = powered(HardwareMode::Dmg);
            a.write(env_addr, 0xf0, false);
            a.write(trigger_addr, 0x80, false);
            a.write(
                length_addr,
                if length_addr == 0xff1b { 255 } else { 63 },
                false,
            );
            a.write(trigger_addr, 0x40, false);
            assert_eq!(a.read(0xff26) & 15, 0);
            a.write(trigger_addr, 0xc0, false);
            let counter = match trigger_addr {
                0xff14 => a.ch1.length.counter,
                0xff19 => a.ch2.length.counter,
                0xff1e => a.ch3.length.counter,
                _ => a.ch4.length.counter,
            };
            assert_eq!(counter, if trigger_addr == 0xff1e { 255 } else { 63 });
        }
    }

    #[test]
    fn power_off_resets_duty_but_preserves_lengths_wave_and_phase() {
        for mode in [HardwareMode::Dmg, HardwareMode::Cgb] {
            let mut a = powered(mode);
            a.write(0xff12, 0xf0, false);
            a.write(0xff14, 0x80, false);
            a.ch1.duty_step = 5;
            a.ch1.length.counter = 30;
            a.frame_sequencer.step = 5;
            a.write(0xff30, 0xab, false);
            a.write(0xff26, 0, false);
            assert_eq!(a.ch1.duty_step, 0);
            assert_eq!(a.ch1.length.counter, 30);
            assert_eq!(a.frame_sequencer.step, 5);
            assert_eq!(a.read(0xff30), 0xab);
            assert_eq!(a.read(0xff12), 0);
            a.write(0xff11, 63, false);
            a.write(0xff16, 62, false);
            a.write(0xff1b, 253, false);
            a.write(0xff20, 60, false);
            assert_eq!(
                (
                    a.ch1.length.counter,
                    a.ch2.length.counter,
                    a.ch3.length.counter,
                    a.ch4.length.counter
                ),
                (1, 2, 3, 4)
            );
            a.advance_frame_sequencer();
            assert_eq!(a.frame_sequencer.step, 6);
            assert_eq!(a.ch1.length.counter, 1);
        }
    }

    #[test]
    fn sweep_overflow_check_runs_immediately_on_trigger() {
        let mut ch = SquareChannel::new(true);
        ch.trigger(0x12, 0, 0xf0, 0xff, 0x87, false);
        assert!(!ch.enabled);
    }

    #[test]
    fn sweep_period_zero_skips_periodic_calculation() {
        let mut ch = SquareChannel::new(true);
        ch.trigger(1, 0, 0xf0, 0xff, 0x83, false);
        for _ in 0..64 {
            assert_eq!(ch.tick_sweep(1), None);
        }
        assert!(ch.enabled);
        assert_eq!(ch.frequency, 1023);
    }

    #[test]
    fn sweep_reads_live_shift_and_period_and_negate_exit_disables() {
        let mut ch = SquareChannel::new(true);
        ch.trigger(0x10, 0, 0xf0, 0xff, 0x87, false);
        assert!(ch.enabled);
        ch.tick_sweep(0x11);
        assert!(!ch.enabled);
        ch.trigger(9, 0, 0xf0, 0, 0x80, false);
        assert!(ch.enabled);
        ch.sweep
            .as_mut()
            .unwrap()
            .on_nr10_write(0x10, &mut ch.enabled);
        assert!(!ch.enabled);
        ch.trigger(0x10, 0, 0xf0, 0, 0x82, false);
        assert_eq!(ch.tick_sweep(0x29), Some(256));
        assert_eq!(ch.tick_sweep(0x29), None);
        assert_eq!(ch.tick_sweep(0x29), Some(128));
    }

    #[test]
    fn swept_frequency_mirrors_into_retrigger_registers() {
        let mut a = powered(HardwareMode::Dmg);
        a.write(0xff10, 0x19, false);
        a.write(0xff12, 0xf0, false);
        a.write(0xff13, 0, false);
        a.write(0xff14, 0x84, false);
        a.advance_frame_sequencer();
        a.advance_frame_sequencer();
        assert_eq!(a.ch1.frequency, 512);
        a.write(0xff14, 0x82, false);
        assert_eq!(a.ch1.frequency, 512);
    }

    #[test]
    fn dmg_retrigger_corrupts_but_cgb_preserves_wave_ram() {
        for is_cgb in [false, true] {
            for pos in [2, 9] {
                let mut ch = WaveChannel::new(is_cgb);
                for (i, b) in ch.wave_pattern.iter_mut().enumerate() {
                    *b = (i * 0x11) as u8;
                }
                ch.trigger(0x80, 0, 0x20, 0, 0x80, false);
                ch.wave_index = pos * 2;
                ch.just_read_countdown = 1;
                ch.trigger(0x80, 0, 0x20, 0, 0x80, false);
                let expected = if is_cgb {
                    [0, 0x11, 0x22, 0x33]
                } else if pos == 2 {
                    [0x22, 0x11, 0x22, 0x33]
                } else {
                    [0x88, 0x99, 0xaa, 0xbb]
                };
                assert_eq!(ch.wave_pattern[..4], expected);
            }
        }
    }

    #[test]
    fn dmg_wave_ram_access_window_is_one_tcycle_and_cgb_always_redirects() {
        for mode in [HardwareMode::Dmg, HardwareMode::Cgb] {
            let mut a = powered(mode);
            for i in 0..16 {
                a.write(0xff30 + i, (i * 0x11) as u8, false);
            }
            a.write(0xff1a, 0x80, false);
            a.write(0xff1d, 0xff, false);
            a.write(0xff1e, 0x87, false);
            assert_eq!(
                a.read(0xff35),
                if mode == HardwareMode::Dmg { 255 } else { 0 }
            );
            a.write(0xff36, 0x77, false);
            assert_eq!(
                a.ch3.wave_pattern[0],
                if mode == HardwareMode::Dmg { 0 } else { 0x77 }
            );
            a.tick_t();
            a.tick_t();
            assert!(a.ch3.just_read());
            assert_eq!(a.read(0xff38), a.ch3.wave_pattern[0]);
            a.write(0xff39, 0x55, false);
            assert_eq!(a.ch3.wave_pattern[0], 0x55);
            a.tick_t();
            assert!(!a.ch3.just_read());
            assert_eq!(
                a.read(0xff35),
                if mode == HardwareMode::Dmg { 255 } else { 0x55 }
            );
        }
    }

    #[test]
    fn div_high_power_on_skips_exactly_one_edge_and_does_not_reset_step() {
        let mut a = Apu::new(HardwareMode::Dmg);
        a.advance_frame_sequencer();
        a.write(0xff26, 0x80, true);
        a.write(0xff12, 0xf0, false);
        a.write(0xff14, 0xc0, false);
        let length = a.ch1.length.counter;
        a.advance_frame_sequencer();
        assert_eq!(a.frame_sequencer.step, 1);
        assert_eq!(a.ch1.length.counter, length);
        a.advance_frame_sequencer();
        assert_eq!(a.frame_sequencer.step, 2);
        assert_eq!(a.ch1.length.counter, length - 1);
        a.write(0xff26, 0x80, true);
        assert!(!a.frame_sequencer.skip_next);
    }

    #[test]
    fn register_masks_and_dac_disable() {
        let mut a = powered(HardwareMode::Dmg);
        for (addr, mask) in [
            (0xff10, 0x80),
            (0xff11, 0x3f),
            (0xff14, 0xbf),
            (0xff16, 0x3f),
            (0xff19, 0xbf),
            (0xff1a, 0x7f),
            (0xff1c, 0x9f),
            (0xff1e, 0xbf),
            (0xff23, 0xbf),
        ] {
            a.write(addr, 0, false);
            assert_eq!(a.read(addr), mask);
        }
        for addr in [
            0xff0f, 0xff13, 0xff15, 0xff18, 0xff1b, 0xff1d, 0xff1f, 0xff20, 0xff27, 0xff40,
        ] {
            assert_eq!(a.read(addr), 255);
        }
        for (env, trig) in [
            (0xff12, 0xff14),
            (0xff17, 0xff19),
            (0xff1a, 0xff1e),
            (0xff21, 0xff23),
        ] {
            a.write(env, 0xf0, false);
            a.write(trig, 0x80, false);
            assert_ne!(a.read(0xff26) & 15, 0);
            a.write(env, 0, false);
            assert_eq!(a.read(0xff26) & 15, 0);
            a.write(trig, 0x80, false);
            assert_eq!(a.read(0xff26) & 15, 0);
        }
    }

    #[test]
    fn nr52_power_and_disabled_writes() {
        let mut a = Apu::new(HardwareMode::Dmg);
        assert_eq!(a.read(0xff26), 0x70);
        a.write(0xff12, 0xf0, false);
        a.write(0xff26, 0x80, false);
        assert_eq!(a.read(0xff12), 0);
        assert_eq!(a.read(0xff26), 0xf0);
        a.write(0xff26, 0, false);
        for _ in 0..10000 {
            a.tick_t();
        }
        assert_eq!(a.sample_buffer.available(), 0);
    }

    #[test]
    fn pcm_square_literal_and_fractional_sample_clock() {
        let mut a = Apu::new(HardwareMode::Dmg);
        a.write(0xff26, 0x80, false);
        a.write(0xff16, 0x80, false);
        a.write(0xff17, 0xf0, false);
        a.write(0xff18, 0xf8, false);
        a.write(0xff19, 0x87, false);
        for _ in 0..1000 {
            a.tick_t();
        }
        let mut pcm = [0; 10];
        assert_eq!(a.sample_buffer.drain(&mut pcm), 10);
        assert_eq!(pcm, [0, 12000, 12000, 0, 12000, 0, 0, 12000, 0, 12000]);
        let mut count = 10;
        for _ in 1000..4194304 {
            a.tick_t();
            count += a.sample_buffer.drain(&mut [0; 1]);
        }
        assert_eq!(count, 44100);
    }

    #[test]
    fn bounded_ring_drops_oldest() {
        let mut b = SampleBuffer::default();
        for i in 0..9000 {
            b.push(i);
        }
        assert_eq!(b.available(), 8191);
        let mut dst = vec![0; 9000];
        assert_eq!(b.drain(&mut dst), 8191);
        assert_eq!(&dst[..3], &[809, 810, 811]);
        assert_eq!(dst[8190], 8999);
        assert_eq!(b.available(), 0);
    }
}
