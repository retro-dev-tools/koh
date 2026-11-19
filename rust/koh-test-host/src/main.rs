mod assembler;
mod common;
mod debugger;
mod emulator;
mod games;
mod presentation2048;
mod verify2048;

fn main() {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() {
        eprintln!("usage: koh-test-host <FixtureHost> [args...]");
        std::process::exit(2);
    }
    let name = args.remove(0);
    let result = match name.as_str() {
        "AssemblerVerify" => assembler::run(&args),
        "Verify2048" => verify2048::run(&args),
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
