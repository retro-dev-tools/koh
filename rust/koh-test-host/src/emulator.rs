use crate::common::*;
use koh_emulator::{HardwareMode, cartridge::MapperKind, cpu::Registers};
use std::fmt::Write;

pub fn run(args: &[String]) -> Result {
    let option = args.first().map_or("", String::as_str);
    let cost = option == "--cost";
    let narrowing = option == "--native-narrowing-cost";
    let region = option.starts_with("--native-region:");
    let arena_start = if region {
        number(option.split(':').nth(1).ok_or("Missing arena start")?)?
    } else {
        0xd000
    };
    if !(0xc100..=0xd000).contains(&arena_start) {
        return Err("Invalid arenaStart".into());
    }
    let loop_cost = option == "--loop-cost";
    let observe = option == "--observe-memory";
    let fill_cost = option == "--native-heap-cost";
    let fill_wrap = option == "--native-fill-wrap";
    let copy_wrap = option == "--native-copy-wrap";
    let raster = option == "--native-raster";
    let stack = option == "--native-stack";
    let frame_mode = option == "--native-frame";
    let lifecycle = option == "--native-video-lifecycle";
    let palette_trap = option == "--native-palettes-trap";
    let palettes = palette_trap || option == "--native-palettes";
    let map_commit = option == "--native-map-commit";
    let sprites = palettes || option == "--native-sprites";
    let oam_mode = map_commit || sprites || option == "--native-oam";
    let raw_oam = option == "--native-oam-trigger";
    let canvas = option == "--native-canvas";
    let maps = map_commit || canvas || option == "--native-maps";
    let cpu_mode = option == "--native-cpu";
    let arena = option == "--native-arena";
    let scroll = option == "--native-scroll";
    let registers = option == "--native-registers";
    let cgb_hal = option == "--native-cgb-hal";
    let scene = option == "--native-scene";
    let framework_input = option == "--native-framework-input";
    let framework_game = option == "--native-framework-game";
    let irq_abi = option.starts_with("--native-irq-abi:");
    let irq_reserve = if irq_abi {
        number(option.split(':').nth(1).ok_or("Missing IRQ reserve")?)?
    } else {
        0
    };
    let irq_count = option == "--native-irq-count";
    let video = lifecycle
        || frame_mode
        || oam_mode
        || raw_oam
        || maps
        || framework_game
        || option == "--native-video";
    let heap = region
        || video
        || stack
        || arena
        || scroll
        || registers
        || cgb_hal
        || scene
        || framework_input
        || irq_abi
        || irq_count
        || fill_cost
        || fill_wrap
        || copy_wrap
        || raster
        || option == "--native-heap";
    let native =
        option == "--native" || option == "--native-poison" || heap || cpu_mode || narrowing;
    let (stop_bank, stop) = if native {
        let parts: Vec<_> = args[1].split(':').collect();
        if parts.len() != 2 {
            return Err("Native stop requires bank:address".into());
        }
        (
            u16::from_str_radix(parts[0], 16).map_err(|e| e.to_string())?,
            number(parts[1])?,
        )
    } else {
        (0, 0)
    };
    for path in args.iter().skip(if native {
        2
    } else if cost || observe || loop_cost {
        1
    } else {
        0
    }) {
        let mut m = load(path)?;
        let color = m.mode == HardwareMode::Cgb;
        let dma_before = hex(&[m.hdma.source1, m.hdma.source2, m.hdma.dest1, m.hdma.dest2]);
        for a in 0xc000..0xc020 {
            m.write_debug(a, 0);
        }
        m.cpu.registers.pc = 0x100;
        if framework_input {
            m.write_debug(0xff43, 0);
            m.joypad_press(1);
        }
        if framework_game {
            m.joypad_press(16);
        }
        let mut input_phase = 0;
        let mut frame_markers = Vec::new();
        let mut map_markers = Vec::new();
        let mut prior_scroll = 0;
        if lifecycle || palettes || map_commit {
            for a in [0xff43, 0xff42, 0xff4a, 0xff4b] {
                m.write_debug(a, 0);
            }
        }
        if loop_cost {
            for off in 0..32 {
                m.write_debug(0xc100 + off, 0xd2);
                m.write_debug(0xc200 + off, 0xe3);
            }
            for (a, v) in [(0xc120, 0xa7), (0xc1ff, 0xc9), (0xc220, 0xb8)] {
                m.write_debug(a, v);
            }
        }
        if cgb_hal || sprites || lifecycle {
            m.write_debug(0xff40, 0);
            m.vram_bank = 0;
            m.vram[..0x2000].fill(0xa7);
            if color {
                m.vram[0x2000..].fill(0xb8);
                for p in 0..8 {
                    for s in 0..4 {
                        m.ppu.bg_palette.set_color(p, s, 0x3d3d);
                        m.ppu.obj_palette.set_color(p, s, 0x6c6c);
                    }
                }
                m.ppu.bg_palette.index_register = 0x2a;
                m.ppu.obj_palette.index_register = 0x15;
            }
        }
        if video {
            m.oam.fill(0xc9);
            for address in 0xfe00..0xfea0 {
                if m.read_debug(address) != 0xc9 {
                    return Err("Cannot poison OAM test memory".into());
                }
            }
            for bank in if color { vec![0, 1] } else { vec![0] } {
                m.write_debug(0xff4f, bank);
                let poison = if bank == 0 { 0xa7 } else { 0xb8 };
                for a in 0x8000..0xa000 {
                    m.write_debug(a, poison);
                    if m.read_debug(a) != poison {
                        return Err("Cannot poison video test memory".into());
                    }
                }
            }
            m.write_debug(0xff4f, 0);
        }
        if stack {
            for a in 0xc100..0xd000 {
                m.write_debug(a, 0xa6);
                if m.read_debug(a) != 0xa6 {
                    return Err("Cannot poison stack test memory".into());
                }
            }
        }
        if heap || option == "--native-poison" {
            for a in 0xc000..0xc080 {
                m.write_debug(a, 0xa5);
                if m.read_debug(a) != 0xa5 {
                    return Err("Cannot poison static test memory".into());
                }
            }
        }
        if heap {
            for bank in if color { vec![2, 1] } else { vec![1] } {
                m.write_debug(0xff70, bank);
                let poison = if bank == 1 { 0x5a } else { 0x6b };
                for a in if bank == 1 { arena_start } else { 0xd000 }..0xe000 {
                    m.write_debug(a, poison);
                    if m.read_debug(a) != poison {
                        return Err("Cannot poison heap test memory".into());
                    }
                }
            }
        }
        for a in 0xc080..0xc100 {
            m.write_debug(a, 0x5a);
        }
        let trace = Hook::attach(
            &mut m,
            TraceOptions {
                video: video || cgb_hal,
                guard_oam: oam_mode,
                guard_palettes: sprites,
                guard_palette_ime: palettes,
                wrap: fill_wrap || copy_wrap,
                wrap_reads: copy_wrap,
                raster,
                hal: cgb_hal,
                observe,
                loop_timing: loop_cost,
                ..Default::default()
            },
        );
        let mut steps = 0;
        let mut dma_instructions = 0;
        let mut control_ime = None;
        let mut cpu_enables = 0;
        let mut cpu_halts = 0;
        let mut delayed_enable = false;
        let mut interrupted: Option<Registers> = None;
        let mut injected = false;
        let mut irq_checks = 0;
        let mut irq_sp = 0;
        let mut irq_bank_low = 0;
        let mut irq_bank_high = 0;
        let mut vblank_ticks = Vec::<u64>::new();
        let finished = |m: &koh_emulator::machine::Machine, oam_starts: usize| {
            if raw_oam {
                return oam_starts != 0;
            }
            if !native {
                return m.read_debug(0xc000) != 0;
            }
            let pc = m.cpu.registers.pc;
            if pc != stop {
                return false;
            }
            if pc >= 0x8000 {
                return true;
            }
            let c = &m.cartridge;
            let bank = if pc < 0x4000 {
                if c.kind == MapperKind::Mbc1 && c.mbc1_mode == 1 {
                    (c.mbc1_bank_high as u16) << 5
                } else {
                    0
                }
            } else if c.kind == MapperKind::Mbc5 {
                ((c.mbc1_bank_high as u16) << 8) | c.mbc1_bank_low as u16
            } else {
                c.current_rom_bank() as u16
            };
            bank == stop_bank
        };
        while !finished(&m, trace.lock().unwrap().oam_starts)
            && steps < if maps { 8000000 } else { 2000000 }
        {
            steps += 1;
            if oam_mode && m.oam_dma.is_bus_locking {
                if (sprites || map_commit) && m.ppu.lcdc & 128 != 0 && m.ppu.ly < 144 {
                    return Err(format!(
                        "Sprite DMA overlaps visible scanline: LY={},dot={},pc={:04X}",
                        m.ppu.ly, m.ppu.dot, m.cpu.registers.pc
                    ));
                }
                if m.cpu.ime || !(0xff80..=0xfffe).contains(&m.cpu.registers.pc) {
                    return Err("DMA execution outside HRAM or with IME enabled".into());
                }
                dma_instructions += 1;
            }
            if cpu_mode && m.cpu.halted {
                cpu_halts += 1;
                if cpu_halts != 1 || m.cpu.ime {
                    return Err("Invalid HALT state".into());
                }
                m.write_debug(0xff0f, 1);
            }
            let opcode = if cpu_mode && !m.cpu.halted {
                m.read_debug(m.cpu.registers.pc)
            } else {
                0
            };
            if irq_abi && m.cpu.ime && (m.cpu.registers.sp as u32) < 0xc100 + irq_reserve as u32 {
                return Err("Main code consumed the IRQ stack reserve".into());
            }
            if irq_abi
                && !injected
                && m.cpu.ime
                && (m.cpu.registers.sp as u32) < 0xc200 + irq_reserve as u32
                && m.cpu.registers.flag_set(Registers::FLAG_C)
                && m.read_debug(m.cpu.registers.pc) == 0xda
            {
                m.write_debug(0xff0f, 1);
                injected = true;
            }
            let returning = (irq_abi || irq_count)
                && interrupted.is_some()
                && m.read_debug(m.cpu.registers.pc) == 0xd9;
            m.step_instruction();
            if map_commit {
                let phase = m.read_debug(0xff43);
                if phase != prior_scroll {
                    if phase == 62 {
                        m.vram[0x1800 + 98] = 0xaa;
                        m.vram[0x1c00 + 98] = 0xaa;
                    }
                    map_markers.push(format!(
                        "{phase}:{}:{}",
                        trace.lock().unwrap().gdma_starts,
                        hex(&m.vram[0x1800..0x2000])
                    ));
                    prior_scroll = phase;
                }
            }
            if lifecycle {
                let phase = m.read_debug(0xff43);
                if phase != prior_scroll {
                    frame_markers.push(format!(
                        "{phase}:{}:{}:{}",
                        m.ppu.ly, m.ppu.dot, m.system_ticks
                    ));
                    prior_scroll = phase;
                }
            }
            if framework_input {
                let phase = m.read_debug(0xff43);
                if phase == 1 && input_phase == 0 {
                    m.joypad_release(1);
                    input_phase = 1;
                } else if phase == 2 && input_phase == 1 {
                    m.joypad_press(2);
                    input_phase = 2;
                }
            }
            if irq_count && m.cpu.registers.pc == 0x40 {
                let tick = m.system_ticks;
                if m.ppu.ly != 144 {
                    return Err("VBlank dispatched outside line144".into());
                }
                if let Some(&prior) = vblank_ticks.last()
                    && !(70200..=70248).contains(&(tick - prior))
                {
                    return Err(format!("Invalid VBlank period {}", tick - prior));
                }
                vblank_ticks.push(tick);
            }
            if returning {
                let expected = interrupted.unwrap();
                if m.cpu.registers != expected
                    || !m.cpu.ime
                    || m.cartridge.mbc1_bank_low != irq_bank_low
                    || m.cartridge.mbc1_bank_high != irq_bank_high
                {
                    return Err(format!(
                        "Interrupt restore expected={expected:?} actual={:?},IME={}",
                        m.cpu.registers, m.cpu.ime
                    ));
                }
                interrupted = None;
                irq_checks += 1;
            }
            if (irq_abi || irq_count) && m.cpu.registers.pc == 0x40 && interrupted.is_none() {
                let mut expected = m.cpu.registers;
                expected.pc = word(&m, expected.sp);
                expected.sp = expected.sp.wrapping_add(2);
                irq_sp = expected.sp;
                irq_bank_low = m.cartridge.mbc1_bank_low;
                irq_bank_high = m.cartridge.mbc1_bank_high;
                interrupted = Some(expected);
            }
            if cpu_mode {
                if (opcode == 0xf3 && m.cpu.ime) || (delayed_enable && opcode != 0xf3 && !m.cpu.ime)
                {
                    return Err("Invalid DI/EI state".into());
                }
                delayed_enable = opcode == 0xfb;
                if delayed_enable {
                    cpu_enables += 1;
                    if m.cpu.ime {
                        return Err("EI enabled before the following instruction".into());
                    }
                }
            }
            if cpu_mode && control_ime.is_none() && m.read_debug(0xff43) == 1 {
                control_ime = Some(m.cpu.ime);
            }
        }
        if oam_mode
            && !palette_trap
            && (m.oam_dma.is_bus_locking
                || trace.lock().unwrap().oam_starts == 0
                || dma_instructions == 0)
        {
            return Err("OAM DMA was not observed completing".into());
        }
        if raw_oam {
            for _ in 0..700 {
                if let Some((source, index)) = m.oam_dma.tick_t() {
                    m.oam[index] = m.read_debug(source);
                }
            }
            if m.oam_dma.is_bus_locking {
                return Err("Raw OAM DMA did not complete".into());
            }
        }
        if !finished(&m, trace.lock().unwrap().oam_starts) {
            return Err(format!(
                "ROM did not finish: {path}, PC={:04X}, C010={:02X}",
                m.cpu.registers.pc,
                m.read_debug(0xc010)
            ));
        }
        if framework_input && input_phase != 2 {
            return Err("Input phase sequence incomplete".into());
        }
        if irq_abi && (irq_checks != 1 || interrupted.is_some()) {
            return Err("Interrupt ABI check did not complete exactly once".into());
        }
        if frame_mode {
            for _ in 0..2 {
                frame(&mut m)?;
            }
            write(
                std::path::Path::new(path).with_extension("rgba"),
                &m.ppu.framebuffer,
            )?;
            png(&m, std::path::Path::new(path).with_extension("png"))?;
        }
        let t = trace.lock().unwrap().clone();
        if !t.violations.is_empty() {
            return Err(format!(
                "Mode-3 VRAM writes: {}, first={:?}; {}",
                t.violations.len(),
                t.violations[0],
                t.video_writes.join("; ")
            ));
        }
        if let Some(error) = &t.unsafe_dma {
            return Err(error.clone());
        }
        let result = read(&m, 0xc008, 8);
        let guard = if (0xc080..0xc100).all(|a| m.read_debug(a) == 0x5a) {
            "OK"
        } else {
            "CORRUPT"
        };
        let mut snapshot = String::new();
        if native {
            if narrowing {
                write!(
                    snapshot,
                    " {:02X} {}",
                    m.read_debug(0xff47),
                    m.cpu.total_tcycles
                )
                .unwrap();
            }
            if cpu_mode {
                write!(
                    snapshot,
                    " {}:{}:{}:{}:{:02X} {cpu_enables}:{cpu_halts}",
                    yes(control_ime != Some(false)),
                    yes(m.double_speed),
                    yes(m.switch_armed),
                    yes(m.cpu.stopped),
                    m.read_debug(0xff4d)
                )
                .unwrap();
            }
            if heap {
                let saved = m.read_debug(0xff70);
                for bank in if color { vec![1, 2] } else { vec![1] } {
                    m.write_debug(0xff70, bank);
                    let start = if bank == 1 { arena_start } else { 0xd000 };
                    write!(
                        snapshot,
                        " {}",
                        hex(&read(&m, start, (0xe000 - start) as usize))
                    )
                    .unwrap();
                }
                m.write_debug(0xff70, saved);
            }
            if video {
                let saved = m.read_debug(0xff4f);
                for bank in if color { vec![0, 1] } else { vec![0] } {
                    m.write_debug(0xff4f, bank);
                    write!(snapshot, " {}", hex(&read(&m, 0x8000, 0x2000))).unwrap();
                }
                m.write_debug(0xff4f, saved);
                write!(
                    snapshot,
                    " {dma_before}:{} {} {}",
                    hex(&[
                        m.hdma.source1,
                        m.hdma.source2,
                        m.hdma.dest1,
                        m.hdma.dest2,
                        yes(m.hdma.active),
                        yes(m.hdma.is_hblank_mode),
                        yes(m.hdma.cpu_halted_by_gp)
                    ]),
                    hex(&read(&m, 0xfe00, 160)),
                    hex(&[
                        m.read_debug(0xff40),
                        m.read_debug(0xff47),
                        m.read_debug(0xff48),
                        m.read_debug(0xff49)
                    ])
                )
                .unwrap();
            }
            if oam_mode || raw_oam {
                write!(
                    snapshot,
                    " {}:{dma_instructions}:{:02X}",
                    t.oam_starts,
                    m.read_debug(0xff46)
                )
                .unwrap();
            }
            if sprites {
                write!(
                    snapshot,
                    " {} {:02X}{:02X}{:02X} {}",
                    if color {
                        hex(m.ppu.obj_palette.raw_data())
                    } else {
                        "-".into()
                    },
                    m.read_debug(0xff45),
                    m.read_debug(0xff48),
                    m.read_debug(0xff49),
                    if color {
                        format!(
                            "{:02X}:{:02X}",
                            m.read_debug(0xff6a) & 0xbf,
                            m.read_debug(0xff6b)
                        )
                    } else {
                        "-".into()
                    }
                )
                .unwrap();
            }
            if palettes {
                write!(
                    snapshot,
                    " {} {} {:02X}{:02X} {}:{}",
                    if color {
                        hex(m.ppu.bg_palette.raw_data())
                    } else {
                        "-".into()
                    },
                    if color {
                        format!(
                            "{:02X}:{:02X}",
                            m.read_debug(0xff68) & 0xbf,
                            m.read_debug(0xff69)
                        )
                    } else {
                        "-".into()
                    },
                    m.read_debug(0xff43),
                    m.read_debug(0xff42),
                    yes(m.double_speed),
                    yes(m.switch_armed)
                )
                .unwrap();
            }
            if arena {
                write!(snapshot, " {:02X}", m.read_debug(0xff47)).unwrap();
            }
            if map_commit {
                write!(snapshot, " {}", map_markers.join(",")).unwrap();
            }
            if scroll {
                write!(
                    snapshot,
                    " {:02X}{:02X}",
                    m.read_debug(0xff42),
                    m.read_debug(0xff43)
                )
                .unwrap();
            }
            if registers {
                write!(
                    snapshot,
                    " {:02X}{:02X}{:02X}{:02X}",
                    m.read_debug(0xff42),
                    m.read_debug(0xff43),
                    m.read_debug(0xff47),
                    m.read_debug(0xff4a)
                )
                .unwrap();
            }
            if cgb_hal {
                write!(
                    snapshot,
                    " {:02X}:{}:{}:{} {} {} {:02X}{} {}",
                    m.vram_bank,
                    if color {
                        format!("{:02X}", m.read_debug(0xff4f))
                    } else {
                        "-".into()
                    },
                    if color {
                        format!("{:02X}", m.ppu.bg_palette.index_register)
                    } else {
                        "-".into()
                    },
                    yes(m.double_speed),
                    if color {
                        hex(m.ppu.bg_palette.raw_data())
                    } else {
                        "-".into()
                    },
                    if color {
                        hex(m.ppu.obj_palette.raw_data())
                    } else {
                        "-".into()
                    },
                    m.vram[0],
                    if color {
                        format!("{:02X}", m.vram[0x2000])
                    } else {
                        String::new()
                    },
                    joined(&t.writes)
                )
                .unwrap();
            }
            if fill_cost {
                write!(snapshot, " {}", m.cpu.total_tcycles).unwrap();
            }
            if fill_wrap {
                write!(snapshot, " {}", t.writes.join(",")).unwrap();
            }
            if copy_wrap {
                write!(snapshot, " {} {}", joined(&t.reads), joined(&t.writes)).unwrap();
            }
            if raster {
                write!(
                    snapshot,
                    " {}:{:02X}:{} {} {}",
                    yes(t.final_ime.unwrap_or(m.cpu.ime)),
                    m.read_debug(0xff70),
                    yes(m.double_speed),
                    joined(&t.reads),
                    joined(&t.writes)
                )
                .unwrap();
            }
            if scene {
                write!(snapshot, " {}", hex(&result)).unwrap();
            }
            if lifecycle {
                write!(
                    snapshot,
                    " {:02X}{:02X}{:02X}{:02X} {} {}",
                    m.read_debug(0xff4b),
                    m.read_debug(0xff4a),
                    m.read_debug(0xff43),
                    m.read_debug(0xff42),
                    t.oam_starts,
                    joined(&frame_markers)
                )
                .unwrap();
            }
            if canvas {
                write!(snapshot, " {}", t.gdma_starts).unwrap();
            }
            if irq_abi {
                write!(snapshot, " {irq_checks}:{irq_sp:04X}").unwrap();
            }
            if irq_count {
                write!(snapshot," {}:{:02X}{:02X}{:02X}{:02X}:{}:{:02X}{:02X}:{irq_bank_high:02X}{irq_bank_low:02X}:{:02X}{:02X}{:02X}{:02X}",vblank_ticks.len(),m.read_debug(0xff42),m.read_debug(0xff43),m.read_debug(0xff47),m.read_debug(0xff45),hex(&result),m.read_debug(0xc020),m.read_debug(0xc021),m.read_debug(0xc022),m.read_debug(0xc023),m.read_debug(0xc024),m.read_debug(0xc025)).unwrap();
            }
            println!(
                "{:02X} {:04X}{:04X} {:04X} {guard}{snapshot}",
                m.cpu.registers.a,
                m.cpu.registers.de(),
                m.cpu.registers.hl(),
                m.cpu.registers.sp
            );
        } else {
            if cost {
                write!(snapshot, " {}", m.cpu.total_tcycles).unwrap();
            }
            if loop_cost {
                if t.loop_count != 2 {
                    return Err("Expected exactly one ordered loop timing window".into());
                }
                write!(
                    snapshot,
                    " {} {} {} {} {:02X}{:02X}{:02X} {}",
                    m.cpu.total_tcycles,
                    t.loop_end - t.loop_start,
                    hex(&read(&m, 0xc100, 32)),
                    hex(&read(&m, 0xc200, 32)),
                    m.read_debug(0xc120),
                    m.read_debug(0xc1ff),
                    m.read_debug(0xc220),
                    t.initialized.map_or("-".into(), |tick| format!(
                        "{}:{}",
                        tick - t.loop_start,
                        t.loop_end - tick
                    ))
                )
                .unwrap();
            }
            if observe {
                write!(snapshot, " {}", joined(&t.reads)).unwrap();
            }
            println!(
                "{:02X} {:02X} {} {:04X} {guard}{snapshot}",
                m.read_debug(0xc000),
                m.read_debug(0xc001),
                hex(&result),
                m.cpu.registers.sp
            );
        }
    }
    Ok(())
}
