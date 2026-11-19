use super::*;

fn paused_host() -> Host {
    let mut host = Host::new(Options {
        headless: true,
        ..Options::default()
    });
    host.machine = Some(Machine::load_rom(&vec![0; 32768]).unwrap());
    host
}

fn write_wram(host: &mut Host, data: &str) {
    host.dap(&protocol::Frame::Request(json!({
        "seq":1,"command":"writeMemory",
        "arguments":{"memoryReference":"$C000","data":data}
    })))
    .unwrap();
}

#[test]
fn paused_commands_mutate_memory_without_advancing_cpu_or_frame() {
    let mut host = paused_host();
    let cycles = host.machine.as_ref().unwrap().cpu.total_tcycles;
    write_wram(&mut host, "Qg==");
    for _ in 0..3 {
        let (snapshot, _) = host
            .command(&json!({"command":"snapshot","debug":true,"address":0xc000}))
            .unwrap();
        assert_eq!(host.machine.as_ref().unwrap().read_debug(0xc000), 0x42);
        assert_eq!(snapshot["cpu"]["totalTCycles"], cycles);
        assert_eq!(snapshot["frame"], 0);
        assert_eq!(snapshot["paused"], true);
    }
}

#[test]
fn paused_save_restore_completes_before_returning() {
    let directory = std::env::temp_dir().join(format!("koh-paused-state-{}", std::process::id()));
    std::fs::create_dir(&directory).unwrap();
    let mut host = paused_host();
    host.path = Some(directory.join("game.gb").to_str().unwrap().into());
    write_wram(&mut host, "Qg==");
    host.command(&json!({"command":"save"})).unwrap();
    assert!(directory.join("game.gb.state").is_file());
    write_wram(&mut host, "GQ==");
    assert_eq!(host.machine.as_ref().unwrap().read_debug(0xc000), 0x19);
    let (snapshot, _) = host.command(&json!({"command":"restore"})).unwrap();
    assert_eq!(host.machine.as_ref().unwrap().read_debug(0xc000), 0x42);
    assert_eq!(snapshot["paused"], true);
    std::fs::remove_dir_all(directory).unwrap();
}
