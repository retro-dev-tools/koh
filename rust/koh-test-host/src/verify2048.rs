use crate::common::{Result, boot, button, frame, load, png, write};
use koh_emulator::machine::Machine;
use serde_json::{Value, json};
use std::{collections::BTreeMap, path::PathBuf};

struct Verifier {
    machine: Machine,
    output: PathBuf,
    sheet: Vec<Value>,
    animations: BTreeMap<String, Vec<String>>,
    passes: usize,
    failures: usize,
}

impl Verifier {
    fn new(machine: Machine, output: PathBuf) -> Self {
        Self {
            machine,
            output,
            sheet: vec![],
            animations: BTreeMap::new(),
            passes: 0,
            failures: 0,
        }
    }

    fn frames(&mut self, count: usize) -> Result {
        for _ in 0..count {
            frame(&mut self.machine)?;
        }
        Ok(())
    }

    fn press(&mut self, name: &str) -> Result {
        self.machine.joypad = button(name)?;
        self.frames(4)?;
        self.machine.joypad = 0;
        self.frames(8)
    }

    fn check(&mut self, condition: bool, message: impl AsRef<str>) {
        if condition {
            self.passes += 1;
            println!("PASS: {}", message.as_ref());
        } else {
            self.failures += 1;
            println!("FAIL: {}", message.as_ref());
        }
    }

    fn image(&self, name: &str) -> Result<String> {
        let path = format!("frames/{name}.png");
        png(&self.machine, self.output.join(&path))?;
        Ok(path)
    }

    fn capture(&mut self, name: &str, label: &str) -> Result {
        let path = self.image(name)?;
        self.sheet
            .push(json!({"name": name, "label": label, "path": path}));
        Ok(())
    }

    fn board(&mut self, label: &str) {
        for row in 0..4 {
            for col in 0..4 {
                let value = self.machine.read_debug(0xc000 + row * 4 + col);
                let start = 0x1800 + (4 + row as usize * 3) * 32 + 2 + col as usize * 4;
                let expected = match value {
                    0 => 1,
                    1 => 2,
                    2 => 3,
                    3 | 4 => 4,
                    5 | 6 => 5,
                    7..=9 => 6,
                    _ => 7,
                };
                for (kind, actual, expected) in [
                    ("top edge", self.machine.vram[start], 31),
                    ("bottom edge", self.machine.vram[start + 64], 32),
                    (
                        "palette",
                        self.machine.vram[start + 0x2000 + 32] & 7,
                        expected,
                    ),
                ] {
                    if actual != expected {
                        self.check(
                            false,
                            format!(
                                "{label}: cell({row},{col}) {kind}={actual}, expected {expected}"
                            ),
                        );
                    }
                }
            }
        }
    }

    fn slide(&mut self, name: &str, direction: &str, a: u16, b: u16, merged: u16) -> Result {
        for cell in 0..16 {
            self.machine.write_debug(0xc000 + cell, 0);
        }
        self.machine.write_debug(0xc000 + a, 1);
        self.machine.write_debug(0xc000 + b, 1);
        self.machine.write_debug(0xc06e, 1);
        let mut frames = vec![self.image(&format!("{name}_0"))?];
        self.machine.joypad = button(direction)?;
        self.frames(2)?;
        self.machine.joypad = 0;
        for index in 1..=12 {
            self.frames(1)?;
            frames.push(self.image(&format!("{name}_{index}"))?);
        }
        if name == "right" {
            self.sheet
                .push(json!({"label": "MID-SLIDE F4", "path": frames[4]}));
            self.sheet
                .push(json!({"label": "POST-COMMIT F9", "path": frames[9]}));
        } else {
            self.sheet
                .push(json!({"label": format!("{} F4", name.to_uppercase()), "path": frames[4]}));
            self.frames(20)?;
        }
        self.animations.insert(name.into(), frames);
        self.check(
            self.machine.read_debug(0xc000 + merged) == 2,
            format!("{name}-merge produced value 2 at cell {merged}"),
        );
        Ok(())
    }
}

