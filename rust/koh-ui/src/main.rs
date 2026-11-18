use gpui_kit::{
    component::{ActiveTheme, Disableable, Theme, button::*},
    prelude::*,
    *,
};
use koh_ui::{
    engine::{self, Engine, Snapshot},
    pixels::nearest_bgra,
};
use serde_json::{Value, json};
use std::{ffi::OsString, path::Path, sync::Arc, time::Duration};

struct Desktop {
    engine: Option<Engine>,
    snapshot: Option<Snapshot>,
    status: String,
    error: bool,
    focus: FocusHandle,
    open_focus: FocusHandle,
    show_inspector: bool,
    lcd: Option<Arc<RenderImage>>,
    lcd_scale: u32,
    lcd_dirty: bool,
    _subscriptions: Vec<Subscription>,
}

impl Desktop {
    fn new(arguments: Vec<OsString>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let (engine, status, error) = match engine::executable()
            .and_then(|path| Engine::spawn(&path, &arguments))
        {
            Ok(engine) => (Some(engine), "Open a ROM to start".into(), false),
            Err(error) => {
                eprintln!("[koh-ui] {error}");
                (
                    None,
                    "The emulator engine is unavailable. Reinstall the Koh desktop package.".into(),
                    true,
                )
            }
        };
        let focus = cx.focus_handle();
        focus.focus(window, cx);
        let subscriptions = vec![
            cx.observe_window_activation(window, |this, window, _| {
                if !window.is_window_active() {
                    this.send(json!({"command": "releaseInput"}));
                }
            }),
            cx.on_blur(&focus, window, |this, _, _| {
                this.send(json!({"command": "releaseInput"}));
            }),
            cx.on_app_quit(|this, _| {
                this.engine.take();
                async {}
            }),
            cx.observe_window_appearance(window, |_, window, cx| {
                Theme::sync_system_appearance(Some(window), cx)
            }),
        ];
        cx.spawn(async move |view, cx| {
            loop {
                cx.background_executor()
                    .timer(Duration::from_millis(16))
                    .await;
                if view
                    .update(cx, |this, cx| {
                        let Some(engine) = &this.engine else {
                            return;
                        };
                        let update = engine.take_update();
                        let mut changed = false;
                        if let Some(snapshot) = update.snapshot {
                            let old = this.snapshot.as_ref();
                            if old.is_none() && snapshot.rom_path.is_some() {
                                this.status = "ROM loaded".into();
                            }
                            this.lcd_dirty |= old.is_none_or(|old| {
                                old.frame != snapshot.frame
                                    || old.rom_path != snapshot.rom_path
                                    || old.cpu.as_ref().map(|c| c.total_t_cycles)
                                        != snapshot.cpu.as_ref().map(|c| c.total_t_cycles)
                            });
                            this.snapshot = Some(snapshot);
                            changed = true;
                        }
                        if let Some(message) = update.message {
                            match message {
                                Ok(command) => {
                                    this.error = false;
                                    this.status = match command.as_str() {
                                        "load" => "ROM loaded",
                                        "reset" => "Reset",
                                        "pause" => "Paused",
                                        "resume" => "Running",
                                        "step" => "Stepped one instruction",
                                        "save" => "State saved",
                                        "restore" => "State restored",
                                        _ => "",
                                    }
                                    .into();
                                    if ["load", "reset", "restore"].contains(&command.as_str()) {
                                        this.lcd_dirty = true;
                                    }
                                }
                                Err(error) => {
                                    this.status = error;
                                    this.error = true;
                                }
                            }
                            changed = true;
                        }
                        if changed {
                            cx.notify();
                        }
                    })
                    .is_err()
                {
                    break;
                }
            }
        })
        .detach();
        Self {
            engine,
            snapshot: None,
            status,
            error,
            focus,
            open_focus: cx.focus_handle(),
            show_inspector: true,
            lcd: None,
            lcd_scale: 0,
            lcd_dirty: true,
            _subscriptions: subscriptions,
        }
    }

    fn send(&mut self, request: Value) {
        if !self
            .engine
            .as_ref()
            .is_some_and(|engine| engine.send(request))
        {
            self.status = "The emulator connection is closed. Relaunch Koh to reconnect.".into();
            self.error = true;
        }
    }

