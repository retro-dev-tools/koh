use crate::common::*;
use koh_emulator::machine::Machine;
use std::{collections::BTreeMap, io::Write};

pub fn vblank(args: &[String]) -> Result {
    let rom = std::fs::read(&args[0]).map_err(|e| e.to_string())?;
    let stop = number(&args[1])?;
    let site = number(&args[2])?;
    let end = number(&args[3])?;
    for line in 0..154 {
        for dot in [0, 452] {
            let mut m = Machine::load_rom(&rom)?;
            m.cpu.registers.pc = 0x100;
            m.vram.fill(0xa7);
            m.write_debug(0xff70, 2);
            let mut steps = 0;
            while m.cpu.registers.pc != site && steps < 100000 {
                steps += 1;
                m.step_instruction();
            }
            if m.cpu.registers.pc != site {
                return Err("Cannot reach copy operation".into());
            }
            m.cpu.registers.pc = 0x3001;
            m.step_instruction();
            m.step_instruction();
            m.write_debug(0xff40, 0x91);
            for _ in (0..line * 456 + dot).step_by(4) {
                m.cpu.registers.pc = 0x3000;
                m.step_instruction();
            }
            if args.get(4).is_some_and(|s| s == "off") {
                while m.ppu.ly < 144 {
                    m.cpu.registers.pc = 0x3000;
                    m.step_instruction();
                }
                m.write_debug(0xff40, 0);
            }
            m.cpu.registers.pc = site;
            let trace = Hook::attach(
                &mut m,
                TraceOptions {
                    vblank: true,
                    ..Default::default()
                },
            );
            let start = m.system_ticks;
            let mut ime = None;
            steps = 0;
            while m.cpu.registers.pc != stop && steps < 100000 {
                steps += 1;
                if m.cpu.registers.pc == end {
                    ime = Some(m.cpu.ime);
                }
                m.step_instruction();
            }
            if m.cpu.registers.pc != stop {
                return Err("Copy did not finish".into());
            }
            let ime = ime.ok_or("Copy boundary was not observed")?;
            let count = ((m.cpu.registers.de() as u32) << 16) | m.cpu.registers.hl() as u32;
            let t = trace.lock().unwrap();
            let cells: Vec<_> = t
                .cells
                .iter()
                .map(|&(a, v, ly, dot)| format!("{a}:{v}:{ly}:{dot}"))
                .collect();
            println!(
                "{line} {dot} {count} {} {} {} {} {} {} {} {}",
                m.cpu.registers.sp,
                m.read_debug(0xff70) & 7,
                t.ly_reads,
                m.system_ticks - start,
                hex(&m.vram),
                joined(&cells),
                t.source_reads,
                yes(ime)
            );
        }
    }
    Ok(())
}
pub fn game(args: &[String]) -> Result {
    let mut m = load(&args[0])?;
    boot(&mut m)?;
    let abort = number(&args[1])?;
    let actions: Vec<Vec<String>> = std::fs::read_to_string(&args[2])
        .map_err(|e| e.to_string())?
        .lines()
        .map(|line| line.split(':').map(str::to_owned).collect())
        .collect();
    let last = actions
        .iter()
        .map(|a| a[0].parse::<usize>().unwrap())
        .max()
        .ok_or("No actions")?;
    let trace = Hook::attach(
        &mut m,
        TraceOptions {
            video: true,
            ..Default::default()
        },
    );
    let mut boots = 0;
    while m.boot_rom_mapped() && boots < 600 {
        boots += 1;
        game_frame(&mut m, abort)?;
    }
    if m.boot_rom_mapped() {
        return Err("Boot did not complete".into());
    }
    let first = if args.get(3).is_some_and(|s| s == "power-on") {
        boots
    } else {
        0
    };
    for frame_index in first..=last {
        let current: Vec<_> = actions
            .iter()
            .filter(|a| a[0].parse::<usize>().unwrap() == frame_index)
            .collect();
        for action in &current {
            if action[1] == "dump" {
                continue;
            }
            match action[1].as_str() {
                "write" => {
                    let address = number(&action[2])?;
                    for (i, b) in unhex(&action[3])?.into_iter().enumerate() {
                        let a = address
                            .checked_add(i as u16)
                            .ok_or("Write address overflow")?;
                        m.write_debug(a, b);
                    }
                }
                "press" => m.joypad_press(button(&action[2])?),
                "release" => m.joypad_release(button(&action[2])?),
                _ => return Err("Unknown input action".into()),
            }
        }
        game_frame(&mut m, abort)?;
        for action in current {
            if action[1] != "dump" {
                continue;
            }
            let path = format!("{}.{}", args[0], action[2]);
            let mut memory = read(&m, 0xc000, 0x1000);
            let bank = m.read_debug(0xff70);
            m.write_debug(0xff70, 1);
            memory.extend(read(&m, 0xd000, 0x1000));
            m.write_debug(0xff70, bank);
            for (suffix, bytes) in [
                ("wram", memory),
                ("rgba", m.ppu.framebuffer.clone()),
                ("vram", m.vram.clone()),
                ("palette", m.ppu.bg_palette.raw_data().to_vec()),
                ("objpalette", m.ppu.obj_palette.raw_data().to_vec()),
                ("oam", read(&m, 0xfe00, 160)),
                (
                    "video",
                    vec![
                        m.read_debug(0xff40),
                        m.read_debug(0xff42),
                        m.read_debug(0xff43),
                    ],
                ),
            ] {
                write(format!("{path}.{suffix}"), bytes)?;
            }
            write(
                format!("{path}.cpu"),
                format!(
                    "{:04X} {:04X} {:02X} {:02X}",
                    m.cpu.registers.pc,
                    m.cpu.registers.sp,
                    m.cartridge.mbc1_bank_low,
                    m.cartridge.mbc1_bank_high
                ),
            )?;
            png(&m, format!("{path}.png"))?;
        }
    }
    println!(
        "{boots} {} {} {:?}",
        last + 1,
        trace.lock().unwrap().violations.len(),
        m.mode
    );
    Ok(())
}
fn game_frame(m: &mut Machine, abort: u16) -> Result {
    frame(m)?;
    if !m.boot_rom_mapped() && m.cpu.registers.pc >= abort && m.cpu.registers.pc < abort + 3 {
        return Err(format!(
            "Runtime fault {}, SP={:04X}",
            m.cpu.registers.a, m.cpu.registers.sp
        ));
    }
    Ok(())
}

