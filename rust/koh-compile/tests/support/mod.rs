use std::{
    path::{Path, PathBuf},
    process::Command,
};

pub struct Host {
    pub directory: tempfile::TempDir,
    pub dll: PathBuf,
}

pub fn dotnet() -> Command {
    Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
}

pub fn build_host() -> Host {
    build_fixture_host("EmulatorHost")
}

pub fn build_fixture_host(name: &str) -> Host {
    let directory = tempfile::Builder::new()
        .prefix("koh emulator execution ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures")
        .join(name);
    let dll = fixture.join("bin/Release/net10.0/EmulatorHost.dll");
    let stamp = fixture.join("obj/koh-nextest-host-run");
    let run = std::env::var("NEXTEST_RUN_ID")
        .ok()
        .filter(|_| std::env::var("NEXTEST_TEST_GROUP").as_deref() == Ok("roslyn"));
    if run.as_ref().is_some_and(|run| {
        dll.is_file() && std::fs::read_to_string(&stamp).ok().as_ref() == Some(run)
    }) {
        return Host { directory, dll };
    }
    eprintln!("Build execution host {name}");
    let output = dotnet()
        .env("CARGO_BUILD_JOBS", "1")
        .arg("build")
        .arg(fixture.join("EmulatorHost.csproj"))
        .args(["-c", "Release", "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    if let Some(run) = run {
        std::fs::write(stamp, run).unwrap();
    }
    Host { directory, dll }
}