    fn open(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.send(json!({"command": "releaseInput"}));
        let picker = cx.prompt_for_paths(PathPromptOptions {
            files: true,
            directories: false,
            multiple: false,
            prompt: Some("Open a Game Boy ROM".into()),
        });
        cx.spawn_in(window, async move |view, cx| {
            let result = picker.await;
            let _ = view.update_in(cx, |this, window, cx| {
                match result {
                    Ok(Ok(Some(paths))) => {
                        if let Some(path) = paths.first() {
                            if let Some(path) = path.to_str() {
                                this.status = "Opening ROM…".into();
                                this.send(json!({"command": "load", "path": path}));
                            } else {
                                this.status = "The ROM filename is not valid UTF-8. Rename the file and try again.".into();
                                this.error = true;
                            }
                        }
                    }
                    Ok(Ok(None)) => {}
                    Ok(Err(error)) => {
                        this.status = format!("Could not open the file picker: {error}");
                        this.error = true;
                    }
                    Err(_) => {
                        this.status =
                            "The file picker closed unexpectedly. Try Open ROM again.".into();
                        this.error = true;
                    }
                }
                this.focus.focus(window, cx);
                cx.notify();
            });
        })
        .detach();
    }

    fn key_down(&mut self, event: &KeyDownEvent, window: &mut Window, cx: &mut Context<Self>) {
        let key = event.keystroke.key.as_str();
        if key == "escape" {
            if self.focus.is_focused(window) {
                self.open_focus.focus(window, cx);
            } else {
                self.focus.focus(window, cx);
            }
            cx.stop_propagation();
            return;
        }
        if !self.focus.is_focused(window)
            || event.is_held
            || event.keystroke.modifiers.control
            || event.keystroke.modifiers.alt
            || event.keystroke.modifiers.platform
        {
            return;
        }
        match key {
            "tab" => self.send(json!({"command": "fastForward", "enabled": true})),
            "p" => self.send(json!({"command": if self.snapshot.as_ref().is_none_or(|s| s.paused) { "resume" } else { "pause" }})),
            "r" => self.send(json!({"command": "reset"})),
            "f5" => self.send(json!({"command": "save"})),
            "f9" => self.send(json!({"command": "restore"})),
            "d" => { self.show_inspector = !self.show_inspector; cx.notify(); },
            key => if let Some(button) = joypad(key) { self.send(json!({"command": "joypad", "button": button, "down": true})); }
                else { return; },
        }
        cx.stop_propagation();
    }

    fn key_up(&mut self, event: &KeyUpEvent) {
        let key = event.keystroke.key.as_str();
        if key == "tab" {
            self.send(json!({"command": "fastForward", "enabled": false}));
        } else if let Some(button) = joypad(key) {
            self.send(json!({"command": "joypad", "button": button, "down": false}));
        }
    }

    fn cpu_panel(&self, cx: &Context<Self>) -> Div {
        let mut panel = div()
            .flex()
            .flex_col()
            .gap_3()
            .p_4()
            .w(px(256.))
            .flex_shrink_0()
            .border_l_1()
            .border_color(cx.theme().border)
            .child(
                div()
                    .text_sm()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child("CPU"),
            );
        if let Some(cpu) = self.snapshot.as_ref().and_then(|s| s.cpu.as_ref()) {
            for (name, value) in [
                ("PC", format!("{:04X}", cpu.pc)),
                ("SP", format!("{:04X}", cpu.sp)),
                ("A", format!("{:02X}", cpu.a)),
                ("F", format!("{:02X}", cpu.f)),
                ("BC", format!("{:04X}", cpu.bc)),
                ("DE", format!("{:04X}", cpu.de)),
                ("HL", format!("{:04X}", cpu.hl)),
            ] {
                panel = panel.child(
                    div()
                        .flex()
                        .justify_between()
                        .font_family(cx.theme().mono_font_family.clone())
                        .child(div().text_color(cx.theme().muted_foreground).child(name))
                        .child(value),
                );
            }
            panel = panel
                .child(
                    div()
                        .text_sm()
                        .font_family(cx.theme().mono_font_family.clone())
                        .child(format!(
                            "Z {}   N {}   H {}   C {}",
                            u8::from(cpu.flag_z),
                            u8::from(cpu.flag_n),
                            u8::from(cpu.flag_h),
                            u8::from(cpu.flag_c)
                        )),
                )
                .child(
                    div()
                        .text_xs()
                        .text_color(cx.theme().muted_foreground)
                        .child(format!("{} T-cycles", cpu.total_t_cycles)),
                );
        } else {
            panel = panel.child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child("Open a ROM to inspect registers."),
            );
        }
        panel
    }
}