pub fn showcase(args: &[String]) -> Result {
    let mut m = load(&args[0])?;
    boot(&mut m)?;
    let trace = Hook::attach(
        &mut m,
        TraceOptions {
            video: true,
            violation_trace: true,
            ..Default::default()
        },
    );
    let mut boots = 0;
    while m.boot_rom_mapped() && boots < 600 {
        boots += 1;
        frame(&mut m)?;
    }
    if m.boot_rom_mapped() {
        return Err(format!(
            "Boot did not hand off: PC={:04X}",
            m.cpu.registers.pc
        ));
    }
    let mut startup = 0;
    while (m.read_debug(0xff40) != 0xf3 || m.read_debug(0xff43) < 2) && startup < 2000 {
        startup += 1;
        frame(&mut m)?;
    }
    if startup >= 2000 {
        return Err("Showcase did not start".into());
    }
    if args.get(1).is_some_and(|s| s == "timeline") {
        let mut timeline =
            std::fs::File::create(format!("{}.timeline", args[0])).map_err(|e| e.to_string())?;
        for index in 0..6000 {
            frame(&mut m)?;
            let digits = read(&m, 0x9c06, 5);
            for bytes in [
                &digits[..],
                &read(&m, 0xfe00, 16),
                &read(&m, 0xff47, 2),
                &m.ppu.bg_palette.raw_data()[..8],
                &m.ppu.obj_palette.raw_data()[..8],
            ] {
                timeline.write_all(bytes).map_err(|e| e.to_string())?;
            }
            let mut score = 0;
            for digit in digits {
                if digit == 16 {
                    continue;
                }
                if !(32..=41).contains(&digit) {
                    score = -1;
                    break;
                }
                score = score * 10 + (digit - 32) as i32;
            }
            if score >= 80 * 17 {
                println!(
                    "{boots} {startup} {} {}",
                    index + 1,
                    trace.lock().unwrap().violations.len()
                );
                return Ok(());
            }
        }
        return Err("Showcase timeline did not complete".into());
    }
    let mut scroll = m.read_debug(0xff43);
    let mut oam = read(&m, 0xfe00, 160);
    let mut scroll_changes = 0;
    let mut sprite_changes = 0;
    let mut hud = Vec::new();
    for _ in 0..120 {
        frame(&mut m)?;
        hud.extend(read(&m, 0x9c06, 5));
        let current = m.read_debug(0xff43);
        let sprites = read(&m, 0xfe00, 160);
        if current != scroll {
            scroll_changes += 1;
        }
        if sprites != oam {
            sprite_changes += 1;
        }
        scroll = current;
        oam = sprites;
    }
    for _ in 0..1000 {
        if !m.oam_dma.is_bus_locking {
            break;
        }
        m.step_instruction();
    }
    if m.oam_dma.is_bus_locking {
        return Err("OAM transfer did not complete".into());
    }
    write(format!("{}.hud", args[0]), hud)?;
    let mut snapshot = read(&m, 0x8000, 32);
    snapshot.extend(read(&m, 0x9800, 1024));
    snapshot.extend(read(&m, 0x9c00, 1024));
    snapshot.extend(read(&m, 0xfe00, 160));
    write(format!("{}.snapshot", args[0]), snapshot)?;
    write(format!("{}.rgba", args[0]), &m.ppu.framebuffer)?;
    png(&m, format!("{}.png", args[0]))?;
    println!(
        "{boots} {startup} {scroll_changes} {sprite_changes} {}",
        trace.lock().unwrap().violations.len()
    );
    Ok(())
}