pub fn run(args: &[String]) -> Result {
    let [rom, output] = args else {
        return Err("Expected <rom> <output-directory>".into());
    };
    let output = PathBuf::from(output);
    match std::fs::remove_file(output.join("manifest.json")) {
        Ok(()) => (),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => (),
        Err(error) => return Err(error.to_string()),
    }
    let mut machine = load(rom)?;
    boot(&mut machine)?;
    for _ in 0..600 {
        if !machine.boot_rom_mapped() {
            break;
        }
        frame(&mut machine)?;
    }
    if machine.boot_rom_mapped() {
        return Err("Boot did not complete after 600 frames".into());
    }
    std::fs::create_dir_all(output.join("frames")).map_err(|error| error.to_string())?;
    let mut v = Verifier::new(machine, output);
    v.frames(120)?;
    v.capture("title", "TITLE")?;
    v.check(
        v.machine.read_debug(0xc06e) == 0,
        "In TITLE state after boot",
    );
    v.check(v.machine.vram[0x1800] == 2, "HUD BEST label present");
    v.check(
        v.machine.vram[0x1800 + 2 * 32 + 10] == 39,
        "HUD divider rule on row 2",
    );
    v.press("Start")?;
    v.frames(30)?;
    v.capture("playing", "PLAYING - 2 STARTING TILES")?;
    v.check(v.machine.read_debug(0xc06e) == 1, "In PLAYING after START");
    let nonempty = (0..16)
        .filter(|cell| v.machine.read_debug(0xc000 + cell) != 0)
        .count();
    v.check(
        nonempty == 2,
        format!("Fresh board has 2 starting tiles (saw {nonempty})"),
    );
    v.board("fresh");
    let directions = ["Left", "Down", "Right", "Up"];
    v.machine.joypad = button("Left")?;
    v.frames(12)?;
    v.machine.joypad = 0;
    v.frames(20)?;
    v.board("after move 1");
    v.capture("after_move1", "AFTER MOVE 1")?;
    for index in 1..10 {
        v.press(directions[index % 4])?;
        v.frames(20)?;
        v.board(&format!("after move {}", index + 1));
    }
    v.capture("after_moves", "AFTER 10 MOVES")?;
    v.slide("right", "Right", 12, 15, 15)?;
    v.slide("down", "Down", 0, 12, 12)?;
    v.slide("up", "Up", 0, 12, 0)?;
    v.slide("left", "Left", 12, 15, 12)?;
    for (index, value) in [1, 2, 1, 2, 2, 3, 2, 1, 1, 2, 1, 3, 0, 2, 1, 3]
        .into_iter()
        .enumerate()
    {
        v.machine.write_debug(0xc000 + index as u16, value);
    }
    v.machine.write_debug(0xc06e, 1);
    v.press("Left")?;
    v.frames(60)?;
    v.capture("gameover", "GAME OVER")?;
    v.check(v.machine.read_debug(0xc06e) == 4, "Triggered GS_GAMEOVER");
    for _ in 0..2 {
        v.press("Start")?;
        v.frames(30)?;
    }
    v.machine.write_debug(0xc06f, 0);
    for cell in 0..16 {
        v.machine.write_debug(0xc000 + cell, 0);
    }
    v.machine.write_debug(0xc00c, 10);
    v.machine.write_debug(0xc00d, 10);
    v.machine.joypad = button("Left")?;
    v.frames(14)?;
    v.machine.joypad = 0;
    v.frames(20)?;
    v.capture("win", "YOU WIN")?;
    v.check(v.machine.read_debug(0xc06e) == 3, "Triggered GS_WIN");
    crate::presentation2048::render(&v.output, &v.sheet, &v.animations)?;
    write(v.output.join("manifest.json"), serde_json::to_vec_pretty(&json!({
        "sheet": v.sheet, "animations": v.animations, "passes": v.passes, "failures": v.failures,
    })).map_err(|error| error.to_string())?)?;
    if v.failures != 0 {
        return Err(format!("{} FAILURE(S), {} passes", v.failures, v.passes));
    }
    println!("ALL {} CHECKS PASSED", v.passes);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn board_checks_use_tile_and_attribute_banks() {
        let mut machine = koh_emulator::machine::Machine::load_rom(&vec![0; 0x8000]).unwrap();
        machine.wram.fill(0);
        for row in 0..4 {
            for col in 0..4 {
                let start = 0x1800 + (4 + row * 3) * 32 + 2 + col * 4;
                machine.vram[start] = 31;
                machine.vram[start + 64] = 32;
                machine.vram[start + 0x2000 + 32] = 1;
            }
        }
        let mut verifier = Verifier::new(machine, std::path::PathBuf::new());
        verifier.board("empty");
        assert_eq!(verifier.failures, 0);
        verifier.machine.vram[0x3800 + 5 * 32 + 2] = 2;
        verifier.board("bad palette");
        assert_eq!(verifier.failures, 1);
    }
}
