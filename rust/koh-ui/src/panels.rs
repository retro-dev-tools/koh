use super::*;
use gpui_kit::component::{
    input::Input,
    tab::{Tab, TabBar},
};
use koh_ui::{
    engine::Color,
    inspector::{memory_rows, parse_address},
};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum Inspector {
    Cpu,
    Palettes,
    Tiles,
    Memory,
}

impl Inspector {
    const ALL: [Self; 4] = [Self::Cpu, Self::Palettes, Self::Tiles, Self::Memory];
    fn index(self) -> usize {
        Self::ALL.iter().position(|item| *item == self).unwrap()
    }
}

impl Desktop {
    pub(super) fn toggle_inspector(&mut self) {
        self.show_inspector = !self.show_inspector;
        self.request_inspection();
    }

    fn request_inspection(&self) {
        if let Some(engine) = &self.engine {
            engine.inspect(
                self.show_inspector && self.inspector != Inspector::Cpu,
                self.memory_address,
            );
        }
    }

    pub(super) fn jump_memory(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(address) = parse_address(&self.memory_input.read(cx).value()) {
            self.set_memory_address(address, window, cx);
        } else {
            self.memory_error = Some("Use 1–4 hexadecimal digits (0000–FFFF).".into());
            cx.notify();
        }
    }

    pub(super) fn set_memory_address(
        &mut self,
        address: u16,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.memory_address = address;
        self.memory_error = None;
        self.memory_input.update(cx, |input, cx| {
            input.set_value(format!("{address:04X}"), window, cx)
        });
        self.request_inspection();
        self.memory_focus.focus(window, cx);
        cx.notify();
    }

    pub(super) fn inspector_panel(
        &mut self,
        width: f32,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let content = match self.inspector {
            Inspector::Cpu => self.cpu_panel(cx).into_any_element(),
            Inspector::Palettes => self.palette_panel(cx).into_any_element(),
            Inspector::Tiles => self.tile_panel(window, cx),
            Inspector::Memory => self.memory_panel(cx),
        };
        div()
            .id("inspector-panel")
            .w(px(width))
            .h_full()
            .min_h_0()
            .flex_shrink_0()
            .flex()
            .flex_col()
            .border_l_1()
            .border_color(cx.theme().border)
            .child(
                TabBar::new("inspector-tabs")
                    .selected_index(self.inspector.index())
                    .children([
                        Tab::new().label("CPU"),
                        Tab::new().label("Palettes"),
                        Tab::new().label("Tiles"),
                        Tab::new().label("Memory"),
                    ])
                    .on_click(cx.listener(|this, index, _, cx| {
                        if let Some(inspector) = Inspector::ALL.get(*index) {
                            this.inspector = *inspector;
                            this.request_inspection();
                            cx.notify();
                        }
                    })),
            )
            .child(
                div()
                    .id("inspector-scroll")
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scroll()
                    .child(content),
            )
            .into_any_element()
    }