pub fn cube(args: &[String]) -> Result {
    let mut m = load(&args[0])?;
    boot(&mut m)?;
    let first = args[1].parse::<usize>().map_err(|e| e.to_string())?;
    let last = args[2].parse::<usize>().map_err(|e| e.to_string())?;
    let abort = number(&args[3])?;
    let managed = number(&args[4])?;
    let raw = number(&args[5])?;
    let raster = args.get(6).is_some_and(|s| s == "racing-beam");
    let trace = Hook::attach(
        &mut m,
        TraceOptions {
            video: true,
            cube: true,
            scroll: raster,
            ..Default::default()
        },
    );
    let mut boots = 0;
    while m.boot_rom_mapped() && boots < 600 {
        boots += 1;
        frame(&mut m)?;
    }
    let mut frame_index = 0;
    let mut previous = m.ppu.framebuffer.clone();
    let mut changes = Vec::new();
    let mut first_pc = 0;
    let mut first_bank = 0;
    let mut profile = BTreeMap::<i32, u64>::new();
    let mut profiling = std::env::var("KOH_CUBE_PROFILE").as_deref() == Ok("1");
    if !m.boot_rom_mapped() {
        while frame_index < last {
            if profiling {
                m.frame_system_ticks = 0;
                while m.frame_system_ticks < Machine::SYSTEM_TICKS_PER_FRAME {
                    let pc = m.cpu.registers.pc;
                    let bank = if pc < 0x4000 {
                        0
                    } else {
                        m.cartridge.current_rom_bank()
                    };
                    let before = m.cpu.total_tcycles;
                    m.step_instruction();
                    *profile
                        .entry(((bank as i32) << 16) | pc as i32)
                        .or_default() += m.cpu.total_tcycles - before;
                }
            } else {
                frame(&mut m)?;
            }
            frame_index += 1;
            if m.ppu.framebuffer != previous {
                changes.push(frame_index);
                previous.clone_from(&m.ppu.framebuffer);
            }
            if m.cpu.registers.pc >= abort && m.cpu.registers.pc < abort + 3 {
                break;
            }
            if frame_index == first || frame_index == last {
                write(
                    format!("{}.{frame_index}.rgba", args[0]),
                    &m.ppu.framebuffer,
                )?;
            }
            if frame_index == first {
                first_pc = m.cpu.registers.pc;
                first_bank = m.cartridge.current_rom_bank();
                profiling = false;
            }
        }
    }
    if !profile.is_empty() {
        let mut bytes = Vec::new();
        for (pc, cycles) in profile {
            bytes.extend(pc.to_le_bytes());
            bytes.extend(cycles.to_le_bytes());
        }
        write(format!("{}.profile", args[0]), bytes)?;
    }
    png(&m, format!("{}.png", args[0]))?;
    let t = trace.lock().unwrap();
    let mut bytes = Vec::new();
    for &(ly, dot, value) in &t.dma_starts {
        bytes.push(ly);
        bytes.extend(dot.to_le_bytes());
        bytes.push(value);
    }
    write(format!("{}.gdma", args[0]), bytes)?;
    if raster {
        let mut bytes = Vec::new();
        for &(ticks, ly, value) in &t.scroll_lines {
            bytes.extend(ticks.to_le_bytes());
            bytes.extend([ly, value]);
        }
        write(format!("{}.scroll-lines", args[0]), bytes)?;
        let mut bytes = Vec::new();
        for &(ticks, ly, dot, mode, value) in &t.scroll_writes {
            bytes.extend(ticks.to_le_bytes());
            bytes.push(ly);
            bytes.extend(dot.to_le_bytes());
            bytes.extend([mode, value]);
        }
        write(format!("{}.scroll-writes", args[0]), bytes)?;
    }
    let raw_start = (0xe000i32 - word(&m, raw) as i32 + 15) & !15;
    eprintln!(
        "rawNZ={} vramNZ={} bank={:02X} palette={} map={}",
        (raw_start..0xe000)
            .filter(|&a| m.read_debug(a as u16) != 0)
            .count(),
        m.vram[..0x1800].iter().filter(|&&b| b != 0).count(),
        m.read_debug(0xff70),
        hex(&m.ppu.bg_palette.raw_data()[..8]),
        hex(&m.vram[0x1800 + 4 * 32 + 4..0x1800 + 4 * 32 + 16])
    );
    eprintln!(
        "frame-changes {}",
        changes
            .iter()
            .map(usize::to_string)
            .collect::<Vec<_>>()
            .join(",")
    );
    println!(
        "{boots} {frame_index} {:04X} {:02X} {:04X} {} {} {:02X} {} {} {first_pc:04X} {first_bank}",
        m.cpu.registers.pc,
        m.cpu.registers.a,
        m.cpu.registers.sp,
        word(&m, managed),
        word(&m, raw),
        m.read_debug(0xff40),
        t.violations.len(),
        m.cartridge.current_rom_bank()
    );
    Ok(())
}
