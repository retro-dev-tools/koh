set windows-shell := ["cmd.exe", "/C"]
export RUSTUP_TOOLCHAIN := "stable"

default:
    @just --list

# Format Rust and C#.
fmt:
    cargo fmt --all
    dotnet tool restore
    dotnet csharpier format .

# Check formatting and workflow/installer lint.
lint:
    cargo fmt --all -- --check
    dotnet tool restore
    dotnet csharpier check .
    actionlint -color
    shellcheck installers/unix/koh-install.sh.template

# Check libraries and binaries on the minimum supported Rust version.
msrv:
    cargo +1.88.0 check --workspace --exclude koh-ui --exclude koh-test-host --lib --bins --all-features --locked

# Publish and launch the desktop, forwarding ROM/options unchanged.
[script("python")]
[positional-arguments]
run *args:
    import os, pathlib, subprocess, sys
    subprocess.run(["dotnet", "msbuild", "build.proj", "-t:PublishEmulatorApp"], check=True)
    publish = subprocess.check_output(["dotnet", "msbuild", "build.proj", "-nologo", "-getProperty:EmulatorPublishDir"], text=True).strip()
    executable = pathlib.Path(publish) / ("koh-emulator.exe" if os.name == "nt" else "koh-emulator")
    subprocess.run([str(executable), *sys.argv[1:]], check=True)

# Build managed projects and the host's complete native package.
[script("python")]
build:
    import os, pathlib, shutil, subprocess
    def run(*args): subprocess.run(args, check=True)
    root = pathlib.Path.cwd()
    bin = root / "artifacts/bin"
    shutil.rmtree(bin, ignore_errors=True)
    run("dotnet", "msbuild", "build.proj", "-t:PublishLanguageServer", f"-p:LanguageServerPublishDir={bin}")
    run("dotnet", "msbuild", "build.proj", "-t:PublishEmulatorApp", f"-p:EmulatorPublishDir={bin}")
    rid = subprocess.check_output(["dotnet", "msbuild", "build.proj", "-nologo", "-getProperty:EmulatorRuntimeIdentifier"], text=True).strip()
    shutil.copytree(bin, root / f"artifacts/emulator/{rid}", dirs_exist_ok=True)
    # Same command and target dir as Koh.Build.Tasks/NativeBuild.targets, so the solution build reuses it.
    target = root / "target/compiler-msbuild/Release"
    run("cargo", "build", "--release", "--package", "koh-compile", "--package", "koh-link", "--locked", "--target-dir", str(target))
    suffix = ".exe" if os.name == "nt" else ""
    shutil.copy2(target / f"release/koh-compile{suffix}", bin)
    run("dotnet", "build", "Koh.Ci.slnf", "-c", "Release", "-m:1")

# Build and test native and managed components.
[script("python")]
test: build
    import os, pathlib, subprocess
    def run(*args, **kwargs): subprocess.run([str(a) for a in args], check=True, **kwargs)
    root = pathlib.Path.cwd()
    bin = root / "artifacts/bin"
    suffix = ".exe" if os.name == "nt" else ""
    os.environ["PATH"] = str(bin) + os.pathsep + os.environ["PATH"]
    for key in ("LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH"):
        os.environ[key] = str(bin) + (os.pathsep + os.environ[key] if os.environ.get(key) else "")
    os.environ.update(KOH_LSP_TEST_BINARY=str(bin / f"koh-lsp{suffix}"),
                      KOH_COMPILE=str(bin / f"koh-compile{suffix}"), KOH_LINK=str(bin / f"koh-link{suffix}"))
    run("cargo", "clippy", "-p", "koh-emulator", "-p", "koh-debugger", "-p", "koh-engine", "-p", "koh-test-host", "-p", "koh-lsp", "--all-targets", "--no-deps", "--locked", "--", "-D", "warnings")
    run("cargo", "nextest", "run", "--workspace", "--all-features", "--locked", "--no-fail-fast")
    run("cargo", "test", "--doc", "--workspace", "--all-features", "--locked")
    run("dotnet", "test", "--solution", "Koh.Ci.slnf", "-c", "Release", "--no-build", "--max-parallel-test-modules", "1")

