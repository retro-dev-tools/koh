# Build and debug a sample

Run these commands from a Git checkout's root, using the .NET SDK selected by
`global.json` (10.0.115 or a compatible newer .NET 10 SDK) and Rust 1.88 or newer
with Cargo for the native tools. Restore needs network
access. The local Koh.Sdk feed imports projects from this checkout; it is not a
standalone SDK installation.

## Build both games

```sh
dotnet msbuild build.proj -t:BuildSample2048
dotnet build samples/csharp/gb-2048-v2/Gb2048V2.csproj
```

The assembly build emits `samples/asm/gb-2048/build/2048.gbc`, `2048.sym`, and
`2048.kdbg`. The C# build emits `samples/csharp/gb-2048-v2/2048v2.gb`.
Keep debug information beside the matching ROM and rebuild after editing source.
Assembly debug information records source paths from the build checkout; rebuild
after moving the checkout.

## Run a ROM

For a window with graphics and audio:

```sh
dotnet run --project src/Emulator/Koh.Emulator.App -- samples/asm/gb-2048/build/2048.gbc
```

The desktop emulator needs a working display, SDL3, and OpenAL. For a headless
check, use the same application with PNG output:

```sh
dotnet run --project src/Emulator/Koh.Emulator.App -- samples/asm/gb-2048/build/2048.gbc --screenshot=assembly-title.png --frames=300
dotnet run --project src/Emulator/Koh.Emulator.App -- samples/csharp/gb-2048-v2/2048v2.gb --screenshot=csharp-title.png --frames=300
```

PNG capture does not open a window or initialize audio or graphics. To host a
debug adapter without a display, use `--headless --dap=<endpoint>` instead.
See [SDL3 setup and verification](sdl3.md) for desktop dependencies.

## Debug assembly in VS Code

The extension uses Windows named pipes or an explicit Unix socket path under
`/tmp` to stay within Unix socket path limits even with a long `TMPDIR`.
On Unix, it passes the same absolute path to the emulator and VS Code.
The DAP host runs independently of graphics initialization. If a desktop DAP
session cannot initialize graphics, its debug adapter continues headlessly.
Windows and macOS GUI debugging still need native runtime smoke tests.

Install the Koh extension, or run its development host as described in the root
README (Node.js and npm are required for extension development). Open the repository
root in that host. Publish the native emulator:

```sh
dotnet msbuild build.proj -t:PublishEmulatorApp
```

NativeAOT requires the native compiler/linker toolchain for the host. Configure
`koh.emulator.exePath` in VS Code to the absolute path of the published executable
under `src/Emulator/Koh.Emulator.App/bin/Release/net10.0/<host-rid>/publish/`.
On Windows the executable is `Koh.Emulator.App.exe`.

Create `.vscode/launch.json` in the repository root:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Debug assembly 2048",
      "type": "koh",
      "request": "launch",
      "program": "${workspaceFolder}/samples/asm/gb-2048/build/2048.gbc",
      "debugInfo": "${workspaceFolder}/samples/asm/gb-2048/build/2048.kdbg",
      "stopOnEntry": true
    }
  ]
}
```

Build the ROM first. Set a breakpoint on `ldh a, [rLY]` immediately after
`ld sp, $E000` in `samples/asm/gb-2048/src/main.asm` (line 78 at this revision).
Start the launch configuration, then continue from entry. The breakpoint should
be verified and execution should stop on that source line. Expand **Registers**
in Variables: `SP` should be `$E000`. Inspect the call stack's source location
and step to observe register changes.

This walkthrough covers assembly source debugging. Building the C# sample does
not establish C# source stepping, arbitrary .NET support, or complete RGBDS
compatibility.
