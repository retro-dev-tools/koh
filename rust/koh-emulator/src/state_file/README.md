# Legacy wrapped state fixture

`csharp.bin` is the actual C# `SaveStateFile.Save` output from accepted baseline
`e1336b0`. `oracle.cs` captures it using the baseline `Koh.Emulator.dll` assembly
(SHA-256 `973a5dbe4f4ede0893a4adbde947e56835823b7afdb9e2292902576ed8512281`).
The test compares all 50255 bytes with Rust output and verifies restore plus
framebuffer continuation, bad magic/version/hash and truncated-payload rollback.

Fixture SHA-256: `b3edf5760d88ec1dfe976e9f324a3daba901d4aff4bbc54e383ec2b03b46a2c6`.

This STES/version-2/hash wrapper is the separate library utility. The existing
desktop `.state` path remains the raw machine payload and is tested separately
against actual native C# session replies and files.