# Verify built packages, native windows, audio and real editor/debugger use.
[script("python")]
verify:
    import hashlib, json, os, pathlib, shutil, subprocess, sys
    from PIL import Image
    def run(*args, **kwargs): subprocess.run([str(a) for a in args], check=True, **kwargs)
    root = pathlib.Path.cwd()
    bin = root / "artifacts/bin"
    notices = bin / "licenses/rust-lsp"
    for package in json.loads((notices / "manifest.json").read_text())["packages"]:
        if hashlib.sha256((notices / package["notice_file"]).read_bytes()).hexdigest() != package["notice_sha256"]:
            raise RuntimeError(f"LSP notice hash mismatch: {package['name']}")
    out = root / "artifacts/validation"
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True, exist_ok=True)
    suffix = ".exe" if os.name == "nt" else ""
    os.environ["PATH"] = str(bin) + os.pathsep + os.environ["PATH"]
    for key in ("LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH"):
        os.environ[key] = str(bin) + (os.pathsep + os.environ[key] if os.environ.get(key) else "")
    os.environ["ALSOFT_DRIVERS"] = "null"
    run("cargo", "test", "-p", "koh-engine", "--bin", "koh-engine", "openal_null_backend_warmup_reset_and_restart", "--locked", "--", "--ignored")
    run("cargo", "test", "-p", "koh-engine", "--test", "session", "audio_resume_reset_and_restore_remain_operational", "--locked", "--", "--ignored")
    run("dotnet", "msbuild", "build.proj", "-t:BuildSample2048")
    run(bin / f"koh-engine{suffix}", "samples/asm/gb-2048/build/2048.gbc", "--frames=300", f"--screenshot={out / 'frame.png'}")
    with Image.open(out / "frame.png") as image:
        image.load()
        if image.size != (160, 144) or image.mode != "RGBA":
            raise RuntimeError(f"Expected 160x144 RGBA frame, got {image.size} {image.mode}")
    linux = sys.platform == "linux"
    verifier = "verify-gpui" if linux else "verify-gpui-platform"
    run(sys.executable, f"scripts/test-{verifier}.py")
    run("dotnet", "build", "samples/csharp/gb-2048-v2/Gb2048V2.csproj", "-c", "Release", "-m:1")
    run(sys.executable, f"scripts/{verifier}.py", bin / f"koh-emulator{suffix}", "samples/csharp/gb-2048-v2/2048v2.gb", "--output", out / "gpui")
    fixture = out / "dap"
    run("cargo", "run", "-p", "koh-debugger", "--example", "fixture", "--locked", "--", fixture)
    os.environ.update(KOH_TEST_DESKTOP=str(bin / f"koh-emulator{suffix}"), KOH_TEST_LSP_DIR=str(bin),
                      KOH_TEST_DAP_ROM=str(fixture / "fixture.gb"), KOH_TEST_DAP_ELF=str(fixture / "fixture.elf"))
    npm = shutil.which("npm")
    run(npm, "ci", cwd="editors/vscode")
    run(npm, "audit", "--omit=dev", "--audit-level=high", cwd="editors/vscode")
    run(*(["xvfb-run", "-a"] if linux else []), npm, "test", cwd="editors/vscode")
    if linux:
        rgbds = root / "artifacts/rgbds"
        if not rgbds.exists():
            run("git", "clone", "--depth", "1", "--branch", "v1.0.1", "https://github.com/gbdev/rgbds.git", rgbds)
        run("make", "-C", rgbds, "-j2")
        os.environ["PATH"] = str(rgbds) + os.pathsep + os.environ["PATH"]
        os.environ["KOH_RGBDS_TEST"] = "1"
        run("cargo", "nextest", "run", "-p", "koh-asm", "-p", "koh-link", "--all-features", "--locked")
        run(sys.executable, "scripts/verify-rust-assembler.py", "--output", out / "assembler")
        run("dotnet", "build", "tools/benchmarks/Koh.Benchmarks/Koh.Benchmarks.csproj", "-c", "Release", "-m:1")
        run("dotnet", "run", "--project", "tools/benchmarks/Koh.Benchmarks", "-c", "Release", "--no-build", "--", "--list", "flat")

# Match the host's CI phases; platform-independent lint runs on Linux.
[linux]
ci: lint test verify

# Match the host's CI phases; platform-independent lint runs on Linux.
[macos]
[windows]
ci: test verify

