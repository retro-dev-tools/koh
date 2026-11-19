# Cartridge reference fixture

`oracle.bin` was emitted by `oracle-capture.cs` using the existing independently
built C# `Koh.Emulator.dll`. It records 4096 ROM/RAM bank operations across
ROM-only/MBC1/MBC3/MBC5, seven observations after each operation, four complete
raw cartridge states and 128 deterministic RTC before/time/after cases. ROMs
are generated bank markers. No downloaded game or managed runtime is needed
to execute the Rust test.

Capture in a private .NET 10 console project using an assembly `Reference`
to the immutable C# emulator DLL. Copy `oracle-capture.cs` as `Program.cs` and
pass the fixture output path as the application argument. The capture does not
build or mutate the emulator project. Run outside the repository if the host
SDK differs from its pinned SDK; the capture needs only a compatible .NET 10
compiler and runtime.

SHA-256 at capture:

| Artifact | SHA-256 |
| --- | --- |
| oracle.bin | 352b6739468896f07bb37297e9c09eae6e1be29283eff195d086a8b9e70f6824 |
| oracle-capture.cs | 60360d650d3b26601327fa2febcf9ec4e8e7f49049ab1b2b40fdea73670b63b1 |
| Koh.Emulator.dll | 973a5dbe4f4ede0893a4adbde947e56835823b7afdb9e2292902576ed8512281 |
| Cartridge.cs | c9490d056c4d030794b21654e41f3aca61fb02cf3390e8e1ba0a5a4789ebdae2 |
| CartridgeHeader.cs | 06bb88654d1ce1ab01015478a278b8bbc1ab292a275565c485a73aff92c59c7e |
| Mbc1.cs | 8b16b782d071e630b7aa289574ffcb76a004e5ed7293e906247454d1867334fd |
| Mbc3.cs | 4cb7f355abb0338faae8e3a3fd1832b59c534ba670c3fbccfcc0865da9764bda |
| Mbc5.cs | c9431c273dccc0cd8e44b763f2ff0fe8a47f0bccd835b6a25377f0d2c98a9deb |
| Rtc.cs | e56a8065496146c3bdefd5a9ff3c103c948f88a5dd7557b60b5e01181e1cb8f9 |

`cartridge.rs` tests retain all source assertions in `Mbc1Tests` (six methods),
`Mbc3Tests` (six), `Mbc5Tests` (three) and `CartridgeHeaderTests` (five), grouped
by behavior. Additional cases cover mode/high bank bits, nine-bit MBC5 with
the existing eight-bit debugger bank result, exact state order, failed-state
atomicity, all header size codes, ASCII replacement and RTC day carry.

Host-clock latching is tested separately with first-call bootstrap. The binary
oracle avoids nondeterministic host timestamps; RTC advancement takes explicit
timestamps. This does not verify SRAM file persistence or whole-machine banking.
