mod common;
mod debugger;
mod emulator;
mod games;

fn main() {
    let mut args: Vec<String> = std::env::args().collect();
    let executable = std::path::Path::new(&args.remove(0))
        .file_stem()
        .unwrap()
        .to_string_lossy()
        .into_owned();
    let name = if executable == "koh-test-host" {
        args.remove(0)
    } else {
        executable
    };
    let result = match name.as_str() {
        "EmulatorHost" => emulator::run(&args),
        "VBlankHost" => games::vblank(&args),
        "GameHost" => games::game(&args),
        "CubeHost" => games::cube(&args),
        "ShowcaseHost" => games::showcase(&args),
        "DebuggerHost" => debugger::run(),
        _ => Err(format!("Unknown fixture host {name}")),
    };
    if let Err(error) = result {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