# Host toolchain archive and installer; no publication.
[script("python")]
[positional-arguments]
package tag:
    import hashlib, os, pathlib, re, shutil, subprocess, sys
    def run(*args): subprocess.run([str(a) for a in args], check=True)
    tag = sys.argv[1]
    number = r"(?:0|[1-9][0-9]*)"
    identifier = rf"(?:{number}|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
    if not re.fullmatch(rf"tools-v{number}\.{number}\.{number}(?:-{identifier}(?:\.{identifier})*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?", tag):
        sys.exit("expected tools-v<semver>")
    run("just", "build")
    version = tag.removeprefix("tools-v")
    root = pathlib.Path.cwd()
    bin = root / "artifacts/bin"
    out = root / "artifacts/package"
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True, exist_ok=True)
    rid = subprocess.check_output(["dotnet", "msbuild", "build.proj", "-nologo", "-getProperty:EmulatorRuntimeIdentifier"], text=True).strip()
    suffix = ".exe" if os.name == "nt" else ""
    (bin / "build-info.txt").write_text("".join(subprocess.check_output(cmd, text=True) for cmd in (["git", "rev-parse", "HEAD"], ["rustc", "-Vv"], ["dotnet", "--version"])))
    archive = pathlib.Path(shutil.make_archive(str(out / f"koh-toolchain-{version}-{rid}"), "zip" if os.name == "nt" else "gztar", bin))
    extracted = out / "extracted"
    shutil.unpack_archive(archive, extracted)
    os.environ["KOH_LSP_TEST_BINARY"] = str(extracted / f"koh-lsp{suffix}")
    run("cargo", "test", "-p", "koh-lsp", "--test", "stdio", "--locked")
    run(extracted / f"koh-compile{suffix}", "--version")
    run(extracted / f"koh-link{suffix}", "--version")
    run("dotnet", "build", "samples/csharp/gb-gfx-demo/GbGfxDemo.csproj", "-c", "Release", "-m:1",
        f"-p:KohCompilerExecutable={extracted / ('koh-compile' + suffix)}", f"-p:KohLinkExecutable={extracted / ('koh-link' + suffix)}", f"-p:KohRomPath={out / 'smoke.gb'}")
    run(extracted / f"koh-engine{suffix}", out / "smoke.gb", "--headless", "--frames=1200", f"--screenshot={out / 'smoke.png'}", f"--mode3-report={out / 'mode3.txt'}")
    if (out / "smoke.png").stat().st_size == 0:
        raise RuntimeError("Packaged engine produced an empty screenshot")
    if "Mode-3 write violations: 0 total" not in (out / "mode3.txt").read_text():
        raise RuntimeError("Packaged engine reported Mode-3 write violations")
    for notice in ("rust-assembler-dependencies.md", "rust-runtime/COPYRIGHT-library.html", "rust-ui/manifest.json", "rust-lsp/manifest.json"):
        if not (extracted / "licenses" / notice).is_file():
            raise FileNotFoundError(extracted / "licenses" / notice)
    assets = [archive]
    if os.name == "nt":
        payload = root / "installers/windows/payload"
        shutil.rmtree(payload, ignore_errors=True)
        shutil.copytree(extracted, payload)
        run(pathlib.Path(os.environ["ProgramFiles(x86)"]) / "Inno Setup 6/ISCC.exe", f"/DAppVersion={version}", "installers/windows/koh-setup.iss")
        assets.append(pathlib.Path(shutil.move(f"koh-setup-{version}-win-x64.exe", out)))
    elif sys.platform == "linux":
        installer = out / "koh-install.sh"
        installer.write_text(pathlib.Path("installers/unix/koh-install.sh.template").read_text().replace("__VERSION__", version))
        installer.chmod(0o755)
        assets.append(installer)
    (out / f"SHA256SUMS-{rid}.txt").write_text("".join(f"{hashlib.file_digest(p.open('rb'), 'sha256').hexdigest()}  {p.name}\n" for p in assets))

# Platform-neutral VSIX; no publication.
[script("python")]
[positional-arguments]
extension tag:
    import hashlib, json, pathlib, shutil, subprocess, sys
    extension = pathlib.Path("editors/vscode")
    version = json.loads((extension / "package.json").read_text())["version"]
    lock = json.loads((extension / "package-lock.json").read_text())
    if sys.argv[1] != f"ext-v{version}" or lock["version"] != lock["packages"][""]["version"] or lock["version"] != version:
        sys.exit("tag must match package.json and package-lock.json")
    shutil.rmtree(extension / "out", ignore_errors=True)
    out = pathlib.Path("artifacts/extension")
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True, exist_ok=True)
    for args in (("ci",), ("audit", "--omit=dev", "--audit-level=high"), ("run", "compile"), ("run", "package", "--", "--out", str((out / f"koh-asm-{version}.vsix").resolve()))):
        subprocess.run([shutil.which("npm"), *args], cwd=extension, check=True)
    asset = out / f"koh-asm-{version}.vsix"
    (out / "SHA256SUMS.txt").write_text(f"{hashlib.file_digest(asset.open('rb'), 'sha256').hexdigest()}  {asset.name}\n")
