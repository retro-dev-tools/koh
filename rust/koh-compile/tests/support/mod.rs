use std::{
    path::{Path, PathBuf},
    process::Command,
};

pub struct Host {
    pub directory: tempfile::TempDir,
    pub program: PathBuf,
    pub native: Option<String>,
}
impl Host {
    pub fn command(&self) -> Command {
        match &self.native {
            Some(name) => {
                let mut command = Command::new(&self.program);
                command.arg(name);
                command
            }
            None => {
                let mut command = dotnet();
                command.arg(&self.program);
                command
            }
        }
    }
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
    if !matches!(name, "ManagedHost" | "SoftFloatHost") {
        static RUN: std::sync::OnceLock<String> = std::sync::OnceLock::new();
        let run = RUN.get_or_init(|| {
            std::env::var("NEXTEST_RUN_ID").unwrap_or_else(|_| {
                format!(
                    "{}-{}",
                    std::process::id(),
                    std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .unwrap()
                        .as_nanos()
                )
            })
        });
        let target = root.join("target/test-host");
        std::fs::create_dir_all(&target).unwrap();
        let lock = std::fs::File::create(target.join("build.lock")).unwrap();
        lock.lock().unwrap();
        let binary = target
            .join("release")
            .join(format!("koh-test-host{}", std::env::consts::EXE_SUFFIX));
        let stamp = target.join(format!("built-{run}"));
        if !stamp.is_file() || !binary.is_file() {
            let output = Command::new(std::env::var_os("CARGO").unwrap_or("cargo".into()))
                .current_dir(root)
                .args([
                    "build",
                    "-p",
                    "koh-test-host",
                    "--release",
                    "--locked",
                    "--target-dir",
                ])
                .arg(&target)
                .env("CARGO_BUILD_JOBS", "1")
                .output()
                .unwrap();
            assert!(
                output.status.success(),
                "{}\n{}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
            std::fs::write(&stamp, run).unwrap();
        }
        return Host {
            directory,
            program: binary,
            native: Some(name.into()),
        };
    }
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
        return Host {
            directory,
            program: dll,
            native: None,
        };
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
    Host {
        directory,
        program: dll,
        native: None,
    }
}