fn joypad(key: &str) -> Option<&'static str> {
    Some(match key {
        "up" => "Up",
        "down" => "Down",
        "left" => "Left",
        "right" => "Right",
        "x" => "A",
        "z" => "B",
        "enter" => "Start",
        _ => return None,
    })
}

impl Render for Desktop {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let loaded = self.snapshot.as_ref().is_some_and(|s| s.rom_path.is_some());
        let paused = self.snapshot.as_ref().is_none_or(|s| s.paused);
        let connected = self.engine.as_ref().is_some_and(Engine::is_alive);
        let viewport = window.viewport_size();
        let dpi = window.scale_factor();
        let available_width =
            (f32::from(viewport.width) - if self.show_inspector { 256. } else { 0. } - 64.)
                .max(160.);
        let available_height = (f32::from(viewport.height) - 184.).max(144.);
        let scale = ((available_width * dpi / 160.)
            .min(available_height * dpi / 144.)
            .floor() as u32)
            .max(1);
        if (self.lcd_dirty || scale != self.lcd_scale)
            && let Some(snapshot) = &self.snapshot
            && let Some(image) = nearest_bgra(&snapshot.framebuffer, 160, 144, scale)
        {
            let next = Arc::new(RenderImage::new(vec![image::Frame::new(image)]));
            if let Some(previous) = self.lcd.replace(next) {
                cx.drop_image(previous, Some(window));
            }
            self.lcd_dirty = false;
            self.lcd_scale = scale;
        }
        let lcd = if loaded {
            div()
                .id("lcd")
                .role(Role::Image)
                .aria_label("Game Boy display")
                .on_mouse_down(
                    MouseButton::Left,
                    cx.listener(|this, _, window, cx| this.focus.focus(window, cx)),
                )
                .children(self.lcd.clone().map(|image| {
                    img(image)
                        .w(px(160. * scale as f32 / dpi))
                        .h(px(144. * scale as f32 / dpi))
                }))
                .into_any_element()
        } else {
            div()
                .flex()
                .flex_col()
                .items_center()
                .gap_3()
                .p_6()
                .child(
                    div()
                        .text_xl()
                        .font_weight(FontWeight::SEMIBOLD)
                        .child("Open a Game Boy ROM"),
                )
                .child(
                    div()
                        .text_sm()
                        .text_color(cx.theme().muted_foreground)
                        .child("Choose a .gb or .gbc file to start playing."),
                )
                .child(
                    Button::new("open-empty")
                        .primary()
                        .label("Open ROM")
                        .disabled(!connected)
                        .on_click(cx.listener(Self::open_click)),
                )
                .into_any_element()
        };
        let path = self
            .snapshot
            .as_ref()
            .and_then(|s| s.rom_path.as_ref())
            .and_then(|p| Path::new(p).file_name())
            .map(|s| s.to_string_lossy().into_owned())
            .unwrap_or_else(|| "No ROM loaded".into());
        div().id("desktop").track_focus(&self.focus).size_full().flex().flex_col()
            .bg(cx.theme().background).text_color(cx.theme().foreground).font_family(cx.theme().font_family.clone())
            .on_key_down(cx.listener(Self::key_down)).on_key_up(cx.listener(|this, event, _, _| this.key_up(event)))
            .child(div().flex().items_center().flex_wrap().gap_2().p_3().border_b_1().border_color(cx.theme().border)
                .child(div().text_lg().font_weight(FontWeight::BOLD).mr_3().child("Koh"))
                .child(Button::new("open").primary().label("Open ROM").track_focus(&self.open_focus).disabled(!connected).on_click(cx.listener(Self::open_click)))
                .child(Button::new("pause").label(if paused { "Resume" } else { "Pause" }).tooltip("Pause or resume · P").disabled(!loaded || !connected)
                    .on_click(cx.listener(|this, _, _, _| { let paused = this.snapshot.as_ref().is_none_or(|s| s.paused); this.send(json!({"command": if paused { "resume" } else { "pause" }})); })))
                .child(Button::new("reset").label("Reset").tooltip("Reset · R").disabled(!loaded || !connected).on_click(cx.listener(|this, _, _, _| this.send(json!({"command":"reset"})))))
                .child(Button::new("step").label("Step").disabled(!loaded || !paused || !connected).on_click(cx.listener(|this, _, _, _| this.send(json!({"command":"step"})))))
                .child(Button::new("save").label("Save").tooltip("Save state · F5").disabled(!loaded || !connected).on_click(cx.listener(|this, _, _, _| this.send(json!({"command":"save"})))))
                .child(Button::new("restore").label("Load").tooltip("Load state · F9").disabled(!loaded || !connected).on_click(cx.listener(|this, _, _, _| this.send(json!({"command":"restore"})))))
                .child(Button::new("inspector").label("Inspector").on_click(cx.listener(|this, _, _, cx| { this.show_inspector = !this.show_inspector; cx.notify(); }))))
            .child(div().flex().flex_1().min_h_0()
                .child(div().flex().flex_col().flex_1().min_w_0().items_center().justify_center().gap_4().p_4()
                    .child(div().p_3().rounded_lg().bg(rgb(0x17212d)).child(lcd))
                    .child(div().text_xs().text_color(cx.theme().muted_foreground).child("Arrows move · X A · Z B · Enter Start · Tab fast-forward · Esc controls")))
                .children(self.show_inspector.then(|| self.cpu_panel(cx))))
            .child(div().id("status").role(Role::Status).flex().flex_col().gap_1().px_4().py_2().border_t_1().border_color(cx.theme().border)
                .child(div().flex().justify_between().gap_3().text_sm().child(path)
                    .child(if loaded { format!("{} · Frame {}{}", if paused { "Paused" } else { "Running" }, self.snapshot.as_ref().map_or(0, |s| s.frame), if self.snapshot.as_ref().is_some_and(|s| s.fast_forward) { " · Fast-forward" } else { "" }) } else { "Ready".into() }))
                .child(div().text_xs().text_color(if self.error { cx.theme().danger } else { cx.theme().muted_foreground }).child(self.status.clone())))
    }
}

