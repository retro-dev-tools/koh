use std::process::Command;

#[test]
fn tile_sheet_cli_preserves_bytes_and_existing_outputs() {
    let dir = tempfile::Builder::new()
        .prefix("koh assets with spaces ")
        .tempdir()
        .unwrap();
    let input = dir.path().join("four-colors.png");
    let output = dir.path().join("Art.g.cs");
    std::fs::write(
        &input,
        include_bytes!("fixtures/TileSheets/four-colors.png"),
    )
    .unwrap();
    let run = || {
        Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .args(["--tile-sheets", "--namespace", "Some.Ns", "-o"])
            .arg(&output)
            .arg("--")
            .arg(&input)
            .output()
            .unwrap()
    };
    let result = run();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let source = std::fs::read_to_string(&output).unwrap();
    assert!(source.contains("namespace Some.Ns;"));
    assert!(source.contains("public const byte FourColorsTileCount = 1;"));
    assert!(source.contains("FourColorsColor0 = 0x7FFF;"));
    assert!(source.contains("FourColorsColor3 = 0x1084;"));
    assert!(source.contains("0xFF, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, "));
    std::fs::write(
        &input,
        include_bytes!("fixtures/TileSheets/five-colors.png"),
    )
    .unwrap();
    let result = run();
    assert!(!result.status.success());
    assert!(String::from_utf8_lossy(&result.stderr).contains("5 distinct colors"));
    assert_eq!(std::fs::read_to_string(&output).unwrap(), source);
    let result = Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .args(["--tile-sheets", "--namespace", "Some.Ns", "-o"])
        .arg(&input)
        .arg("--")
        .arg(&input)
        .output()
        .unwrap();
    assert!(!result.status.success());
    assert!(String::from_utf8_lossy(&result.stderr).contains("distinct"));
    assert_eq!(
        std::fs::read(&input).unwrap(),
        include_bytes!("fixtures/TileSheets/five-colors.png")
    );
}
