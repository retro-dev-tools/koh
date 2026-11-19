use base64::{Engine, engine::general_purpose::STANDARD};
use koh_emulator::{HardwareMode, machine::Machine};
use serde_json::{Value, json};

pub fn snapshot(
    machine: Option<&Machine>,
    path: Option<&str>,
    frame: u64,
    paused: bool,
    fast_forward: bool,
    debug: bool,
    address: u16,
) -> Value {
    let cpu=machine.map(|m|{let r=&m.cpu.registers;json!({"pc":r.pc,"sp":r.sp,"a":r.a,"f":r.f,"bc":r.bc(),"de":r.de(),"hl":r.hl(),"totalTCycles":m.cpu.total_tcycles,"flagZ":r.flag_set(0x80),"flagN":r.flag_set(0x40),"flagH":r.flag_set(0x20),"flagC":r.flag_set(0x10)})});
    let framebuffer = machine.map_or_else(
        || [0x2e, 0x2e, 0x2e, 255].repeat(160 * 144),
        |m| m.ppu.framebuffer.clone(),
    );
    let palettes = machine.filter(|_| debug).map(palettes);
    let vram = machine.filter(|_| debug).map(vram);
    let memory = machine.filter(|_| debug).map(|m| {
        let bytes: Vec<u8> = (0..256)
            .map(|i| m.read_debug(address.wrapping_add(i)))
            .collect();
        json!({"baseAddress":address,"bytes":STANDARD.encode(bytes)})
    });
    json!({"protocolVersion":1,"romPath":path,"frame":frame,"paused":paused,"fastForward":fast_forward,"mode":machine.map(|m|format!("{:?}",m.mode)),"cpu":cpu,"framebuffer":STANDARD.encode(framebuffer),"palettes":palettes,"vram":vram,"memory":memory})
}
fn color(r: u8, g: u8, b: u8) -> Value {
    json!({"r":r,"g":g,"b":b})
}
fn palettes(m: &Machine) -> Value {
    let cgb = m.mode == HardwareMode::Cgb;
    let p = &m.ppu;
    let mut bg = vec![];
    let mut obj = vec![];
    if cgb {
        for palette in 0..8 {
            for slot in 0..4 {
                for (value, colors) in [
                    (p.bg_palette.get_color(palette, slot), &mut bg),
                    (p.obj_palette.get_color(palette, slot), &mut obj),
                ] {
                    let r = (value & 31) as u8;
                    let g = ((value >> 5) & 31) as u8;
                    let b = ((value >> 10) & 31) as u8;
                    colors.push(color(
                        (r << 3) | (r >> 2),
                        (g << 3) | (g >> 2),
                        (b << 3) | (b >> 2),
                    ));
                }
            }
        }
    } else {
        let shades = [
            [0x9b, 0xbc, 0x0f],
            [0x8b, 0xac, 0x0f],
            [0x30, 0x62, 0x30],
            [0x0f, 0x38, 0x0f],
        ];
        for (index, palette) in [p.bgp, p.obp0, p.obp1].into_iter().enumerate() {
            let colors = if index == 0 { &mut bg } else { &mut obj };
            for slot in 0..4 {
                let [r, g, b] = shades[((palette >> (slot * 2)) & 3) as usize];
                colors.push(color(r, g, b));
            }
        }
    }
    json!({"isCgb":cgb,"bgp":p.bgp,"obp0":p.obp0,"obp1":p.obp1,"bgColors":bg,"objColors":obj})
}
fn vram(m: &Machine) -> Value {
    let banks = if m.mode == HardwareMode::Cgb { 2 } else { 1 };
    let height = banks * 192;
    let mut rgba = vec![255; 128 * height * 4];
    let shades = [255, 192, 96, 0];
    for bank in 0..banks {
        for tile in 0..384 {
            for row in 0..8 {
                let low = m.vram[bank * 0x2000 + tile * 16 + row * 2];
                let high = m.vram[bank * 0x2000 + tile * 16 + row * 2 + 1];
                for column in 0..8 {
                    let bit = 7 - column;
                    let index = ((high >> bit) & 1) * 2 + ((low >> bit) & 1);
                    let shade = shades[index as usize];
                    let offset =
                        ((bank * 192 + tile / 16 * 8 + row) * 128 + tile % 16 * 8 + column) * 4;
                    rgba[offset..offset + 4].copy_from_slice(&[shade, shade, shade, 255]);
                }
            }
        }
    }
    json!({"rgba":STANDARD.encode(rgba),"width":128,"height":height})
}
