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
    for name in ["EmulatorHost.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(name), directory.path().join(name)).unwrap();
    }
    let output = dotnet()
        .env("CARGO_BUILD_JOBS", "1")
        .arg("build")
        .arg(directory.path().join("EmulatorHost.csproj"))
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
    let dll = directory
        .path()
        .join("bin/Release/net10.0/EmulatorHost.dll");
    Host { directory, dll }
}