    fn palette_panel(&self, cx: &Context<Self>) -> Div {
        let mut panel = div().flex().flex_col().gap_4().p_4();
        let Some(palettes) = self.snapshot.as_ref().and_then(|s| s.palettes.as_ref()) else {
            return panel.child("Open a ROM to inspect palettes.");
        };
        if palettes.is_cgb {
            panel = panel.child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child("CGB · 8 background + 8 object palettes"),
            );
            for (name, colors) in [("BG", &palettes.bg_colors), ("OBJ", &palettes.obj_colors)] {
                for (index, colors) in colors.as_chunks::<4>().0.iter().enumerate() {
                    panel = panel.child(palette_row(&format!("{name} {index}"), colors, cx));
                }
            }
        } else {
            panel = panel
                .child(
                    div()
                        .text_sm()
                        .font_family(cx.theme().mono_font_family.clone())
                        .child(format!(
                            "BGP {:02X}  OBP0 {:02X}  OBP1 {:02X}",
                            palettes.bgp, palettes.obp0, palettes.obp1
                        )),
                )
                .child(palette_row("Background", &palettes.bg_colors, cx))
                .child(palette_row("Object 0", &palettes.obj_colors[..4], cx))
                .child(palette_row("Object 1", &palettes.obj_colors[4..], cx));
        }
        panel
    }

    fn tile_panel(&mut self, window: &mut Window, cx: &mut Context<Self>) -> AnyElement {
        let dpi = window.scale_factor();
        let scale = (2. * dpi).floor().max(1.) as u32;
        let vram = self.snapshot.as_ref().and_then(|s| s.vram.as_ref());
        if let Some(vram) = vram {
            self.tiles
                .refresh(&vram.rgba, vram.width, vram.height, scale, window, cx);
        }
        let ready = vram.is_some();
        let height = vram.map_or(0, |vram| vram.height);
        div()
            .id("tile-atlas")
            .role(Role::Image)
            .aria_label(if !ready {
                "No ROM loaded"
            } else if height == 384 {
                "VRAM tiles: 384 per bank, bank 0 above bank 1"
            } else {
                "VRAM tiles: 384 in bank 0"
            })
            .flex()
            .flex_col()
            .gap_3()
            .p_4()
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child("384 tiles per bank · $8000–$97FF"),
            )
            .child(div().text_sm().child(if height == 384 {
                "Bank 0 above · Bank 1 below"
            } else {
                "Bank 0"
            }))
            .children(self.tiles.image.clone().filter(|_| ready).map(|image| {
                img(image)
                    .w(px(128. * scale as f32 / dpi))
                    .h(px(height as f32 * scale as f32 / dpi))
            }))
            .children((!ready).then(|| div().text_sm().child("Open a ROM to inspect tiles.")))
            .into_any_element()
    }

    fn memory_panel(&self, cx: &mut Context<Self>) -> AnyElement {
        let loaded = self.snapshot.as_ref().is_some_and(|s| s.rom_path.is_some());
        let mut rows = div()
            .flex()
            .flex_col()
            .gap_1()
            .min_w(px(530.))
            .font_family(cx.theme().mono_font_family.clone())
            .text_size(px(12.))
            .child(
                div()
                    .text_color(cx.theme().muted_foreground)
                    .child("      00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII"),
            );
        if let Some(memory) = self.snapshot.as_ref().and_then(|s| s.memory.as_ref()) {
            for (index, row) in memory_rows(memory).into_iter().enumerate() {
                rows = rows.child(
                    div()
                        .id(SharedString::from(format!("memory-row-{index}")))
                        .role(Role::ListItem)
                        .aria_label(row.clone())
                        .child(row),
                );
            }
        } else {
            rows = rows.child("Open a ROM to inspect memory.");
        }
        div()
            .flex()
            .flex_col()
            .gap_3()
            .p_4()
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(
                        Input::new(&self.memory_input)
                            .id("memory-address")
                            .aria_label("Memory address in hexadecimal")
                            .w(px(100.))
                            .disabled(!loaded),
                    )
                    .child(
                        Button::new("memory-go")
                            .label("Go")
                            .disabled(!loaded)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.jump_memory(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("memory-prev")
                            .label("−100")
                            .disabled(!loaded)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.set_memory_address(
                                    this.memory_address.wrapping_sub(256),
                                    window,
                                    cx,
                                )
                            })),
                    )
                    .child(
                        Button::new("memory-next")
                            .label("+100")
                            .disabled(!loaded)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.set_memory_address(
                                    this.memory_address.wrapping_add(256),
                                    window,
                                    cx,
                                )
                            })),
                    ),
            )
            .children(self.memory_error.as_ref().map(|error| {
                div()
                    .id("memory-error")
                    .role(Role::Alert)
                    .aria_label(error.clone())
                    .text_sm()
                    .text_color(cx.theme().danger)
                    .child(error.clone())
            }))
            .child(
                div()
                    .text_xs()
                    .text_color(cx.theme().muted_foreground)
                    .child("Hex address · PgUp/PgDn ±100 · Home 0000"),
            )
            .child(
                div()
                    .id("memory-rows")
                    .role(Role::List)
                    .aria_label("Memory bytes in hexadecimal and ASCII")
                    .track_focus(&self.memory_focus)
                    .overflow_x_scroll()
                    .on_mouse_down(
                        MouseButton::Left,
                        cx.listener(|this, _, window, cx| this.memory_focus.focus(window, cx)),
                    )
                    .child(rows),
            )
            .into_any_element()
    }
}

fn palette_row(name: &str, colors: &[Color], cx: &Context<Desktop>) -> Div {
    div()
        .flex()
        .flex_col()
        .gap_2()
        .child(div().text_sm().child(name.to_owned()))
        .child(
            div()
                .flex()
                .gap_3()
                .children(colors.iter().enumerate().map(|(index, color)| {
                    let packed =
                        (u32::from(color.r) << 16) | (u32::from(color.g) << 8) | u32::from(color.b);
                    let hex = format!("{packed:06X}");
                    div()
                        .flex()
                        .flex_col()
                        .gap_1()
                        .child(
                            div()
                                .id(SharedString::from(format!("{name}-color-{index}")))
                                .role(Role::Image)
                                .aria_label(format!("{name} color {index}: #{hex}"))
                                .w(px(48.))
                                .h(px(28.))
                                .rounded_sm()
                                .bg(rgb(packed))
                                .border_1()
                                .border_color(cx.theme().border),
                        )
                        .child(
                            div()
                                .text_xs()
                                .font_family(cx.theme().mono_font_family.clone())
                                .child(hex),
                        )
                })),
        )
}
