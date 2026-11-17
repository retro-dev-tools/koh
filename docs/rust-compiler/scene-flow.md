# Executed scene transitions

`driver::game_loop_defers_scene_changes_and_preserves_constructor_state` is a
native rstest integration test in Debug/Release × DMG/CGB. Roslyn compiles the
SceneFlow fixture against the actual Koh.GameBoy project. The Rust compiler
includes referenced framework bodies; canonical ELF is serialized and read back,
linked by Rust, and executed by the existing C# emulator.

The fixture runs the actual nonreturning `Game.Run`. Its title waits two frames,
requests a constructor-initialized play scene, and continues executing before
Exit. The play scene requests a final scene and also continues before Exit.
An independently expected decimal event trace123456789 checks first Enter,
both request continuations, both Exits, both subsequent Enters and play Update.
The final packet additionally checks constructor value1234 across two scenes
and clock frame4. The expected little-endian packet is15CD5B07D2040400.

Execution stops at the reachable `Program::Done` symbol inside final Enter;
it does not assume a returned frame or defined return registers. The host's
small `--native-scene` mode exposes the eight-byte test packet. Existing stack
memory guards remain checked and SP must remain inside the reserved stack.
The fixture explicitly disables LCD before framework initialization; emulator
constructor defaults are not part of its expected result. No emulator production
code, game-facing API or compiler algorithm changes are needed.

Traceability: PORT, complete,
`CilVirtualDispatchTests.SceneMachine_GameRun_DeferredChangeWithCtorState`:
one original method, two Roslyn configurations and five assertion sites.
Register-specific phase markers are replaced by explicit lifecycle/state output.
This preserves the useful deferred-transition behavior without making the current
public API or private layout a convention for future compiler design. It does
not establish complete sample-game, graphics, debugger or migration acceptance.

Verification: the four-case fixture selection passed4/4 in24.176s, exit0.
After formatting and adding the live-stack bound, the selection combining scene
flow, object identity/faults, virtual dispatch and actual tileset transfers passed
16/16 in112.176s, exit0;412 unrelated driver cases were excluded by that filter.
Completed log: `/tmp/koh-scene-flow-regression.log`. Rustfmt, CSharpier(two files)
and diff checks pass. This is local focused evidence, not whole-workspace or
cross-platform acceptance.
