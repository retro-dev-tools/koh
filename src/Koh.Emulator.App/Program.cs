using Koh.Emulator.App;
using Koh.Emulator.Core;
using KohUI;
using KohUI.Backends.Gl;

// Phase 1: hardcoded ROM path. Resolve against the repo test-rom
// fixture so the emulator window opens on something interesting —
// file-picker UI comes in phase 2. CLI arg override keeps local
// debugging flexible.
string romPath = args.Length > 0 ? args[0] : FindDefaultRom();

// AudioSink owns the OpenAL context; EmulatorLoop runs the emulator
// core on its own thread and paces against the sink's buffer depth.
// This keeps emulated speed locked to the audio hardware clock (not
// monitor vsync), which matters because GB is 59.73 Hz and a 60 Hz
// display would otherwise introduce slow pitch drift.
using var audio = new AudioSink();
using var loop  = new EmulatorLoop(audio);

var runner = new Runner<EmulatorModel, EmulatorMsg>(
    initialModel: new EmulatorModel(Loop: loop, RomPath: null, FrameCount: 0, Status: "Loading...", ShowDebug: false),
    update: EmulatorApp.Update,
    view: EmulatorApp.View);

// Load the ROM synchronously and seed the runner before the window
// opens — that way the initial render already has the hardware booted
// and we don't paint a frame with the grey placeholder.
// .gbc → CGB, everything else → DMG. Covers the common case without
// needing to parse the cartridge header for the pick; CartridgeFactory
// still validates header + MBC shape.
var mode = string.Equals(Path.GetExtension(romPath), ".gbc", StringComparison.OrdinalIgnoreCase)
    ? HardwareMode.Cgb
    : HardwareMode.Dmg;
runner.Dispatch(EmulatorApp.LoadRomFromDisk(romPath, mode));

var backend = new GlBackend<EmulatorModel, EmulatorMsg>(
    runner,
    title: "Koh Emulator",
    onTick: () => new Tick(),
    onKeyDown: name => EmulatorApp.MapShortcut(name)
                    ?? (EmulatorApp.MapKey(name) is { } btn ? new JoypadDown(btn) : null),
    onKeyUp:   name => EmulatorApp.MapKey(name) is { } btn ? new JoypadUp(btn)   : null);

backend.Run();
await runner.DisposeAsync();
return 0;

static string FindDefaultRom()
{
    // Search upward from the exe for the repo's .playwright-cli/ folder
    // where azure-dreams.gbc lives — works whether you `dotnet run` from
    // the project directory or invoke the published binary in-place.
    var dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        var candidate = Path.Combine(dir, ".playwright-cli", "azure-dreams.gbc");
        if (File.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    // Fall through to a blargg test ROM — always present under
    // tests/fixtures so builds from inside src/ still find something.
    dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        var candidate = Path.Combine(dir, "tests", "fixtures", "test-roms", "blargg", "cpu_instrs", "cpu_instrs.gb");
        if (File.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    throw new FileNotFoundException("no default ROM found. Pass a path as arg1 or place azure-dreams.gbc under .playwright-cli/.");
}