impl Desktop {
    fn open_click(&mut self, _: &ClickEvent, window: &mut Window, cx: &mut Context<Self>) {
        self.open(window, cx);
    }
}

fn main() {
    let arguments = std::env::args_os().skip(1).collect::<Vec<_>>();
    if arguments.iter().any(|a| {
        a == "--headless"
            || a.to_string_lossy().starts_with("--screenshot=")
            || a.to_string_lossy().starts_with("--mode3-report")
    }) {
        let result = engine::executable()
            .and_then(|path| std::process::Command::new(path).args(arguments).status());
        std::process::exit(match result {
            Ok(status) => status.code().unwrap_or(1),
            Err(error) => {
                eprintln!("[koh-ui] {error}");
                1
            }
        });
    }
    gpui_kit::application()
        .with_quit_mode(QuitMode::LastWindowClosed)
        .run(move |cx| {
            gpui_kit::init(cx);
            gpui_kit::open_window(
                WindowOptions {
                    window_bounds: Some(WindowBounds::Windowed(Bounds::centered(
                        None,
                        size(px(1040.), px(720.)),
                        cx,
                    ))),
                    window_min_size: Some(size(px(680.), px(480.))),
                    titlebar: Some(TitlebarOptions {
                        title: Some("Koh Emulator".into()),
                        ..Default::default()
                    }),
                    ..Default::default()
                },
                cx,
                |window, cx| {
                    Theme::sync_system_appearance(Some(window), cx);
                    cx.new(|cx| Desktop::new(arguments, window, cx))
                },
            )
            .expect("open Koh window");
        });
}
