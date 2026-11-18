# Per-program RAM layout

Status: the5000-byte source requirement has native acceptance. General banked
storage and full migration acceptance remain open. This replaces the assumption
that the first4KiB arena is an immutable ABI.

The default remains an unbanked C100–CFFF stack and bank1 D000–DFFF arena. When
reachable IR contains a larger known allocation requirement, the compiler chooses
a page-aligned larger contiguous arena ending at E000, reducing the stack from its
top. Known requirements include array payload/header/alignment, rectangular arrays,
objects and raw allocations. This is a minimum known requirement, not a proof of
total lifetime allocation demand. Dynamic and cumulative exhaustion remain checked.

The linker receives distinct fixed WRAM0 extension and WRAM1 reservations, plus
the shortened stack UNION. It still owns placement and overlap diagnostics; no
new object container or second linker is introduced. Bounds and startup SP must
agree across every emitted function. Exported symbols describe the selected ABI.

Before selecting expansion, stack accounting must include frames, argument/return
areas, call cookies, hardware return addresses and the IRQ reserve. Acyclic call
graphs can be bounded; recursive execution retains runtime stack guards. Allocation
and pin code must use the selected capacity/base, including a pin starting below
D000 whose buffer crosses into switchable WRAM. The caller's bank must be restored.

Alternatives: permanently shrinking every game's stack wastes depth for games
that need no larger allocation; segmented arrays require a different pointer and
copy model; pretending the5000-byte source passed because it traps loses useful
capacity. A per-program physical partition keeps ordinary flat CIL arrays while
making the actual RAM tradeoff explicit. No game API or RAM-size CLI option is
needed for this requirement.

## Focused execution evidence

The first positive selection passed four Debug/Release × DMG/CGB cases in9.139s,
exit0 (`/tmp/koh-expanded-array-second.log`). Each round-trips native ELF, links
with the Rust linker and executes the existing C# emulator. Result16, SP CC00,
the complete5120-byte arena, length5000, zero initialization, endpoints7/9 and
unchanged bank2 are asserted. The preceding run caught a startup symbol-index
regression introduced by this change; adding the absolute layout symbol after
existing relocation targets corrected it.

The pin extension passed6/6 in9.170s, exit0
(`/tmp/koh-expanded-array-pins.log`). Two additional CGB cases select bank2,
pin a5000-byte array beginning below D000, write both endpoints through the raw
pointer and verify caller-bank restoration. A subsequent raw DFF0 write changes
only bank2; every other byte of both arenas is checked. Default-layout and stack/
IRQ regressions, other allocation shapes and final source disposition remain
required before this unit is complete.

The raw-arena fixture's constant4097-byte allocation now succeeds with capacity4352,
SP CF00 and returned bus pointer CFFF. Its old expected fault represented the
retired fixed-capacity policy. The replacement asserts those exact values and
untouched memory; negative, truncated and cumulative-collision cases still require
fault05. Physical-capacity rejection and IRQ reservation have dedicated planner
tests. This does not close the larger source audit until all allocation forms and
stack integration have their acceptance evidence.

Corrected selected regression passed289/289 in68.906s, exit0
(`/tmp/koh-expanded-arena-regression-final.log`), with596 explicitly unselected.
This covers compiler unit tests, static contracts, pinned memory, raw-arena
collisions and interrupt contracts. The preceding selection failed five cases:
one diagnostic-symbol assertion typo and four obsolete4097-byte failure
expectations. It is not counted as green. Full-workspace and cross-platform
acceptance remain open.

## Allocation-shape and stack follow-up

Four cases initially executed20 ROMs covering625 long elements, a50×100 byte
rectangle, a256-aligned5000-byte payload, raw5000-byte reset/reuse and managed/raw
collision. Complete arena/header/padding/tail and alternate-bank snapshots passed
in13.321s, exit0 (`/tmp/koh-expanded-shapes.log`).

Adding bounded/deep mutual recursion and two native compile-time rejection roots
passed12/12 in13.434s, exit0 (`/tmp/koh-expanded-stack-final.log`): eight planner
cases plus28 ROMs/eight rejected compilations. Deep recursion faults01 before
guard/arena corruption; bounded recursion returns16. A single-function sum was
legally transformed to a loop, so the stack-exhaustion fixture uses mutual calls
instead. The earlier failing probe is not treated as a compiler bug.

Wider regression exposed fixed4KiB expectations for4095-byte vectors, packed
aggregate arrays, aligned payloads and65×65 rectangles. These now assert exact
expanded layouts/results. Product requests beyond physical WRAM reject at compile
time with member/IL context; dynamic invalid dimensions and shared exhaustion
retain explicit runtime faults.

Alignment selection evaluates each page-sized candidate's actual bus address.
An empty4096-aligned array still fits the default arena, while a4096-byte payload
can use a WRAM0 header and WRAM1 payload. Worst-case padding arithmetic incorrectly
over-reserved or rejected these valid shapes. Dedicated planner cases protect both.
The allocator also had to align the relocated bus address, rather than merely
the cursor offset: after expansion, the arena base is no longer necessarily
aligned to4096. The shared allocator now adds the relocated heap base before
rounding and subtracts it afterward. Full physical snapshots caught this bug;
length-only return checks did not. A4096-byte payload aligned at D000 now fills
WRAM1 exactly, with its header at CFFE and initial SP CF00.

Final corrected affected regression passed354/354 in184.961s, exit0
(`/tmp/koh-expanded-capacity-bus-alignment.log`),540 explicitly unselected.
The source contract now closes one additional REPLACE disposition:531/565,
34 outstanding. This is not general banked-data or full migration acceptance.
The earlier939-case run passed923/939 in371.336s but failed16 old-capacity cases;
the intermediate corrected354-case run passed350 and exposed the four actual
bus-alignment failures. Neither failing run is reported as green. IR/backend cases
in that earlier selection passed; they were not redundantly rerun after the
shared alignment fix. Complete current-head workspace/host validation remains open.

The final planner boundary selection passed12/12 in0.098s, exit0
(`/tmp/koh-expanded-capacity-boundary.log`), including the7680-byte arena/256-byte
stack partition. All-feature discovery reports2119 workspace/1942 compiler cases,
zero ignored and all14 static-contract cases (`/tmp/koh-expanded-capacity-discovery.json`,
exit0). Separate workspace rustdoc ran successfully with no current doctest cases;
rustfmt and diff checks passed. The pre-existing cubes `build_host` warning remains.
