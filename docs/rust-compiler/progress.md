# Native CIL compiler migration (PR #55)

Status: migration incomplete. Native CIL reading, typed IR, optimization and
SM83/ELF emission are implemented, including banked managed calls and
[bounded hardware interrupt roots](irq-calls.md). Broader IRQ runtime support remains
incomplete. The SDK now
invokes native compiler/linker processes; the legacy compiler source and test
project still remain pending final replacement acceptance. Complete sample
acceptance, test-disposition closure, performance comparison and final hosted
validation remain required. The current public API is evidence, not a design
baseline; authoring needs and hardware constraints determine the new contract.

### Video lifecycle and input closure

Three remaining video/input source methods now have native replacements, with16
Debug/Release × DMG/CGB ROMs. Physical register composition, distinct VBlanks,
counter wrap and held/released/new input edges are checked. Clean maps previously
entered heavy commit helpers after the wait; shared preparation now reports pending
work so clean frames skip the dispatcher. Affected23/23 passed58.089s and map/sample
10/10 passed53.425s, including all four real graphics showcase configurations.
[Source matrix, independent hardware contract and evidence](video-joypad-contracts.md).
Ledger534/565,31 outstanding; final acceptance and legacy deletion remain open.

### Expanded arena and static-capacity closure

The5000-byte source contract now has complete native replacements, with58 total
static/allocation ROMs and eight rejected compilations. Wider cases cover exact
wide/rectangular/aligned/raw storage, shared exhaustion and mutual recursion.
Candidate-address alignment and the allocator's bus-address rounding fix a real
expanded-base bug. Final affected354/354 passed184.961s, exit0.
[ABI, decisions and failure evidence](ram-layout.md). Ledger531/565,
34 outstanding; legacy deletion and full current-head host acceptance remain open.

### Earlier expanded arena implementation checkpoint

[Per-program RAM partitioning](ram-layout.md) now executes the5000-byte array
with result16 across Debug/Release × DMG/CGB. CGB cross-boundary pins also restore
the caller bank. Selected compiler/IRQ/pin/raw/static regression passed289/289
in68.906s, exit0. Wider allocation-shape and stack acceptance remain open, so this
implementation checkpoint adds no source-disposition credit:530/565,35 outstanding.

### Static storage source contracts

Nine methods now have native replacements for29 assertion sites, with20 ROMs
checking zero/init state, counter wrap, readonly-array mutation, array identity,
literal storage and cached callback behavior. Affected64/64 passed94.287s, exit0.
[Matrix, independent requirements and capacity evidence](static-contracts.md).
Before RAM partitioning, the separate5000-byte array faulted05 in both modes;
the newer closure above supersedes that limitation. At this checkpoint the
ledger was530/565,35 outstanding.
No capacity reduction is hidden behind test closure; full migration remains active.

### CGB prepared-map source ownership

Debug and Release reproduced GDMA reading bank2 poison instead of the authored
bank1 shadow when the caller selected SVBK2. Prepared commits now retain their
shadow pin through the hardware transfer and restore the caller mapping afterward.
Affected24/24 passed91.148s, including all four real graphics showcase cases.
[Root cause, physical-bank assertions and evidence](bg-win-maps.md#cgb-commit-source-bank-lifetime).
No new source-method credit: ledger521/565,44 outstanding. General bank/capacity
and final full acceptance remain open.

### Sprite and palette acceptance

Seven remaining source methods now have native replacements for57 assertion sites.
Eight Rust cases cover32 normal ROMs, two unsafe palette probes and two desktop
library executions. Full OAM/palette snapshots and timing guards exposed Debug DMG
sprite DMA crossing into visible scanlines and a CGB map commit running late.
Preparation now precedes the sprite VBlank wait; map commits recheck LY after
register preparation with a conservative admission window. Public APIs remain useful.
Affected36/36 passed129.702s, exit0, including the real graphics showcase in all
four build/hardware combinations. [Decision, matrix and evidence](sprite-contracts.md).
Ledger521/565,44 outstanding; final full acceptance and legacy deletion remain open.

### Deferred LINQ runtime

Rust redirects exact trusted Enumerable signatures to metadata-selected ordinary
target CIL in Koh.GameBoy. Real deferred query objects replace the legacy
placeholder model. Where/Select, Count/Any/All, checked numeric Sum and primitive
Min/Max execute through ELF and the Rust linker. Twelve rstest cases passed with
120 Debug/Release × DMG/CGB ROM executions and16 unsupported-overload checks in
69.522s, exit0. Full result packets cover effects, disposal, repeat enumeration,
foreign types, numeric widths and distinct failure traps.
[Contract, evidence and remaining validation](linq-runtime.md).
The acceptance extension adds exact floating edge cases, metadata mutations,
deterministic PDB/ELF/KDBG provenance, four ordinary SDK build/run cases and four
real selector breakpoint/disassembly/unwind sessions. Expanded30/30 passed308.833s;
all four SDK cases passed separately. Five source methods now have replacements:
ledger514/565,51 outstanding. Full migration acceptance and legacy deletion remain open.

### Cross-assembly callback arguments

Delegates now share canonical closed-type binding with an assembly scope per
argument, replacing the raw single-scope substitution path. Referenced class,
struct, enum, array and nested delegate arguments preserve nominal identity;
runtime declarations, variance positions and constraints validate before interning.
Eight new rstest cases cover44 Debug/Release × DMG/CGB ROMs plus six valid reuse
runs after errors, full memory packets and root/library PDB-to-KDBG provenance.
Expanded462/462 passed216.581s; final affected8/8 passed17.298s, both exit0.
Four old Rust compound-type rejection expectations now execute42 instead.
[Decision, Rust API change, evidence and limits](foreign-callback-scopes.md).
Discovery2061 workspace/1884 compiler, zero ignored; separate compiler doctests0
examples passed. Ledger509/565,56 outstanding. LINQ, final full acceptance and
legacy deletion remain open.

### Generic callback storage

Delegate binding now preserves both declaring-type and method parameter scopes,
fixing Func<T,T> fields in generic classes without LINQ-specific patterns. Four
Debug/Release × DMG/CGB cases execute32 ROMs across scalar widths, array references,
distinct scopes, generic captures and owner method groups, with full memory packets.
Expanded binding/layout/reference validation passed454/454 in198.597s; the final
affected fixture and scope selection passed86/86 in42.660s, both exit0.
[Contract and limits](delegates.md#generic-declaring-and-method-scopes).
Foreign named argument combinations still diagnose; no ABI/API changes or LINQ
source closure is claimed. The LINQ probe now reaches unsupported array RTTI in
Where. Ledger509/565,56 outstanding; final validation and legacy deletion remain open.
Fresh discovery2053 workspace/1876 compiler cases, zero ignored.

### Local definite assignment

Nonzeroed CIL locals now use bounded verifier must-assignment analysis across
branches and loops; lowering omits their user-local zero stores. Finally preserves
the original flag. Raw CIL covers missing-path/first-iteration reads, block order
and bitset boundaries. Actual SkipLocalsInit Roslyn fixtures execute24 DMG/CGB ROMs
across Debug/Release, and four strengthened SDK iterator/finally build/run cases pass.
Expanded1070/1070 passed457.594s, exit0;551 explicitly unselected.
[Contract, evidence and conservative boundaries](local-assignment.md).
Unassigned-address initialization, continuation-correlated nonzeroed cleanup and
broader LINQ remain unsupported. The fresh LINQ probe now reaches generic declaring
storage inside Where. No source-method closure is claimed:509/565,56 outstanding.
Fresh discovery2047 workspace/1870 compiler cases, zero ignored. Full current-head
cross-platform validation and legacy deletion remain open.

### LINQ investigation

[Fresh Roslyn/native probes and semantic audit](linq-audit.md) identify separate
array-to-interface conversion, recursive constraint binding and framework layout
resolution gaps. The framework root layout bug is now fixed: the real trusted
System.Object definition has a base-less two-byte root layout, while an unrelated
same-named root still rejects. Two new rstest cases cover Debug/Release metadata;
expanded layout/reference/generic/interface validation passed148/148 in136.815s,
exit0. The original iterator pipeline now reaches the explicit uninitialized-local
analysis gap inside Where rather than failing root layout. Pipeline Max rejection is retired as a proposed acceptance
expectation; the replacement must execute and return 3. All five LINQ source
methods remain outstanding. This audit adds no completed-test credit or support
claim; the ledger remains509/565,56 outstanding.

### Structured finally and ordinary iterator closure

Normal structured cleanup now lowers to the existing typed CFG and per-call ABI.
Bounded small/fat/chained EH decoding, original stack/local checks and source
provenance accompany normal leave/endfinally semantics. ECMA stack discard and
disjoint-block entry rules are tested independently of Roslyn. Scoped dispatch
signature loss and System named-type misclassification were fixed; the exact
CurrentManagedThreadId getter has a single-managed-thread identity1 contract.
No generated state-machine names, native BCL execution or new dependency is used.
[Contract, ABI, dispositions and evidence](finally-iterators.md).

Three original iterator methods/seven assertion sites now map to native tests:
84 direct ROM executions, four standard-reference SDK build/run cases and four
real debugger source-breakpoint/unwind sessions. Final315/315 passed62.156s,
exit0;797 explicitly unselected. A broader651-case checkpoint found two obsolete
catch diagnostic expectations, now corrected; it is not called a green run.
Ledger509/565 (508 native replacements, one DELETE),56 outstanding. Catch/filter/
fault, exceptional unwinding and yield bodies requiring fault remain explicitly
unsupported. Full current-head hosted validation and legacy deletion remain open. Fresh
discovery2017 workspace cases/1840 compiler cases, zero ignored.

### Ordinary AllowByRefLike arguments

The anti-constraint now permits ordinary arguments instead of rejecting its
presence. Actual ref-like arguments are detected through raw-definition metadata
and reject with missing-permission or unsupported-lifetime diagnostics. Shared
attribute constructor decoding retains Koh contracts; malformed/duplicate markers
reject and an unrelated same-named attribute is not treated as the marker.
Four new rstest cases execute32 Debug/Release × DMG/CGB ROMs across primitive,
reference, array/string, value and closed generic value arguments. An earlier
Rust mutation assertion wrongly rejecting flag32 on Cell<int> now executes the
original Objects behavior instead. No source-method ledger credit is added.
[Contract and evidence](allow-ref-like.md). Expanded308/308 passed133.153s, exit0;
481 explicitly unselected. The real iterator now reaches exception sections;
structured finally/disposal remains next. Ledger506/565,59 outstanding; full
current-head hosted validation and legacy compiler deletion remain open. Fresh
discovery1971 workspace/1794 compiler cases, zero ignored.

### Exact variant interface binding

Valid exact nominal instantiations of covariant/contravariant interfaces now bind
and dispatch, including reference and integer returns. Declaration validation
checks return/parameter positions, nested variance, arrays, byrefs, inherited
interfaces and static virtual signatures against ECMA-335/.NET rules. Existing
invalid flags and constraints still diagnose. Cross-instantiation coercions and
variant runtime cast/type tests reject explicitly pending variance-aware matching.
Four new rstest cases execute24 Debug/Release × DMG/CGB ROMs across six entries;
existing shape/flag and callback tests remain checked. The Steps iterator probe
now reaches AllowByRefLike/ref-safety rather than blanket variance rejection.
[Contract, evidence and limitations](exact-variance.md). Expanded300/300 passed
99.675s, exit0;485 explicitly unselected. Source ledger unchanged506/565,59 open;
all three iterator source methods remain outstanding. Full hosted validation and
legacy compiler deletion remain open. Fresh discovery1967 workspace/1790 compiler,
zero ignored.

### Null interface base classification

Iterator investigation exposed a real resolver bug: a valid null Extends index
was treated as TypeDef02000000 while classifying a framework interface.
The shared type binding now skips null bases, retaining non-null resolution and
layout validation. Four new Debug/Release × DMG/CGB cases execute a framework
IDisposable parameter/null check through ELF and ROM. The original iterator
fixture now reaches an explicit unsupported-variance diagnostic; it does not yet
execute. All three iterator methods remain outstanding, with no additional source
ledger credit. [Evidence and next gaps](iterator-gaps.md).
Expanded compiler/reference/interface/generic validation passed296/296 in98.178s,
exit0;485 explicitly unselected. Fresh discovery1963 workspace/1786 compiler,
zero ignored. Final current-head hosted validation remains open.

### Retained-IL reference support

The reader now accepts bounded standalone ReadyToRun version16 references with
retained IL, fixing rejection of valid managed Math.Max before lowering.
Native-only bodies, native entries, unsupported image forms and malformed headers
still diagnose. No native runtime code is executed or embedded. Four rstest cases
cover eight Debug/Release × DMG/CGB ROMs, signed boundary results, reachability,
Math.Sqrt rejection and36 header mutations. Expanded compiler/reference/IR
validation passed242/242 in23.943s; reader/signature/opcode/layout/limits validation
passed147/147 in40.257s. [Contract and evidence](retained-il.md).
One old blanket-BCL-policy method (two assertion sites) is replaced:506/565
complete (505 native replacements, one DELETE),59 outstanding. No legacy compiler
source is deleted yet; full product and current-head hosted acceptance remain open.
Fresh discovery:1959 workspace cases,1782 compiler cases, zero ignored.

### String tables and referenced-memory closure

Seven [reference/string-table source methods](reference-memory-contracts.md),23
assertion sites, now have native replacements.28 Debug/Release × DMG/CGB ROMs
check original string table/local values, alias mutation, null/bounds traps,
referenced stackalloc copy/fill/palette writes, full target-map clears and dead
method exclusion despite a missing unreachable dependency. Explicit LCD-off/map
initialization replaces old implicit VRAM/PPU assumptions. Expanded243/243 passed
29.177s, exit0,530 explicitly unselected (/tmp/koh-reference-contracts-final.log).
Four already-counted raw-arena rows and the existing rectangular method were
reconciled without duplicate credit. Ledger505/565 (504 replacements, one DELETE),
60 outstanding. The old blanket BCL-rejection test remains open. No production
compiler/emulator/API changes; final hosted acceptance remains open.

### Canvas pixel and presentation closure

All seven [canvas source methods](canvas-contracts.md),36 assertion sites, now
have dedicated native PORT replacements.32 Debug/Release × DMG/CGB ROMs verify
full pixel planes, solid/dither masks, diagonal/triangle/clipping, both page-flip
states, centered maps and actual CGB GDMA counts with unchanged mode3/VBlank
safety guards. Expanded237/237 passed46.810s, exit0,522 explicitly unselected
(/tmp/koh-canvas-final.log). Initial extra LCD-off CGB no-DMA assumption was wrong
and corrected against documented hardware/library behavior; no legacy assertion
or timing ceiling weakened. Ledger498/565 (497 replacements, one DELETE),
67 outstanding. No production/API changes; full hosted acceptance remains open.

### Framework state and scripted input acceptance

All five [framework source methods](framework-contracts.md),14 assertion sites,
have native replacements.20 Debug/Release × DMG/CGB ROMs expose independent RNG,
timer, asset, input and frame-clock packets, canonical ELF round-trip and actual
VRAM/LCD/OAM state. Test-only input modes script Right/release/Left and held A;
production emulator/API remain unchanged. Expanded239/239 passed38.553s, exit0,
516 explicitly unselected (/tmp/koh-framework-final.log). Ledger491/565
(490 replacements, one DELETE),74 outstanding. Final hosted acceptance remains open.

### Font and decimal text acceptance

All five [font/text source methods](font-text.md),31 assertion sites, now have
native PORT replacements.24 Debug/Release × DMG/CGB ROMs check authored768-row
font input expanded to1536 tile bytes, original string/window/byte-array text,
decimal alignment/zero, shifted font base and65535 exceeding field width.
Whole VRAM, OAM, LCD/palette/result/stack guards remain checked. Expanded238/238
passed38.961s, exit0,512 explicitly unselected (/tmp/koh-font-text-final.log).
Ledger486/565 (485 replacements, one DELETE),79 outstanding. No production/API
changes or framebuffer/debugger claims; final hosted acceptance remains open.

### Background/window graphics acceptance

All five [Bg/Win source methods](bg-win-maps.md),50 assertion sites, now have
native replacements:16 Debug/Release × DMG/CGB ROMs compare every available VRAM
byte, original fills/blits/attributes,64 board cells, LCD/palette state, OAM and
mode3 safety. DMG phantom second-bank assertions are replaced by actual hardware
VRAM/no-op/register observations. Expanded300/300 passed27.286s, exit0,
812 explicitly unselected (/tmp/koh-bg-win-final.log). Stale completion markers
for11 already-counted comparison/conversion methods were reconciled after fresh
native checks; no duplicate credit. Ledger481/565 (480 replacements, one DELETE),
84 outstanding. No production/API changes; final full hosted acceptance remains open.

### Pointer and multi-loop behavioral closure

Eleven [loop memory-flow methods](loop-memory-flows.md) now have native PORT
replacements for19 source assertion sites.624 added Debug/Release ROMs check
original buffers/values, all copied/filled bytes, calls, forward overlap, both
branch arms, zero trips, unequal lengths and final pointers. Runtime/bound inputs
run before/after optimization; whole rewrite fixture3072 ROMs. Observer expansion
throughC4FF/cap256 accommodates the longest192-event trace; strict full equality
remains. Expanded233/233 passed48.951s, exit0,207 explicitly unselected
(/tmp/koh-loop-flows-final.log). Ledger476/565 (475 replacements, one DELETE),
89 outstanding.18/20 LoopCodegen methods complete; the two190/740-dot timing
methods remain open. No production or API changes.

### Direct terminal comparison branches

The backend now [branches directly from terminal single-use comparisons](comparison-branches.md).
It counts all instruction/edge/terminator consumers; escaped/shared Booleans,
intervening operations and mismatched storage widths retain materialization.
The shared comparison routine preserves existing signed/unsigned logic and
parallel edge copies. Four new rstest cases execute280 direct/escaped predicate
ROMs. Full compiler-library/SM83 validation passed440/440 in127.552s, exit0,
zero skipped (/tmp/koh-fused-compare-final.log). Marked Release windows improve
689->555.88 dots/count and2610.25->2359.5 dots/copied byte. Original190/740-dot
ceilings and remaining100 legacy dispositions stay open. No ABI/API change.
Real-C# int128/wide and PDB checks12/12 passed105.147s, exit0
(/tmp/koh-fused-compare-integration.log); discovery1934/1757/0ignored.
Full current-head debugger/cross-platform acceptance remains unproven.

### Immediate arithmetic in the SM83 backend

Known right-hand constants now use [immediate arithmetic](immediate-arithmetic.md),
avoiding right-frame addressing and HL increments while preserving multi-byte
carry/borrow. Four new rstest cases run800 independently checked ROMs across
32/64-bit values, all five operations and frames beyond127-byte offsets.
Compiler library plus the full SM83 target passed436/436 in127.245s, exit0,
zero skipped (/tmp/koh-alu-immediate-final.log). Marked Release costs improve
709->689 dots/count and2666.25->2610.25 dots/copied byte; original190/740 ceilings
remain open. Real-C# int128/wide arithmetic8/8 passed98.049s, exit0
(/tmp/koh-alu-immediate-wide.log). Discovery1930/1753/0ignored.
No ABI/API change or additional legacy disposition credit.

### Isolated source-loop timing

Test-only [marked source windows](loop-costs.md#marked-source-windows) now exclude
native wrapper and result observation. Counted70900T/100=709T; copy85320T/32=
2666.25T including original source initialization. Empty marker pair96T, not
subtracted as an assumed identical cost. Original190/740-dot ceilings stay open;
no new disposition credit or production changes. Expanded225/225 passed31.807s,
exit0,207 explicitly unselected (/tmp/koh-loop-window-final.log). Discovery1926/
1749/0ignored. Ledger remains465/565,100 outstanding. This identifies a real
codegen gap for subsequent optimization rather than blaming wrapper overhead.

### Loop cost baseline and remaining timing gap

One counted-store baseline method now has a [native replacement](loop-costs.md).
The separate190/740-dot ceilings remain open. Native inclusive cost probes
measure71444 T-cycles for100 stores and166056 for32 copied/individually checked
bytes; harness and observation overhead prevent claiming equivalent windows or
exact ceiling violations. No timing assertion was weakened. Expanded validation
passed224/224 in30.101s, exit0,207 explicitly unselected (/tmp/koh-loop-cost-final.log).
Discovery1925workspace/1748compiler/0ignored. Ledger465/565 (464 replacements,
one DELETE),100 outstanding. Next isolate hot-loop windows before codegen changes.

### Loop exit and saved values

Six Sm83LoopCodegenTests methods have [native replacements](loop-exit-values.md).
288 added Debug/Release ROMs preserve original exit/call/nested/break/old-value
observations and add zero trips and byte-total truncation. Every observed store
and packed caller live value is independently checked; runtime and bound inputs
run before/after optimization. The full rewrite fixture now runs2448 ROMs.
Compiler-library plus fixture validation passed222/222 in27.956s, exit0,
207 explicitly unselected (/tmp/koh-loop-values.log). Stable fmt, CSharpier and
diff checks passed. Ledger464/565 (463 replacements, one DELETE),101 outstanding.
Other14 loop-codegen methods, including190/740-dot ceilings, remain open.
No production compiler/emulator changes or relaxed timing assertions.

### Guarded and sequential copies

Three public RedundantCompareLoopTests methods now have [native replacements](guarded-copies.md):
112 added Debug/Release ROMs check the full inclusive guard window, zero lengths,
exact source/destination accesses and two independent sequential copies. Runtime
and bound inputs run before/after optimization. The whole rewrite fixture now
executes2160 ROMs. Compiler-library plus fixture validation passed222/222 in22.468s,
exit0 (/tmp/koh-guarded-copy-final.log);207 explicitly unselected. Stable fmt,
CSharpier and diff checks passed. Ledger458/565 (457 replacements, one DELETE),
107 outstanding. Real FlushRun/private compare-graph and loop timing replacements
remain open; no production changes or relaxed timing ceilings.

### Generic delegate flag validation

The native delegate binder now rejects [malformed generic parameter flag words](delegate-parameter-flags.md)
without rejecting already-working exact covariant/contravariant delegate calls.
Four new Debug/Release rstest cases run eight valid ROMs and reject24 malformed
bindings with caller/IL diagnostics. Metadata scans use the existing work budget;
zero-arity delegates incur no extra scan. Interface variance, cross-argument
adaptation and unsupported compound generic shapes remain distinct limitations.
Expanded compiler/reference/delegate/generic regressions pass326/326 in82.642s,
exit0; final caller-context checks pass8/8 in6.593s. This production validation
fix does not add legacy completion credit:455/565 remain resolved,110 outstanding.
Full current-head hosted validation and the whole migration remain incomplete.

### Loop-carried pointer migration

Five [pointer-phi/walk source methods](pointer-walks.md) now have native
replacements for19 lexical assertion sites. Real Debug/Release CIL selects both
incoming bases, walks loads/stores, preserves exit pointers/counters and checks
every physical byte access. Runtime/bound inputs before/after optimization add
192 ROMs; the shared rewrite fixture now executes2048. Register names, private
fusion/edge-sync structures and static frame pokes are not copied.

The stale backend audit's eight copy cases were reconciled with the existing
complete central matrix; no duplicate completion credit is taken. Fresh copy
checks passed12/12; final combined library/catalog/ISA/rewrite/copy validation
passed257/257 in51.763s, exit0,691 explicitly unselected. The ledger is455/565
complete,110 outstanding; separate loop timing, runtime/product and final hosted
gates remain open. No production compiler/emulator/API code changed.

### Indexed pointer/liveness migration

All five [GEP/fusion source methods](indexed-addresses.md) now have native
replacements for7 assertion sites. Real Debug/Release CIL runs with runtime/bound
inputs before/after optimization:320 added ROMs check physical base+index,
page crossings, signed offsets, multiple uses and pointer/data preservation
across a real call. Full64 return data and exact watched memory traces replace
private frame pokes/fusion flags. The complete rewrite fixture now runs1856 ROMs.
Expanded library/catalog/ISA/rewrite selection passed245/245 in35.833s, exit0.
The ledger is455/565 complete,110 outstanding. Production compiler/emulator and
ABI are unchanged; pointer-loop budgets and final migration gates remain open.

### Independent ISA encoding coverage

Five [encoding/parity source methods](encoding-contracts.md) now have native
replacements for34 assertion sites. Four rstest cases preserve all33 named byte/
operand vectors; the exhaustive boundary test replaces local decoder agreement.
The execution matrix adds immediate/pair arithmetic and every conditional JP,
including half-carry, carry, Z preservation and pair wrap:14224 probes. Expanded
compiler/library/disassembly/effect/Roslyn rewrite validation passed245/245 in
32.462s, exit0. The ledger is455/565 complete,110 outstanding; no production
compiler/emulator/debugger code changed and full migration remains incomplete.

### Comparable compiler-core measurements

The [performance checkpoint](compiler-performance.md) now records identical
Roslyn inputs compiled by both implementations before legacy deletion:54 final
fresh-process samples across three common kernels, first-call core/wall times,
calibrated child peak RSS and compiler/deployment byte sizes. Six separately
linked ROMs pass independent scalar, aggregate, VRAM/map and idle-control checks
in the existing emulator. The measurement collector's inherited Python RSS floor
was detected and corrected before recording results. An old returning-entry
execution difference is documented, not adopted as a native requirement.

This is a scoped Linux baseline: no whole-SDK/full-game/warmed-JIT/cross-host speed
claim. Native emitted payloads are larger for these kernels and are recorded
alongside the lower first-call time/RSS observations. Broader performance/product
acceptance remains open; the disposition ledger is455/565 with110 outstanding.
The optional host-native workflow adds no CI-per-commit job or container.

### Executable machine-effect migration

The remaining15 `MirDecoderTests` methods now have [native replacements](machine-effects.md)
for66 assertion sites. Nine rstest cases run13952 fresh probes covering loads,
ALU, INC/DEC, every CB encoding, branches/calls/returns, AF push/pop, MMIO,
post-increment/decrement, DI/EI delay and RETI. Independent Rust expectations
check every register/flag, PC/SP/IME, stack and exact ordered data accesses.
Only the existing C# test adapter gains a bounded raw-state command; production
emulator/debugger/compiler code is unchanged. No constructor/boot defaults or
private effect bitsets become specifications. All19 source decoder methods now
have dispositions; the full ledger is455/565 complete,110 outstanding.
Expanded library/catalog/boundary/effect/real-Roslyn rewrite validation passed
241/241 in29.956s, exit0, with703 explicitly unselected cases. Full migration,
performance comparison and final hosted validation remain incomplete.

### Instruction/input boundary migration

Four of19 old MIR decoder methods now have [native replacements](instruction-boundaries.md).
Two rstest cases validate all512 base/CB encoding vectors through canonical ELF,
the Rust linker and the existing debugger: exact bytes, lengths, next addresses
and all11 invalid base opcodes. A mixed stream checks every original byte/offset.
The actual CIL input boundary now rejects every proper truncation of all218 SDK
catalog encodings. No private MIR/opaque-clobber model or second decoder is added.
The other15 methods now have [separate execution proof](machine-effects.md). Focused decoder/catalog/debugger
validation passed43/43 in10.504s, exit0;683 cases explicitly unselected. The ledger
is455/565 complete (454 replacements, one DELETE),110 outstanding. Full migration
and current-head hosted acceptance remain incomplete.

### Native three-platform validation checkpoint

Full run44193814588 on567a192 completed all native jobs successfully: Linux
1891/1891 (1304.198s), macOS1891/1891 (1123.478s), Windows1889/1889 (2206.247s),
zero skipped. Separate doctests, release tools, graphics SDK smoke and LSP/native
artifact publication passed in each job. Both basic and real2048 SDK source-debug
cases passed on Windows/macOS, validating the physical-document identity fix.
The Windows count excludes only two documented Unix assembler tests.

Both .NET jobs still failed114 legacy compiler tests each and blocked dependent
benchmark/AOT jobs. Thus the overall pipeline is not green. Later mapper/rewrite
commits change tests/docs only; their newly added coverage has focused local
proof and still needs current-head full validation. Exact scope, primary logs
and run link: [CI checkpoint](../ci-validation.md#recorded-native-compiler-checkpoint).
Scoped common-kernel measurements are now [recorded separately](compiler-performance.md);
no whole-SDK or game-performance improvement is claimed.

### Machine rewrite contract migration

All38 `Sm83PeepholeTests` methods now have [native dispositions](rewrite-legality.md):
one PORT for six independent ISA lengths,37 REPLACE decisions for private edit
predicates. Actual Roslyn Debug/Release methods run with runtime/bound inputs
before/after optimization:1536 ROMs check full64-bit carry/borrow/shifts, live
call/branch results, pointer increments/decrements and exact native bus traces.
A small observer in the existing C# test adapter retains overwritten write values
and intervening reads; production emulator code is unchanged. The chosen typed-IR
memory-effect and call ABI contracts replace private byte edits/flags/whitelists.

Six native driver cases independently check known opcode bytes, lengths and next
instruction addresses through the existing debugger API. The expanded library/
rewrite/ISA/mapper/production-banked-call selection passed236/236 in38.223s, exit0
(`/tmp/koh-rewrite-contracts-final.log`), with695 explicitly unselected cases.
The ledger is455/565 complete (454 native replacements, one DELETE),110 outstanding.
No compiler-owned case is deferred; no private byte edit or cycle improvement is
declared part of the public contract.

On preceding567a192, full native validation has now passed1891/1891 on Linux
(1304.198s) and macOS (1123.478s), zero skipped. The macOS log confirms both real
2048 SDK source-debugger cases and the release native graphics-run smoke test
passed, alongside all other native cases. Primary logs:
`/tmp/koh-full-567a192-{linux,macos}-native.log`. Windows remained running at this
checkpoint; these older-head results do not validate the current test additions
or turn the failing legacy .NET/full pipeline green.

### Mapper-state contract migration

All six `RedundantBankSelectEliminationTests` methods now have
[native replacements](mapper-state.md) for17 assertion sites (six verifier,
11 private pass/store-count checks). Actual Debug/Release CIL executes runtime
and bound selectors before/after optimization:80 ROMs read distinct payloads in
banks0/3/4/255/259. A valid callee changes the mapper, ordinary WRAM remains
independent, and RAM-enable/high-ROM-bit writes have their actual distinct MBC5
effects. The Rust linker places canonical ELF data sections and finalizes ROMs.
Code is checked to remain in ROM0 for these raw mapper operations; this does not
claim a managed banked-data API or safety for changing the executing ROMX bank.

The expanded library/mapper/production-banked-call/optimizer selection passed
230/230 in37.535s, exit0 (`/tmp/koh-mapper-contracts-final.log`), with693 explicitly
unselected cases. It retains the existing DMG/CGB managed-call and recursion
contract. The ledger is383/565 complete (382 native replacements, one DELETE),
182 outstanding; no compiler-owned coverage is deferred.

### Hosted source-debugger path correction

Full run44187413725 on e412837 completed: Linux native1880/1880 passed; macOS
1876/1880 passed (four source-debugger failures); Windows1872/1878 passed (those
four plus two2048 SDK source-debugger failures). Primary logs are
`/tmp/koh-full-e412837-{linux,macos,windows}-native.log`. Both .NET jobs also
failed114 legacy compiler cases each. Full validation is therefore not green.

A controlled local source-directory alias reproduced all four basic debugger
failures, with PDB physical paths differing from the requested alias. The Rust
tests now independently require the same physical source file and exact line,
then use the recorded document spelling for DAP requests and caller comparisons.
Unix tests always exercise an actual private symlink. No basename fallback,
provenance rewrite, compiler algorithm or debugger rewrite is introduced.
The initial alias/symbol-free selection passed6/6 in21.399s, exit0;
[source-debug evidence](source-debugging.md#source-document-identity-across-hosts)
records the red cases and remaining hosted revalidation requirement.
The final selection, including both real2048 SDK debugger cases, passed8/8
in125.882s, exit0 (`/tmp/koh-debug-source-identity-final.log`), with488 explicitly
unselected cases. Rustfmt/diff checks passed. Hosted Windows/macOS proof remains
outstanding; the local alias reproduction establishes the corrected fixture
identity assumption without changing the production mapping contract.

### General optimizer contract migration

All36 `OptimizationTests` methods now have [native replacements](optimization-semantics.md)
for132 original assertion sites (21 verifier,15 numeric result/store,96 private
graph/pass observations). Real Roslyn Debug/Release methods run with runtime
arguments and bound IR constants, before/after production optimization. Alongside
bounded dead/store-reachable phi cycles this gives2728 ROMs:2568 successful and
160 defined arithmetic/null faults. Native bus reads remain observable; called
mutations, distinct locals, aliased element stores and neighboring bytes are
checked independently. Explicitly selecting otherwise dead unsupported code
diagnoses; the valid leaf root includes exactly its transitive callee.

The expanded selection passed629/629 in34.831s, exit0
(`/tmp/koh-optimization-final.log`), including complete library/IR targets and
neighboring comparison/conversion/loop execution. Final byte-local normalization
and execution/fault-count guards passed4/4 in19.240s, exit0
(`/tmp/koh-optimization-final-guards.log`). The ledger is377/565 complete
(376 native replacements, one DELETE),188 outstanding. No compiler-owned case
is deferred and no private pass ordering or unmeasured speed improvement is claimed.

The Linux native job of full run44187413725 on older published e412837 completed
1880/1880 in1369.457s, zero skipped, plus separate doctests for all four crates
(zero examples), release native builds and the graphics SDK run smoke test
(1/1,8.420s). Native-backed language-server/native artifacts were published by
that job. Primary log: `/tmp/koh-full-e412837-linux-native.log`. This does not
cover subsequent local changes. Other native platforms remained running; both
legacy .NET jobs failed114 compiler tests, so full validation is not green.

### Loop optimization contract migration

All six `LoopInvariantCodeMotionTests` methods now have native replacements for
23 source assertion sites (five verifier checks and18 private graph/pass checks).
Actual Roslyn Debug/Release methods execute before optimization and after one/two
production optimizer calls:420 ROMs,408 successful and12 division faults. Full
int32 sums and trip counts retain changing induction, conditional entry, nested
dominance and stores observed through invariant pointers. Empty/bypassed loops
do not acquire division faults. The invalid dominance premise in the old nested
fixture is corrected rather than copied. See [source matrix](loop-contracts.md).

Final expanded selection passed595/595 in26.982s, exit0, with192 explicitly
unselected cases (`/tmp/koh-loop-contracts-final.log`). It includes the complete
library and IR targets plus new loop and neighboring narrowing execution cases.
The current ledger is341/565 (340 native replacements, one DELETE),224 outstanding.
No compiler-owned case is deferred and no new optimization/cycle win is claimed.

### Literal-data budget correction

Readonly span literals now charge the existing cumulative per-method 64 KiB
budget before allocating their IR payload. Real Roslyn Debug/Release fixtures
accept exactly sixteen 4096-byte blobs and reject a seventeenth with method/IL
context; the positive case also passes ELF serialization and native linking.
Unreachable over-budget methods do not affect the selected entry. Both negative
cases reproduced the missing check before the fix. Budget and existing literal
ROM tests passed6/6 in15.035s; the final library/span/budget selection passed
231/231 in4.653s, zero skipped, exit0 (`/tmp/koh-span-budget-final.log`).
This closes a resource-validation bug, not an additional legacy disposition.

Follow-up review found the same omission in IR verification and whole-program
reachability accounting. Both now charge readonly-span blobs. Debug/Release
mixed UTF-16/span fixtures accept exactly16MiB of reachable data and reject the
next span payload with member/IL context. Three verifier cases retain the exact
64KiB boundary and reject span-only/mixed excess before emission. Both omissions
were reproduced before fixing them. Final selection:236/236, zero skipped,
11.440s, exit0 (`/tmp/koh-literal-budgets-final-reviewed.log`). The independent
generic-metadata work budget remains unchanged; see [limits and evidence](literals.md).

Full three-platform run44187413725 was started on published head e412837.
Its Linux .NET job completed with114 failures in the legacy C# compiler suite,
including stale intrinsic dispatch for `memfill`, `memcopy` and graphics contracts.
Primary downloaded job log: `/tmp/koh-full-linux-dotnet-job.log`. This is an
unresolved full-validation failure, not grounds for restoring compiler algorithms
in C# or skipping coverage. Native Linux/Windows/macOS jobs were still running
at this checkpoint. Final compiler/test deletion and current-head full acceptance
remain required; no full-validation pass is claimed.

### Faster iteration and full validation

[Metadata images](fixture-images.md) now build once per serialized nextest run
and return private byte buffers. New-run, failed/incomplete-build and disabled
reuse checks remain explicit. All93 consumers/cache checks passed34.993s; the
same six warmed reader cases fell26.890s→4.508s. No compiler performance win is
claimed. The raw-byte fixture cache does not replace source-debugger builds.

Per-push [CI checks](../ci-validation.md) now select all Rust targets except the
three expensive compiler binaries `driver`, `cubes` and `sdk`. All cases remain
discoverable; the default local command and three-platform full validation are
unfiltered. Opening/reopening a draft can start full validation before manual
dispatch reaches `master`; repeated pushes run the two quick Linux jobs.
The exact fast selection passed1274/1274 in111.792s, exit0
(`/tmp/koh-ci-fast-cached.log`), including four new cache checks. The pre-cache
selection passed1270/1270 in414.482s; these are regression-loop measurements,
not full-workspace or compiler-throughput benchmarks. Required check names and
read-only permissions remain unchanged; branch protection was inspected, not edited.

Full CI on the published literal head75951e2 subsequently passed1869/1869 in
1821.803s, zero skipped, plus separate rustdoc and .NET fast checks. Primary log:
`/tmp/koh-ci-75951e2-completed.log`. Newer test/CI increments still need their own
full validation, and the migration remains incomplete.

### Narrowing contract migration

All19 `NarrowPassTests` methods and73 assertion sites now have
[native replacements](narrowing.md). Five rstest cases execute3224 ROMs with
runtime/constant inputs before/after optimization. Full-width packed observations
retain inner sums, both byte/word sinks and actual loop trip counts; signedness,
comparison constants, shift masks and division faults have independent Rust oracles.
The ledger is335/565 complete (334 native replacements, one DELETE),230 outstanding.
No compiler behavior or coverage is deferred. Named-pass/IR-width assumptions are
replaced; the separately inventoried C# narrowing cost benchmark remains open.

The expanded selection passed299/299 in24.238s, exit0
(`/tmp/koh-narrowing-acceptance.log`). Final shape/count guards passed5/5 in16.028s,
exit0 (`/tmp/koh-narrowing-final.log`). Source accounting:
`/tmp/koh-narrowing-source-assertions.log`.
Discovery finds1874 workspace cases,1697 compiler cases, zero ignored
(`/tmp/koh-narrowing-discovery.json`); rustfmt and diff checks passed.

The preceding full CI on113eaf2 completed1856/1856 native tests in1785.687s,
zero skipped, with separate rustdoc and .NET fast checks successful. Primary log:
`/tmp/koh-ci-113eaf2-completed.log`. The literal commit75951e2 is now published;
its CI44177800888 subsequently passed1869/1869 as recorded above. The newer
test/CI batches still require full validation. No controlled compiler speedup is claimed.

### Array and UTF-8 literal acceptance

The [literal matrix](literals.md) closes the remaining four `CilLiteralTests`
methods and their16 assertion sites. All six methods in that source group now
have native replacements. The ledger is316/565 complete (315 native replacements,
one DELETE),249 outstanding; nothing is physically retired yet.

Native readonly byte-span views now retain a ROM address and logical int32 length
through calls, copies and returns. UTF-8 terminators remain outside that length;
negative, empty and past-end indexing fault. The IR rejects readonly writes,
numeric conversions and fabricated references, and preserves unused bounds checks.
Mutable array literals instead retain fresh RAM storage with ROM initializer data;
the tests check mutation isolation rather than unconditionally promoting arrays.
The real framework assembly also exposed and fixed an inflated generic-constraint
work charge; actual scans remain budgeted before execution.

Final focused acceptance passed249/249 in58.278s, exit0
(`/tmp/koh-literals-final.log`), including48 new ROM executions,12 unsupported-span
diagnostics, nine native IR cases, all220 unit cases, and generic/128-bit neighbors.
Workspace discovery finds1869 cases, including1692 compiler cases, zero ignored
(`/tmp/koh-literals-discovery.json`). This is not a full workspace/platform pass.
The final signature review passed233/233 unit/span/literal cases in11.791s,
exit0 (`/tmp/koh-literals-reviewed.log`). Separate workspace doctests succeeded
for all four crates (zero examples); rustfmt and diff checks passed.

### 128-bit implementation increment

[Framework Int128/UInt128 operators](int128.md) now lower to typed native integer
operations with 16-byte slots, arguments and return buffers. The initial real C#
fixture exposed and corrected framework value-type bases encoded as TypeDefs,
then exercised full-width arithmetic, comparisons, recursion and static storage.
At682bd2e the source-test ledger remained309/565. The subsequent storage/fault
acceptance below closes three source methods; no full-migration completion is claimed.

Focused acceptance passed233/233 cases in90.913s, exit0, including all220 compiler
unit cases,64 new128-bit ROM executions, eight unsupported-operation diagnostics,
existing32/64-bit arithmetic vectors and allocation/call regressions. Primary log:
`/tmp/koh-int128-acceptance.log`. This is focused coverage, not a full workspace run.
After C# formatting the four new cases passed again in20.126s, exit0
(`/tmp/koh-int128-formatted.log`). Workspace discovery finds1856 cases, including
1679 compiler cases, zero ignored (`/tmp/koh-int128-discovery.json`). Rustfmt and
diff checks passed.

The next128-bit acceptance batch fixes typed token classification for `ldobj`,
`stobj` and array operations, and shares the numeric classification with aggregate
layout. References, 16-byte array elements and independent struct copies now have
native execution coverage. Full-width positive/negative constants, narrowing,
high-bit divisors, signed minimum, zero/overflow faults and the real managed host
oracle complete [three source dispositions](int128.md#source-test-disposition).
The ledger is312/565 complete (311 native replacements, one DELETE),253 outstanding.
No useful assertion is deleted or deferred. The batch runs120 ROMs plus16 managed
invocations. Focused units/aggregate neighbors passed228/228 in40.837s; the final
four expanded cases, including missing-reference diagnostics, passed in32.867s.
Actual exit codes0; logs `/tmp/koh-int128-boundaries-acceptance.log` and
`/tmp/koh-int128-boundaries-final.log`. Broad metadata mutation and checked128-bit/
float-conversion policy remain open; this is not full compiler acceptance.

Published head0413235 completed hosted CI44162995161 successfully:
1852/1852 native tests, zero skipped,1444.708s; separate Rust documentation tests
and the .NET job also succeeded. Primary log:
`/tmp/koh-ci-0413235-completed.log`. This predates the 128-bit increment.

### Regression setup cost

Execution hosts build incrementally in their ignored fixture output directories.
The serialized `roslyn` nextest group reuses each successful host build within
one [nextest run ID](https://nexte.st/docs/configuration/env-vars/#environment-variables-nextest-sets).
Each new run invokes MSBuild again; failed builds never publish a reuse stamp.
Runs outside that group always invoke MSBuild. Test inputs remain fixed during
a run, and each test still owns its temporary ROMs and mutable outputs. Keep
builds serial in the checkout, as for the existing shared project references.

On this Linux host, the same eight static/captured delegate execution cases
passed in46.865s before,42.954s with incremental host builds, and26.850s with
reuse within the run (about43% faster than baseline). The last log contains one
EmulatorHost build. Logs: `/tmp/koh-host-speed-before.log`,
`/tmp/koh-host-speed-after.log`, `/tmp/koh-host-speed-run-cache.log`.
This measures the selected regression cases, not full-suite speed or compiler
performance. No test selection, assertion, timeout or concurrency limit changed.
Another run passed8/8 generic execution and source-breakpoint/machine-inspection
cases in53.513s (`/tmp/koh-host-speed-consumers.log`), rebuilding EmulatorHost
and DebuggerHost once each for that new run. Rustfmt check passed.

Game fixtures now also reuse one successful Roslyn build per fixture,
configuration and serialized nextest run. Each case copies the complete `bin`
and `obj` trees and source files into its own temporary directory. DLL/PDB,
source and reference-manifest mutations cannot affect later cases. Cached
sources remain readable for Portable PDB provenance. `SourceDebug` retains
private builds because its debugger contracts require exact temporary source
paths. Empty local Directory.Build files preserve the former isolated fixture
settings; referenced repository projects keep their own settings. New runs and
runs outside the serialized group rebuild, and failed builds publish no stamp.
Only one generated build per fixture/configuration is retained on disk.

The same12 CGB HAL, aggregate-return and source-debugger cases passed in48.501s
before and37.344s after (about23% faster), using warmed local build outputs.
Fixture builds fell from12 to8; all four debugger cases still build privately.
The earlier cold baseline was61.033s and is not used for the comparison.
No case, assertion, timeout or concurrency limit was removed. The broader
memory-copy/fill, delegate, PDB and isolation selection passed86/86 in70.861s;
both isolation cases also passed outside nextest in13.03s. The initial broader
run exposed the missing reference manifest; copying `obj` fixed the cause and
the isolation cases now mutate that manifest as well. Logs:
`/tmp/koh-fixture-reuse-before-warm.log`,
`/tmp/koh-fixture-reuse-after-final.log`,
`/tmp/koh-fixture-reuse-consumers-final.log`, and
`/tmp/koh-fixture-reuse-outside-nextest.log`.
This is a scoped regression speed measurement, not compiler performance or a
whole-suite/platform speed claim. Reproduce the compared selection with:

```sh
cargo +stable nextest run -p koh-compile --test driver --locked --success-output final -E 'test(cgb_hal::) | test(struct_returns::) | test(debugger_stops_on_native_csharp_source)'
```

### Field-signature budget before binding

Completed CI44117991696 on3d44c0b ran1,808 cases:1,807 passed, one failed,
zero skipped; the .NET fast job passed. The original oversized-field-signature
test reached the newer rank32 rejection before accumulating the intended byte
budget. Class layout now charges all instance-field signature bytes before
parsing/binding fields. It retains the same byte limit, skips static fields and
does not double-charge. Unsupported array ranks remain unsupported.

The unchanged failing test reproduced locally, then all54 layout cases plus
four closed-type and two interface-budget cases passed60/60 in266.024s, exit0.
Rustfmt check passed. Logs: `/tmp/koh-ci-3d44-completed-plain.log`,
`/tmp/koh-layout-byte-budget-red.log`, `/tmp/koh-layout-byte-budget-regression.log`.
The earlier132-case interface regression predates this separate budget fix.

[Closed base-class layouts](closed-types.md#closed-base-classes) now substitute
declaring arguments through inherited storage, including nested and referenced
base types. [Inherited method references](closed-types.md#inherited-method-references)
now bind caller scopes and exact closed base identities; field references retain
their declaring-type requirement. [Closed reference dispatch](closed-types.md#closed-reference-dispatch)
now supports nongeneric virtual/interface methods, overrides and retained virtual
callbacks. [Instantiated invariant interface contracts](closed-types.md#instantiated-invariant-interface-contracts)
now preserve declaring scopes for reference and constrained value calls, including
explicit/generic value methods and foreign aggregate returns. [Generic methods on
closed reference receivers](closed-types.md#generic-methods-on-closed-reference-receivers)
now retain declaring scopes through overrides, explicit/default bodies, callbacks
and inherited MethodImpl selection. The selected interface/generic/virtual
regression passes133/133 in409.320s; final consumers pass8/8 in39.404s.
These are scoped results and do not close another legacy disposition.
Review also corrected selected-body generic arity validation; the final
library/reference/closed-generic selection passes248/248 in192.823s.
Discovery lists1,819 cases; seven new cases are unignored. Published
CI44128918287 on the preceding cd41567 passes1,812/1,812 with zero skipped,
plus separate rustdoc and .NET fast checks. It does not validate this newer unit.
Closed generic value receivers now support [constrained calls](constrained-calls.md#closed-generic-value-receivers)
to nongeneric interface contracts, preserving caller mutation and explicit/generic
method selection. Variance and boxing/default-interface value fallback remain unfinished.

Current compiler increment adds distinct binary32 IR, explicit bit transport,
float constants/calls/joins/byrefs/fields/arrays and metadata-selected software
addition/subtraction/multiplication/division, direct sign-bit negation and ordered/unordered comparisons.
Actual Debug/Release DMG/CGB execution and SDK paths with spaces are
covered. [Checked numeric casts](checked-casts.md) now guard integer and floating
conversions; [checked integer addition/subtraction](checked-arithmetic.md) now uses
the same guard, including [checked multiplication](checked-multiplication.md).
Managed-reference arithmetic, catchable exceptions and bare internal-F expressions remain unsupported. Floating
remainder now executes through the [exact shared runtime core](floating-remainder.md). All22 methods in the legacy CilFloatTests group now have
native replacements (74 original parameter-expanded cases,504 explicit expanded
assertions). Five [IR construction/verifier methods](ir-construction.md) now have
native replacements. The [IR round-trip group](ir-roundtrip.md) adds six native
replacements and one obsolete private-parser decision. The current ledger is
509/565 dispositions complete (508 native replacements, one DELETE),56 outstanding,
including four [live-value preservation replacements](liveness.md).
The23 [allocation-independent backend methods](allocation-contracts.md) now
replace private register/slot/byte-shape expectations with executable arithmetic,
pressure, caller/callee, backedge and source-map contracts. Nine new cases run460
ROMs; the focused selection including malformed-IR guards passed40/40 in14.775s,
exit0 (`/tmp/koh-allocation-contracts-acceptance.log`). The separate128-bit helper
and both frontend arithmetic methods now have [full-width replacements](int128.md#source-test-disposition).
Completed [CI44153557742](https://github.com/retro-dev-tools/koh/actions/runs/44153557742)
on preceding1acc333 passed1,835/1,835 in1409.395s, zero skipped, plus separate
rustdoc and .NET fast checks. This predates the two metadata/owner fixes and this
backend acceptance batch. No controlled whole-suite speedup is inferred.
The34 [scalar control-flow contracts](control-flow-scalars.md) now retain all37
original numeric assertions and strengthen carry/borrow/sign boundaries through
188 native executions. The remaining20
[memory/call/boot/phi contracts](control-flow-memory.md) now account for30 assertion
sites, closing the54-method source file. Four real-C# cases execute84 ROMs;
three explicit phi cases execute24 ROMs, with existing sum/switch coverage reused.
The final formatted-input selection passed11/11 in18.752s, exit0; discovery lists
1,835 workspace cases, including1,658 compiler cases with zero ignored. Logs:
`/tmp/koh-control-flow-memory-formatted.log` and
`/tmp/koh-control-flow-memory-discovery.json`.
Completed [CI44147614067](https://github.com/retro-dev-tools/koh/actions/runs/44147614067)
on preceding378e4f2 passed1,826/1,826 in1395.579s, zero skipped, plus separate
rustdoc and .NET fast checks. This predates the fixture-reuse and current control-flow
increments; full validation of those changes remains required.
The remaining five [CGB HAL methods](cgb-hal.md) now check actual referenced
code, both VRAM banks, all palette bytes and CGB-only CPU writes.
See [float lowering contract and evidence](float-lowering.md).
The [multiplication correction](softfloat-multiplication.md) replaces inherited
subnormal flushing and missing special-value handling before enabling CIL `mul`.
It adds26 boundary and256 exponent-stratified numeric observations plus56 native
multiplication executions. Subsequent [division work](softfloat-division.md) adds
another26+256 numeric observations and64 division ROMs, closes the mixed arithmetic
rows; subsequent remainder work replaces the earlier rejection with execution.
No floating feature has been retired. [32-bit conversions](float-conversions.md)
now cover signed/unsigned32/64-bit and16-bit-native inputs, with explicit
clamping/NaN choices and narrower integer truncation. Wide conversion rounds
once without binary64 intermediates and verifies all eight returned bytes in
WRAM. At that checkpoint, additional integer-width coverage did not change the
148-method ledger. The [binary64 core](binary64.md) now adds distinct typed
64-bit float constants/storage/calls, corrected software addition/subtraction,
negation and ordered/unordered comparisons. Unchecked integer↔binary64 conversions now use direct53-bit rounding and
explicit32/64-bit clamping/NaN policy. Binary32↔binary64 casts
now preserve subnormals and round once at the final storage scale. Multiplication now uses exact
two-limb products with final-scale rounding. Division now uses bounded64-bit long
division with one rounding step at the final normal/subnormal scale.
Final comparison regression passes606/606 selected tests156.717s, exit0;
workspace all-target/all-feature check and separate rustdoc pass. Hosted
cceba4c run43981727648 stopped after1073 passes on an obsolete `FloatArray`
rejection expectation; the replacement now checks real allocation, payload,
heap/bank isolation and frame guards in all four configurations. Updated
commits still require a fresh complete hosted run.

Earlier AddF32 checkpoint fixes inherited subnormal/special-value errors
observed in both managed execution and a Rust-compiled ROM. Raw1+raw1 now returns
raw2; NaNs are quieted and infinity/zero/overflow/rounding boundaries are checked.
Expanded14/14 passes64.421s, exit0 (/tmp/koh-softfloat-ieee-first.log):80 actual
banked ROMs and415 managed observations. Independent Rust oracles and numerical
review support the correction; sampled coverage is not exhaustive IEEE proof.
API/compiler/emulator implementations are unchanged apart from this numeric
runtime body. Broader floating arithmetic/conversion lowering was unfinished; ledger
at that stage was137/565 complete,428 partial/outstanding, zero physically retired.
See [algorithm rationale and evidence](softfloat-addition.md).
Formatted final14/14 passes45.734s, exit0
(`/tmp/koh-softfloat-ieee-final.log`). Rustfmt/CSharpier pass. Hosted baseline
15d0cff run43968285185 has completed1373/1373 in1953.920s, zero skips, with
separate rustdoc and .NET fast tests successful. This proves that baseline;
4741e89 run43974339605 has also completed successfully. That Float32
compiler checkpoint still required its own hosted CI evidence.

Previous software-runtime test migration closes23 methods (3CilKohRuntimeTests,
20SoftFloatDifferentialTests):24 actual banked AddF32 ROMs plus145 independent
managed-library numeric checks covering every original85 parameter row. The
12 Rust cases pass12/12 in53.059s, exit0 (/tmp/koh-softfloat-first.log). Review
confirms no lost inputs/predicates; no production code/API/emulator changes.
The ledger at that stage was137/565 complete,428 partial/outstanding, zero physically retired. Float/
double CIL lowering and runtime-key dispatch were unfinished at that checkpoint. See the
[complete dispositions and numeric-domain rationale](softfloat-bits-tests.md).

The duplicate local15d0cff full run was intentionally interrupted after555 passes
(one SIGINT,817 not run, exit100), while hosted43968285185 for the same head was
verified actively running Rust tests with .NET fast tests successful. It is not
reported as a full pass or a code failure. Hosted full-suite results remain
now complete as recorded above; the23-method test increment was not present in
that baseline snapshot.

Previous accumulator optimization fixes exact Sum(500)=66 in Debug/Release ×
DMG/CGB without changing the ABI or consuming heap. Eligibility excludes memory,
addresses, other/multiple calls, division and fault terminators. Signed8/U16/I32/
I64 fixtures and a signed-widening exclusion independently execute; first expanded
selection passes22/22 in34.306s, exit0 (/tmp/koh-accumulator-widths.log).
All6 CilRecursionTests methods now have complete ports: ledger then114/565 complete,
451 partial/outstanding, zero physically retired. General recursion still has
checked capacity limits. See [proof and design](accumulator-recursion.md) and
[per-method matrix](recursion-tests.md). Full migration remains incomplete.
Final source/debugger/unwind/CLI-stack/backend selection passes14/14 in122.102s,
exit0 (`/tmp/koh-accumulator-debug-stack-final.log`). Optimized Sum source ranges
survive ELF/link/KDBG. The broader run's obsolete pure-sum overflow expectation
is replaced by non-associative continuation depth10=5/depth10000 trap coverage;
that interrupted614-case run is not claimed green. Rustfmt/CSharpier pass;
full workspace/all-features verification is pending separately.

Previous recursion audit closes four methods with native Fib(10)=55 and mutual
parity(10/7) execution across Debug/Release × DMG/CGB. Full returns, actual MMIO,
banked helpers and frame/heap/bank guards pass4/4 initially in22.987s, exit0
(`/tmp/koh-recursion-bounded.log`). The exact Sum(500) reproducer fails with native
stack overflow: Debug frame49 bytes exceeds3,840-byte stack capacity at that depth.
Both depth-success contracts remain outstanding; no overflow test substitutes
for success. Ledger then112/565 complete,453 partial/outstanding, zero physically
retired. See the [recursion matrix and root-cause evidence](recursion-tests.md).
Final recursion/bank-restoration selection passes8/8 in44.783s, exit0
(`/tmp/koh-recursion-final.log`); Rustfmt/CSharpier pass. Production code unchanged.

Previous test closure completes all12 CilLoweringTests methods: ten PORT observable
contracts and two REPLACE private IR assumptions. Four rstest configurations run40
ROMs with464 ordered packet bytes and160 actual MMIO bytes; hand-CIL tests check
both typed stack-join outcomes. The selected lowering/host/struct-return regression
passes53/53 in62.067s; final default-entry strengthening passes6/6 in27.642s, exit0
(`/tmp/koh-cil-basics-entry-final.log`). Rustfmt and CSharpier checks pass. Production
compiler/emulator code is unchanged; the test host adds register observations.
Ledger then108/565 complete,457 partial/outstanding, zero physically retired.
See the [basic CIL per-method matrix](cil-basics.md).

Previous test closure ports all five CilStructReturnTests methods to Rust-driven
Roslyn/PDB/ELF/ROM execution. Factories, copies, chaining, recursion, generic returns
and concrete foreach preserve22 original value comparisons and strengthen them to
39 full-width ordered observations per configuration. Final4/4 passes25.641s,
exit0 (`/tmp/koh-struct-return-final.log`):20 Debug/Release × DMG/CGB ROMs,624 checked
bytes plus native completion/frame/bank/source checks. Production compiler code is
unchanged. Ledger then96/565 complete,469 partial/outstanding, zero physically retired.
See the [per-method matrix and oracle rationale](struct-returns.md). The full
migration and old compiler removal remain incomplete.

Previous constrained-call increment supports class/interface reference cells and
unboxed struct implementations, including explicit MethodImpl and generic method
arguments. Reference cells are dereferenced before normal dispatch; value receivers
retain their original pointer, so mutation/copies preserve C# semantics without
boxing. Prefix/call control flow, closed receiver types and body ownership are
checked. Boxing-dependent/default-interface value fallback and static constrained
dispatch remain explicit unfinished work. See [contract](constrained-calls.md).
Selected library/IR/interface/generic/class-dispatch regression passes524/524 in
252.077s, exit0 (`/tmp/koh-constrained-regression.log`). Final4/4 passes39.612s
(`/tmp/koh-constrained-final.log`), including the late no-box heap assertion and
interface alias case;116 ROMs across Debug/Release × DMG/CGB. Invalid receiver types
and foreign-struct MethodImpl bodies reject. Read-only review found no confirmed
blockers. Ledger91/565 is unchanged; broader migration/removal remains incomplete.

Previous runtime-generic increment retains closed arguments in dispatch identities,
selects raw class/interface/MethodImpl/default bodies through the existing binder,
then specializes selected bodies with the same scoped arguments and constraints.
Class overrides, generic delegates, implicit/explicit/default interfaces and
cross-assembly packed arguments execute independently expected results. Direct
base calls continue selecting their stated body. No new calling ABI or runtime.
Selected library/IR/delegate/default-interface/class-dispatch regression passes
566/566 in496.601s, exit0 (`/tmp/koh-generic-dispatch-regression.log`). Final22/22
passes214.867s (`/tmp/koh-generic-dispatch-final.log`), including ordinary interface
metadata regressions and100 generic ROMs across Debug/Release × DMG/CGB. New null
cases require fault4 before allocation; a former valid virtual-flag rejection now
executes42. Read-only review found no confirmed blockers. Constructed declaring
layouts, boxed receivers, constrained calls and broader generic constraints remain
unfinished. Ledger91/565 is unchanged; this does not complete the compiler migration.

Previous generic-base increment supports statically bound `call`/`ldftn` of generic
virtual bodies, including ordinary C# base calls and base method groups. Dispatch
diagnostics now apply where dispatch is requested, rather than to every virtual
declaration. A deliberately different override cannot replace the selected base
body: full64-bit return, callback42 and field2 are independently checked. Actual
generic `callvirt`/`ldvirtftn` remain explicitly unfinished. Final focused178/178
passes64.527s, exit0 (`/tmp/koh-generic-base-final.log`), including compiler-library,
four generic configurations and existing virtual-dispatch regressions. Formatting
passes; read-only review found no confirmed blockers. Ledger91/565 is unchanged.

Previous pointer-binding increment deliberately extends the earlier CLI-verifiable
pattern boundary to correct nonvirtual `dup/ldvirtftn/newobj` CIL, including closed
MethodSpecs. Typed receiver/signature checks, null-before-allocation and the
no-interior-branch rule remain. Patched nonvirtual and generic targets return21
rather than override42; patched null binding faults4 without heap allocation.
Selected compiler-library/IR/generic/pointer/null regression passes496/496 in86.152s,
exit0 (`/tmp/koh-nonvirtual-ldvirtftn-final.log`). Read-only review found no confirmed
blockers. Ledger91/565 remains unchanged. See [contract rationale](delegates.md).

Previous delegate increment supports closed generic `ldftn` targets using existing
specialization and callback ABI. Scoped raw signatures remain checked independently
of lowered widths. The fixture covers same-method int/ushort instantiations,
reference-library and bound-class targets, side effects and foreign packed structs.
Reused/fresh ELF equality exposed stale callback IDs; callbacks now reset alongside
generic instances. Selected library/IR/delegate/generic regression passes544/544
in362.459s, exit0 (`/tmp/koh-generic-delegate-final.log`), before the final added
signedness mutation. Generic virtual targets and declaring layouts remain unfinished.
The final signedness mutation also passes4/4 in32.431s, exit0
(`/tmp/koh-generic-delegate-signedness-final.log`): a ushort-to-short MethodSpec
substitution rejects at callback binding despite identical lowered register widths.
Ledger91/565 remains unchanged. See [generic contract](generic-methods.md).

Previous compiler increment removes the blanket rejection of nonvirtual generic
instance methods. Existing typed receiver lowering supports object and byref
struct receivers, field side effects and independently specialized widths.
Generic virtual dispatch remains unfinished with an explicit diagnostic.
The initial valid fixture fails in `/tmp/koh-instance-generic-red.log`; first
four-configuration acceptance passes4/4 in32.244s. Ledger91/565 is unchanged.
Final static-signature/virtual-dispatch probes also pass4/4 in31.877s, exit0
(`/tmp/koh-instance-generic-final2.log`). Existing static consistency validation
remains in the shared assembly reader rather than being duplicated.

Previous test closure completes all six CilGenericTests methods. The four remaining
numeric methods now have native full64-bit expression/extension acceptance,
independent expected bytes in both build modes and DMG/CGB execution. Final2/2
passes18.073s; four ROMs compare12 complete64-bit results. Ledger now91/565 complete,
474 partial/outstanding, zero physically retired. See [traceability](generic-methods.md).

Previous construction increment supports closed Roslyn `new T()` through trusted
Activator metadata and the shared ordinary constructor path. Distinct reference
allocations, zero defaults, explicit struct constructors and reference-library
side effects execute in Debug/Release × DMG/CGB. Forged owners retain ordinary
method semantics; invalid signatures and unsupported activation diagnose. Final
selected12/12 passes89.200s; preceding broad572/572 passes315.603s. See
[construction contract](generic-construction.md). Ledger87/565 remains unchanged;
the full compiler migration and legacy removal remain incomplete.

Previous constraint increment validates closed special, constructor, nongeneric
base/interface and parameter constraints before instantiation. Valid recursive
parameter constraints are not rejected by a C#-syntax/CLR assumption: the patched
self-constraint ROM executes42. Malformed value InterfaceImpl and ignored owner0
were reproduced, then fixed; the shared reader validates owner/target indices,
duplicates and owner contiguity. Final4/4 passes29.294s, including48 ROMs and80
metadata-rejection probes across D/R × DMG/CGB. Selected broad572/572 passes
335.233s; subsequent213/213 passes294.935s with reader/reference coverage. These
are selected local results, not final workspace/platform acceptance. See
[constraint contract and limits](generic-constraints.md). Ledger remains87/565,
478 partial/outstanding, zero physically retired.

Preceding pushed0913e97 has completed hosted success:1318/1318 workspace/
all-features tests, zero skips,1398.743s, plus separate rustdoc and .NET fast tests
(run43933091863). Those hosted results predate the constraint increment.

Previous named-generic increment preserves assembly scope through substitution,
then binds individual parameters/returns, locals and typed operands in that scope.
Canonical named keys retain TypeDef+assembly and enum identity. Same-row types in
separate DLLs have distinct packed layouts, class fields, enum widths, interface
dispatch and delegate signatures. No metadata aliases or invented tokens are used.
Broad575/575 passes362.555s, exit0; after the review-fixed indexed arity lookup,
final23/23 passes66.511s. Final interface/delegate fixture additions pass4/4 in
27.890s, with32 Debug/Release × DMG/CGB ROMs, deterministic ELF, source ranges and
bank/stack guards. Logs and remaining constructed-generic boundaries are in
[generic methods](generic-methods.md). Ledger remains87/565 complete,478 partial/
outstanding, zero physically retired; broader generic support is not complete.
Current-source SDK game regression passes2/2 in172.953s, exit0:2048 Debug/DMG
and JRPG Release/CGB actual gameplay/rendering/state checks, not just builds.

Previous generic-method increment specializes closed unconstrained static methods
with supported primitive/string/object/vector arguments. Substitution covers
transitive reference calls, recursion, local/byref storage and typed operands;
distinct instance symbols retain original MethodDef/PDB provenance. Final20/20
passes65.508s, exit0 (`/tmp/koh-generic-final.log`), including28 generic ROMs,
original source-range regressions and two previous generic-rejection cases now
executing the independently expected1. Broad566-case run passed564 with those
two obsolete rejection expectations failing,405.122s, exit100; it is not a green
full suite. Subsequent budget and final fixture checks are covered by final20.
Read-only review found no confirmed blockers. Ledger87/565 complete,478 partial/
outstanding, zero physically retired. Constraints, named arguments, declaring-type
storage and virtual/delegate generic binding remain unfinished, not malformed
metadata or approved feature retirement. See [contract](generic-methods.md).

Previous wide-arithmetic batch closes all14 methods in CilWideArithmeticTests:
all639 original vectors plus unsigned/remainder/mask/fault cases execute through
real Roslyn Debug/Release and native ELF/ROMs on DMG/CGB. Exact ordered packets
replace unordered/equality-only checks. The audit corrects signed minimum%-1 to
the arithmetic fault already used by division, as required by C#'s chosen-overflow
contract. See [matrix and rationale](wide-arithmetic.md). Current ledger85/565
complete,480 partial/outstanding, zero physically retired; counts below are earlier
checkpoints. The rest of the migration, including legacy deletion, remains open.

Final selected regression passes551/551 in328.728s, exit0: all157 compiler-library,
312 IR and80 SM83 cases plus both arithmetic driver cases. Another276 driver
cases were deliberately unselected; this is not a full-workspace result.
Workspace all-target/all-feature check passes9.09s with the existing cubes
unused-helper warning; separate rustdoc (zero cases) and rustfmt pass.
The previous pushed63c690b hosted run43919305645 is completed/success;
those hosted results predate this arithmetic batch.

Current interface increment implements metadata-driven nongeneric interface
dispatch, explicit/inherited MethodImpl bindings, castclass/isinst and typed
interface storage/delegates. Valid inherited bodies follow ECMA semantics rather
than the tested CLR's same-type restriction. Concrete class implementations are
selected even for default interface declarations. Default bodies now use bounded
most-specific interface selection, including re-abstraction and explicit final
body tokens; ambiguous or missing bodies diagnose. Tests directly observe hardware
scroll registers and preserve bank/frame
guards, including large frames and aggregate calls. See [interfaces](interfaces.md).
This closes one obsolete legacy rejection expectation:71/565 complete,494
partial/outstanding, zero physically retired. Earlier ledger counts below record
their respective checkpoints, not the current total.

Final interface selection passes34/34 in419.564s, exit0; it includes actual
2048/JRPG gameplay and source-debugger callers after the runtime descriptor change.
Broader677-case run passed676 with one obsolete interface-rejection expectation;
its positive replacement passes in the final selection. This does not turn that
exit100 run into a full-suite pass. Logs and exact scope are in interfaces.md.

Default-interface extension final10/10 passes95.941s; small/large interface
graph-budget2/2 passes10.508s. The broader503-case selection passed497 with six
Roslyn fixture warning failures; packet visibility was corrected and those six
pass in the final10. Production binder/IR/reference/class-dispatch cases passed.
The preceding pushed a152743 is independently hosted-green:1287/1287 workspace/
all-features cases, zero skips,1610.259s; rustdoc and .NET fast tests also pass
(run43913045233). These hosted results predate the default-interface extension.

Current native caller/source-debug acceptance: budgeted, versioned exact-PC unwind
symbols pass through canonical ELF/KDBG without adding ROM instructions or frame
storage. The existing debugger follows actual saved PC/u16 bank cookies, bounds
stack and ROM accesses, and stops at unknown states. Debug/Release tests force
equal CPU addresses in distinct banks and verify recursive caller source chains.
Eighteen independent malformed/corrupt/bounds/limit states exercise rejection.
Actual2048 SDK Debug/DMG and Release/CGB source breakpoints recover the exact
ordered `TitleScene.Enter → Game.Run → Program.Main` source chain, then resume to
the named Update function. Core DAP acceptance is proven; GUI and arbitrary-PC
unwinding are not claimed. See the [contract](native-unwind.md).

Broader selected255/255 passes438.133s, exit0, including all157 library/all80
SM83 cases,17 driver cases and an SDK graphics launch. Subsequent19/19 driver
cases pass202.899s; final edited helper/ordered-callers11/11 passes99.619s.
All are selected runs, not new full-workspace/hosted results. Review-fixed
aggregate metadata accounting includes symbols even without PDBs; standalone
near records are deliberately unimplemented. The preceding hosted failure was
an outdated CLI/library ELF-parity expectation after automatic PDB discovery.
Commit09d332d preserves byte-for-byte determinism and input-integrity checks in
both explicit no-PDB and default debug modes; all four configurations pass.
Both legacy Gb2048Kdbg methods now have complete native replacements:
70/565 complete,495 partial/outstanding, zero physically retired.
The existing debugger-owned .NET suite separately passes37/37, zero skips; its
build has zero warnings/errors. Workspace check, separate rustdoc and rustfmt pass.

Previous debugger acceptance increment: Rust tests drive the existing C# DAP on
native Debug/Release ROMs through real source breakpoints, exact top-frame source,
registers, readable symbols and ROM-matching disassembly. The same debug session
continues to `__koh_exit`, returns42 without a fault and restores SP D000. Genuine
symbol-free root PE files compile and execute in both configurations, closing
`Gb2048KdbgTests.Frontend_DoesNotThrow_WhenAssemblyHasNoPdb`. Final focused
PDB/CLI/debugger regression passes10/10 in69.777s, exit0, with243 intentionally
unselected driver cases; this is not a workspace or cross-platform result.
Read-only review caught shared reference rebuilds in the no-PDB test; the corrected
test rebuilds only the temporary root project against an unchanged DLL copy.
Caller-stack and real game acceptance were still open at that checkpoint.
Ledger then:69/565 complete,496 partial/outstanding, zero physically retired.

Previous source-mapping increment: validated PDB inputs now reach surviving IL
origins, emitted byte ranges, canonical ELF and linker KDBG metadata. Native CLI
adjacent/explicit discovery and disabling are integrated into the SDK, with
PDB-aware incremental inputs and presence/path stamps. Readable method aliases
retain module/token identity. Hidden code, prologues and internal helpers are
unmapped; raw/expanded source metadata has explicit resource limits.
The broader selected regression passes254/254 in416.277s, exit0, including all
compiler-library/SM83 cases in the selection, PDB/CLI cases and six real game
cases. SDK Debug/Release passes2/2 in125.502s, exit0, including real project
reference maps, PDB timestamp copying/invalidation and debug toggling. Initial
MSBuild item-expression and output-PDB timestamp-probe failures are recorded in
the [source-debugging evidence](source-debugging.md). No assertions were relaxed.
Read-only review found no confirmed implementation defect. At that checkpoint,
actual debugger acceptance and a genuinely symbol-free assembly case remained
open; no legacy debug method was claimed closed. Ledger then remained68/565.

Remaining work is tracked as four acceptance milestones: source debugging;
compiler/runtime/hardware and games; all test dispositions and legacy removal;
final product/release/performance/host validation. Internal commits do not count
as completed milestones or imply that the full migration is nearly finished.

Latest PDB increment: identity/checksum matching validates raw Portable CodeView
GUID+timestamp and checks SHA256/384/512 over the zeroed-ID PDB. Real Roslyn game
and reference PDBs validate; identity/payload mutations diagnose. Multiple-checksum
ordering and all-mismatch cases are exercised through mutated real PE fixtures.
Final selection154/154 passes11.857s, plus workspace check, rustdoc and rustfmt.
Read-only review found no implementation defect; the requested coverage is added.
RustCrypto supplies hashing and all60 locked external packages now have license
notice rows, including previously missing PNG test dependencies. Symbol discovery,
ELF/KDBG source ranges and actual source debugging still remain required.

Previous Portable PDB reader increment: the [source-debugging plan](source-debugging.md)
now has a native bounded standalone reader, correct signed deltas, document
changes and explicit hidden ranges. Real Roslyn Debug/Release DLL/PDB fixtures
and hand-encoded malformed inputs are covered. Review-found decode-work
amplification and format edges have reproduced/fixed regressions. The final
selection passes145/145 in11.896s, exit0, including30 PDB unit cases and two
Roslyn cases; workspace check, separate rustdoc and rustfmt pass. No new
dependency or unsafe code was added at that checkpoint. PE/PDB identity/checksum
matching was still absent then; symbol discovery, emitted ELF/KDBG source ranges
and real debugger acceptance remain unimplemented. This does not close the debug gate or another legacy method;
the ledger remains68/565 complete,497 outstanding.

Latest visual/font increment: [JRPG acceptance](jrpg-acceptance.md) now includes
independent PNG/RGB555/bitplane/pixel checks, terrain, four Hero OAM quadrants,
HUD and rendered no-residue at the original movement budget. Four actual PNG
captures are isolated under TestResults; curated screenshots remain unchanged.
The300-frame power-on title deadline exposed slow1bpp expansion; hoisted masks
and pointer walks fix it while reusing guarded VRAM stores. Nonempty1bpp/font
loads explicitly leave interrupts disabled. The separate Mono fixture checks
all16 color pairs, both LCD states, selected banks, wrapping and empty input.
Final focused42/42 passes478.485s, final edited JRPG/HUD9/9 passes56.031s, exit0
for both. Four more legacy methods close:68/565 complete,497 outstanding,
zero physically retired. This is selected verification, not a new full suite.

Hosted native validation for a19d67a completed in workflow43860434611:
Linux1205/1205 in1133.700s (job133275524527), macOS1205/1205 in1280.221s
(job133275524423), Windows1203/1203 in1966.459s (job133275524540), zero skipped
in each full run. Separate rustdoc, executable artifact smoke and native-backed
language-server publishing succeed on all three hosts. Managed CI43860434567
still fails:110 legacy compiler cases reject new intrinsic kinds on both hosts;
Windows additionally has an LSP timeout (6.1650563s against5s). Native replacement
coverage and legacy removal remain required; the unrelated LSP timeout needs
separate investigation. These results do not validate subsequent uncommitted work.

Latest graphics increment: [guarded VRAM stores](vram-write.md) fixes the old
library's live-attribute retry race without changing sample sources or relaxing
zero mode-3-write assertions. [JRPG native acceptance](jrpg-acceptance.md) now
checks real SDK Debug/Release builds, boot, movement, NPC collision, three
exact dialogue pages, retained callback return and one seeded Slime victory.
Successful nonempty CGB attribute writes explicitly leave interrupts disabled;
IME preservation/reentrancy is not claimed. Phase checks sample two dot positions
per scanline, not all PPU phases. Independent review found no blocking defect.

Actual-worktree checks pass12/12 in275.699s and final battle cases2/2 in56.038s;
isolated broader regression passes396/396 in472.676s, including existing bounded
copies,2048 SDK gameplay and all IR/SM83 cases. All-target/all-feature workspace
check passes10.66s with the existing cube-helper warning; separate rustdoc passes
four crates/zero cases. The actual unchanged JRPG project builds through normal
native discovery in2.92s, zero warnings/errors, producing131072-byte CGB-only ROM
and ELF32/SM83. These are functional/build observations, not host-performance
comparisons. Two more source methods close:64/565 complete,501 outstanding,
zero physically retired. New-head full-workspace/hosted validation, remaining
JRPG art/OAM/capture assertions and whole-migration gates remain required.

Previous committed increment: [native delegates and callbacks](delegates.md). Focused
Debug/Release and DMG/CGB tests execute stored static, captured, instance and
virtual callbacks, returned closures, reference arrays and wide/byref results.
Malformed metadata, nominal signature mismatches and null binding/invocation
have explicit checks. The unchanged JRPG Release SDK build now succeeds,
producing a131072-byte ROM. Existing emulator visual smoke checks reach the title
at600frames and the overworld after Start at600/release602 and900total frames;
both processes exit0. These captures do not prove movement, collision, dialogue
or game-state semantics; Rust-owned gameplay acceptance is outstanding. The
callback increment7b8a456 passes the final workspace/all-features regression:
1,193/1,193 in2,764.108s, zero skipped, exit0. All44 frozen source hashes match.
Separate rustdoc passes for four crates/zero doctests. The interrupted first
attempt143 after414passes remains recorded separately; it is not a green run.
The callback audit closes11 more methods:62/565 complete,503 partial/outstanding,
zero physically retired. This checkpoint does not cover the separate isolated
VRAM-write fix or expanded JRPG acceptance, which still need integration.


Hosted native validation for pushed deab1b2 is now verified from completed job
logs in workflow43833072527: Linux1120/1120 in1075.379s (job133182850276),
macOS1120/1120 in1597.915s (job133182850362), Windows1118/1118 in2065.902s
(job133182850038), zero skipped in each full run. Separate rustdoc passes on
all three hosts. The artifact smoke selection also passes1test per host, with
14 intentionally unselected tests; it is not the full-suite count. These results
include rectangular arrays but predate the callback unit. Managed
solution CI still fails at the JRPG callback on that pushed commit; the local
native callback build/smoke evidence above does not establish hosted acceptance.

Previous increment: [rank2 rectangular arrays](rectangular-arrays.md), including
mutable initializers, checked row-major addressing, queries, borrowed references
and aggregate/reference elements. Final selected30 cases pass183.953s; the
earlier shared allocator/IR regression passes349 cases, with four obsolete
rank2 rejection assertions replaced and rerun successfully. The source
rectangular method now has native behavioral replacement:51/565 complete,
514 outstanding. JRPG progresses to a delegate/callback dependency and still
does not build; full acceptance remains incomplete.

Previous increment: [bounded VBlank copy](vblank-copy.md) and actual2048v2 SDK
gameplay. Four Debug/Release × DMG/CGB cases build the unmodified game sources,
round-trip canonical ELF, reproduce the SDK ROM with the Rust linker, boot the
existing emulator and play eight moves. Rust independently checks merge/score/
single-spawn rules, title/HUD glyphs, uploaded art and every board pixel. The
original capture budgets were kept. All four final cases pass; the bounded-copy
sweep also passes6160 phase/count/frame probes, including actual stack offsets
above127 and interrupt-state checks. Review's late-store LY152 defect was
reproduced and fixed by reserving instruction latency before the two-line tail.

The full workspace/all-features run completed1086 tests in1729.809s:1084 passed,
two new LCD-off tests failed, zero skipped, exit100. The emulator starts with
LCDC=0x91; those tests had incorrectly assumed LCD-off state. They now establish
that state explicitly, retaining the fault9 assertions, and pass2/2 in10.475s,
exit0. Production and all other source hashes stayed unchanged. Do not describe
the original full command as green. Current-increment hosted CI remains pending.
The two original2048v2 methods/three scenarios now have stronger native
replacements at that checkpoint:50/565 complete,515 outstanding, zero physically retired.

Previous increment: sequential reference-bearing value structs and typed reference
cells. Inline copies retain independent scalar storage and shared class/string/
array referents; nested, static, class-owned and array-element values execute.
`ldind.ref`/`stind.ref` retain cell types. Explicit managed layouts and scoped
byref fields diagnose. Cache-order depth and reference-signature work limits
have negative regressions. Final selected regression:347/347 passed in229.390s,
including16 ManagedValues ROM probes across Debug/Release and DMG/CGB. Workspace
all-feature/all-target check succeeds in8.45s with the existing cube-helper warning.
IR field projections remain trusted metadata declarations, not a sandbox for
arbitrary hand-authored IR; see [IR contract](ir.md).

Hosted verification for pushed6eda360 is complete: native workflow43799726969
passes1063 Linux cases in1054.414s,1063 macOS cases in1216.587s and1061 Windows
cases in1385.779s, zero skipped; separate rustdoc passes on all three hosts.
These results predate the pending bounded-VBlank-copy/gameplay increment. Managed
workflow43799726853 still fails the actual JRPG SDK build at rank2 byte[,] in
World::IsWalkable IL0013. Do not describe the complete solution CI as green.

At the managed-value checkpoint, the ordinary2048v2 Release SDK build succeeded,
producing131072 bytes in31.59s.
Manual emulator runs display its title and scripted gameplay. New native-owned
SDK acceptance is still uncommitted: both CGB configurations pass exact board
state, single-merge/spawn rules, tile bytes and board pixels. Both DMG cases fail
exact pixels: after300 settling frames the clear's dirty range still begins at
140 of1024, leaving title text and partial tiles. Do not claim sample acceptance
or increase settling budgets to conceal the flush throughput defect. The test
reference's four independent merge cases passed. That historical checkpoint's
ledger was48/565; the bounded-copy/gameplay increment above resolves these DMG
failures and adds complete native sample acceptance.

Previous increment: [native class virtual dispatch](virtual-dispatch.md). Typed
synthetic dispatch functions preserve newslot/override/base-call semantics,
constructor-time dispatch, null checks and transitive class reachability.
Four D/R × DMG/CGB configurations execute36 ROM probes with separate banks
including257+, exact per-type packets and wide/byref signatures. Review's
duplicate-override defect was reproduced and fixed. Final focused283/283 checks
pass43.495s, exit0. The full workspace run completed1047 tests in1487.093s:
1045 passed, two stale forged-framework diagnostic assertions failed, zero
skipped; both strengthened replacements pass in the focused run. Do not describe
that original full command as green. New-head hosted validation remains required.
One more legacy method is replaced:48/565 complete,517 outstanding.
Ordinary2048v2 SDK build advances to Assets::Load IL_0005 and rejects managed
references inside value structs, exit1 in29.78s. Interfaces, scene-game behavior,
managed structs and the other whole-migration gates remain required.

Previous unit: [reference-class layout and executable object core](class-layout.md).
Object-core work now passes60 ROM probes (15 entries across
Debug/Release and DMG/CGB) in four rstest cases. Typed references,
fresh allocation, constructors/direct calls, inherited fields, aliases,
reference arrays, null faults and exhaustion are exercised. Broader regression
passes376/376 in338.797s; final focused checks pass277/277 in40.210s, exit0.
Review-discovered fabrication/alias gaps have negative regressions and fixes.
At that checkpoint virtual/interface dispatch and real scene games remained required. Two legacy
class methods now have complete replacements:47/565 complete,518 outstanding.
Workspace all-feature/all-target check succeeds7.14s (existing cube support
unused-helper warning); separate rustdoc succeeds for four crates/zero cases.
An ordinary2048v2 Release SDK build now reaches `Game::Run IL_000c` and rejects
virtual dispatch explicitly, exit1,29.55s (`/tmp/koh-object-2048-sdk.log`).
This advances past class construction but is still a failing required sample;
it is not game acceptance. No fallback compiler is invoked.

Metadata handling now distinguishes packed reference-class layouts from unmanaged
values, preserves inherited/hidden field identity across assemblies and bounds
signature parsing/cached inheritance work. Review-discovered depth/cache-order
and large-signature amplification defects have explicit red/green regressions;
the corrected selection passes5/5. Final layout/reference regression passes58/58
in241.459s, exit0, zero skips (`/tmp/koh-class-metadata-regression.log`), including
all48 layout and10 reference cases. Cross-assembly base/enum fields, missing
references, numeric/array fields, corrupted metadata and bounded amplification
are covered; read-only re-review found no further blocker in the fixes.
Workspace/all-features/all-targets check succeeds46.58s with the existing cube
support dead-code warning; separate rustdoc succeeds with no current doctests.
Those metadata results alone do not establish execution or dispatch. The new
object-core probes above extend them;2048v2/JRPG acceptance remains unproven
and remaining class dispositions stay open.

Hosted phase-sweep commitd3f2d2a has completed successfully:
[Rust run43777137980](https://github.com/retro-dev-tools/koh/actions/runs/43777137980)
passes Linux987/987 in776.956s, macOS987/987 in916.300s and Windows985/985
in1276.868s, zero workspace skips. Windows excludes the same two Unix-only
assembler tests; compiler targets are included. Separate rustdoc, artifact smoke
and independent RGBDS jobs succeed. These logs predate the class-layout changes.

The pushed metadata commitb4caf55 also now has completed hosted Rust evidence:
[run43781354384](https://github.com/retro-dev-tools/koh/actions/runs/43781354384).
Actual nextest logs show Linux1012/1012 in774.586s, macOS1012/1012 in1178.527s
and Windows1010/1010 in1792.635s, zero workspace skips. The same two Unix-only
assembler cases explain the Windows difference. Separate rustdoc, artifact
smoke and RGBDS jobs succeed. These results predate the object-core increment;
they do not establish its cross-platform acceptance or the real class games.

Previous unit: [Rust-owned cube phase sweep](cube-phase-sweep.md). Two Roslyn
configurations pass all768 shared-renderer phase/geometry scenarios each;24
adversarial validator cases also pass. The redundant C# verifier and its solution
entries are removed after caller audit; screenshot capture uses the existing
emulator CLI. Final76/76 cube tests pass in123.815s, exit0, zero skips
(`/tmp/koh-cube-phases-final.log`). The documented CLI exits0 after500 frames
with zero mode3 writes and a160x144 four-color shaded-cube PNG,1269 lit pixels
inside the racing viewport (`/tmp/koh-phase-screenshot.log`). Coverage review
found no blocking issue. No compiler-owned assertions remain in the deleted
standalone verifier; this does not close additional565-inventory methods.
This is CLR geometry evidence alongside native ROM cases, not all-phase native
execution; reference classes and the remaining whole-migration gates stay open.

Previous unit: [bounded HBlank streams](raster-stream.md). All four racing-beam
Debug/Release × DMG/CGB cases now pass unchanged capture budgets and actual
per-row SCX timing checks. Focused boundary/IR tests pass10/10; backend/validator
checks pass35/35. Final formatted IR/backend/cube/boundary regression passes
380/380 in415.625s, exit0 (`/tmp/koh-raster-regression.log`),126 unrelated
driver tests filtered. Review found no blocking defect. Phase-sweep migration
and current raster hosted validation remain pending. The924-test workspace
result below predates this unit.
Ordinary Release SDK racing-beam build succeeds with0 warnings/errors in38.41s,
producing a131072-byte ROM with valid header/global checksums and CGB flag
(`/tmp/koh-raster-sdk.log`). Portable PDB/source mapping remains unimplemented.

Hosted copy-commit3ce00aa results were read directly from completed job logs:
[Rust run43771777807](https://github.com/retro-dev-tools/koh/actions/runs/43771777807)
passes Linux924/924 in713.150s, macOS924/924 in965.246s and Windows922/922
in1396.444s, with zero skips in the workspace runs. Windows omits two Unix-only
assembler path/symlink tests, not compiler targets. Separate rustdoc and native
artifact smoke steps succeed on every host; the independent RGBDS job succeeds.
[Managed run43771777666](https://github.com/retro-dev-tools/koh/actions/runs/43771777666)
still fails Linux/Windows solution builds for 2048v2/JRPG reference classes
(`Aggregate must be a sealed framework System.ValueType`). Its assembly2048,
extension and SDL jobs pass; managed tests and AOT publishing are not reached.
These results prove the previous commit's stated scope, not current raster or
full migration acceptance.

Previous unit: [native ordered byte copy](memory-copy.md). Rust now validates
the metadata contract, lowers an effectful three-operand IR operation and emits
an ascending SM83 byte loop. The3840-byte execution probe falls from6235252T
Debug/5735156T Release to217748T; this is target execution, not host compiler
performance. All8 double-buffered/full-frame Debug/Release × DMG/CGB cube cases
now pass unchanged frame budgets and stronger geometry/DMA checks.

Full Linux workspace/all-features nextest completed924/924 in1638.039s, exit0,
zero skipped/retried (`/tmp/koh-copy-workspace-nextest.log`), including747 compiler
cases and all3 native SDK build/run cases. The separate rustdoc command succeeds
with no current doctests. Final selected-bank strengthening passes4/4 in21.380s
(`/tmp/koh-copy-final-banks.log`),56 ROM probes, with measured56T/byte marginal
copy cost. Tested production code is unchanged across those runs. Read-only
review found no blocking defect. Source mappings, IRQs, banked data, reference
classes, racing-beam/phase-sweep acceptance, legacy deletion and final hosted/
release/performance validation remain open. At that checkpoint,45/565 dispositions were complete,
520 partial/outstanding, zero physically retired:8 more runtime-memory methods
and3 cube methods have independently justified replacements.

Previous unit: [pin-scope bank validation](pinned-pointers.md). The whole-method
bank-write ban incorrectly rejected bank selection before a fixed block.
CFG possible-live-pin sets now guard writes and transitive call sites, preserving
rejection while a pin may be active and allowing selection before acquisition
or after the final release. The regression passes241/241 in82.006s, exit0
(`/tmp/koh-pin-scopes-regression.log`,98 unrelated cases filtered), including
all four original OAM-DMA cases without changing their fixtures/assertions.
Final229/229 IR checks pass in1.610s, exit0, including two overlapping-pin cases
added after review. These local results fix the reproduced hosted diagnostic;
they do not establish a green new-head hosted run. Dispositions stay34/565.

Previous unit: [complete GDMA windows](gdma-windows.md). Native FF55 tracing
revealed late full-frame transfers despite passing images and CPU-write guards.
LCD-on copies now use synchronized1024-byte bursts; LCD-off hardware-size
copies remain. The direct2048-byte red fails Debug and Release, while the
corrected focused selection passes15/15. Extra exact-memory/no-op/boundary
cases and all8 real double-buffered/full-frame cases are now required native
acceptance. The broader selection completes44passed/1failed in239.891s,
exit100 (`/tmp/koh-gdma-regression.log`,98 unrelated cases filtered).
Final distinct-source-half transfer recheck passes4/4 in29.211s, exit0
(`/tmp/koh-gdma-patterns-final.log`):56 exact-memory ROM probes.
Full-frame DMGDebug still fails animation at450/750: only frame
changes83 and437 occur, and frame budgets remain unchanged. No additional
legacy method is retired. See the timing document for the deliberate API and
four-VBlank presentation change.

Hosted head e9a6be0 is verified red, not pending. Native run
[43762030785](https://github.com/retro-dev-tools/koh/actions/runs/43762030785)
fails the same OAM pinned-bank diagnostic on Linux/macOS/Windows. Linux/macOS
reach659 passes then1 failure; Windows657 then1. Another194 cases were not run
after fail-fast, and downstream doctest/artifact checks were skipped. The
fixture selects bank2 before pinning, exposing an overly broad method-level
bank-write restriction. This must be corrected without weakening the fixture.
Managed run [43762031131](https://github.com/retro-dev-tools/koh/actions/runs/43762031131)
fails Linux/Windows solution builds at the known 2048v2/JRPG class limitations.
Extension and SDL checks passed; these do not establish compiler acceptance.

Previous unit: [VBlank window and bounded map writes](vblank-window.md).
Final graphics selection passes28/28 in161.668s, exit0
(`/tmp/koh-vblank-final-green.log`;90 unrelated tests filtered). It includes
all four double-buffered Debug/Release × DMG/CGB cube cases, four actual
showcases, tile/map transfers, lazy storage and four VBlank cases exercising
32 entry phases and four double waits. Frame budgets, structural capture,
exact memory and actual mode3 assertions are unchanged. Read-only review
found no blocking issue. Full-frame/racing-beam structural acceptance remains
open, so legacy dispositions stay34/565 complete and531 partial/outstanding.

Previous unit: [native byte fill](runtime-memory.md). The real cube profile
identified the old C# loop as its largest early-capture cost. Exact guarded
fill ROMs improve from1434496T Debug/1276400T Release to91492T in either mode;
this is target execution, not host compiler-performance evidence. The validated
metadata contract lowers to effectful IR and complete Rust code generation.
Final post-hook fill selection passes24/24 in125.887s,exit0, including exact
bus wrap, local aliases and signed/narrow constant classification. The actual
SDK graphics dotnet build/run also passes87.842s with zero mode3 writes.
Two more legacy methods have native replacements:34/565 complete,
531partial/outstanding, none physically retired. CGB Debug/Release cube
captures now pass, as does DMGRelease; DMGDebug still fails its early capture.
The stronger ten-case cube run is9passed/1failed,59.485s, exit100.
The next root cause to test is Ppu.WaitVBlank's one-scanline polling window.
No frame limit, geometry assertion or failing case is waived.
The broader regression completed308passed/1failed in466.164s,exit100
(`/tmp/koh-fill-graphics-regression.log`,82 unrelated cases filtered). The
only failure is that DMGDebug early capture; IR, backend, fill, tile, lazy-map
and showcase checks pass. Read-only review found no new Rust correctness defect;
the retiring C# compiler's inability to consume memfill is intentional and
documented, not serviced by a fallback implementation.

Previous units: [small-magnitude multiplication](magnitude-multiply.md) and
[shared canvas transfers](canvas-transfers.md). Backend66/66 plus real tile
transfer4/4 passed221.574s, exit0 (`/tmp/koh-multiply-transfer-regression.log`).
Multiplication's I32 probe fell47080T→3828T; I64 fell185896T→6260T. One private
helper per function/width prevents repeated arithmetic from overflowing a bank.
Canvas's unsafe late-copy setup is replaced by the tile loader's bounded path.
These changes do not complete game acceptance: stronger two-capture geometry
finds blank early images on CGB Debug/Release and DMG Debug. DMG Release passes;
the full real-game probe is1/4, with zero mode3 writes in all4. Its Rust target
is committed as required acceptance, with unchanged frame budgets, and must be
fixed in this PR. Legacy retirement stays32/565; no methods are newly complete.
Exact post-hook selection:18passed/3failed in78.830s, exit100
(`/tmp/koh-cube-multiply-postcommit.log`): all11 selected math/stack cases pass,
all6 validator cases pass, DMGRelease cube passes; the other3 cubes fail their
early capture.55 unrelated backend cases were intentionally filtered.

Previous unit: [word division](word-division.md). Runtime magnitude dispatch
preserves full-width CIL faults/sign semantics and uses register division for
small operands. The independent I32 ROM test fell from51268T to4532T; I64 takes
6212T. Backend50/50 and expanded division9/9 passed. These CPU execution costs
are not compiler-performance measurements or whole-game acceptance.
The actual cube probe now passes Debug/Release CGB at the unchanged500-frame
budget. DMG still fails with806/826 mode3 writes (Release/Debug); full probe
result2passed/2failed,40.931s (`/tmp/koh-cube-word-all.log`, exit100).
Its structural geometry assertions still need strengthening before retirement.

Previous unit: [lazy map storage](lazy-maps.md). Native game tracing confirmed
that eager map shadows exhausted the shared arena before canvas allocation.
The writer now records uniform clears and creates each aligned shadow only when
needed, preserving same-value no-ops and flattened bounds failures. The final
serial graphics/map/sprite/text regression passed22/22 in155.775s, exit0
(`/tmp/koh-lazy-map-verified.log`). Read-only review found no concrete defects.
The cube proceeds past allocation, but CGB arithmetic cost and DMG presentation
timing still fail the new diagnostic acceptance probes; these remain open work.
No additional legacy method is fully retired by this memory correction.

Previous unit: [checked raw arena](raw-arena.md). Allocation/reset lower through
typed IR into the linker-owned4KiB bank1 region. Independent managed/raw cursors
check their combined capacity; reset preserves managed arrays. Native D/R ×
DMG/CGB, desktop-body, IR and existing array regressions passed26/26 in136.631s
(`/tmp/koh-raw-arena-array-regression.log`, exit0;265 unrelated cases filtered).
The allocation red and separate desktop wraparound red are retained. Four more
legacy methods have complete native replacements:32of565 complete,533 partial/
outstanding, none physically retired. All three actual3D Release SDK builds now
produce128KiB ROMs with zero warnings/errors. Real-boot headless captures show
cubes for full-frame/racing-beam, but double-buffered remains on the boot logo.
That sample is failing; zero mode3 writes alone is not acceptance. Exact
game-state assertions and double-buffered rendering remain open. Pinned
head4e7212d passed native CI43742721789 on Linux794/794613.381s,
macOS794/794939.618s and Windows792/7921273.505s, zero skipped; two Unix assembler
cases explain the count difference. All hosts also passed separate doctests and
release-artifact graphics smoke1/1 (two SDK cases intentionally filtered).

Previous unit: [CPU control and startup speed switching](cpu-control.md). Native
DI/EI/HALT/NOP/STOP lowering and six IR cases pass alongside four Debug/Release ×
DMG/CGB execution cases:10/10 focused,25.164s, exit0
(`/tmp/koh-cpu-control-final.log`;271 unrelated cases filtered). The IME probe
exposed the old speed helper's unconditional interrupt enable. Its explicit
startup contract now preserves IE/pending requests and leaves IME disabled.
Two more legacy speed-switch methods have complete native replacements:
28of565 complete,537 partial/outstanding at that checkpoint, none physically
retired. Its local workspace completed784/784, zero skipped,1210.838s, exit0
(`/tmp/koh-cpu-workspace.log`); separate doctests passed (no cases). This used
CPU-control production code plus the corrected unknown-kind rejection fixture;
the new raw-arena cases were not in that binary. Hosted6898940 passed Linux and
macOS784/784 and Windows782/782, zero skipped; two Unix assembler cases explain
the count difference. All three passed separate release-artifact graphics smoke
1/1 (two SDK cases intentionally filtered), and separate doctests. Handler
ABI/reentrancy and physical speed-switch timing
are not proved by these tests.

Previous unit: [native SDK boundary](sdk.md). Ordinary Debug/Release builds,
implementation references, canonical ELF, real CPU/MMIO and asset consumption,
incremental inputs, flags/failures/aliases and real dotnet-run routing pass the
three-case SDK target. The actual graphics sample builds a65536-byte ROM and
runs1200frames with zero mode3 writes. Source debugging and release packaging
remain open; a KDBG file is not proof of Portable PDB mappings. The two tile-sheet
tests now have native ownership:26of565 complete,539 partial/outstanding, none
physically retired. The local all-features workspace run completed774/774,
zero skipped, in1230.794s (`/tmp/koh-sdk-workspace.log`); separate doctests passed
(no cases). Hosted validation and final migration acceptance remain separate.

Native CI/release packaging includes `koh-compile` at961e7c3. The task payload
copies the existing complete Rust dependency notices and license bundle alongside
both native tools. A published Linux task payload compiled the real graphics
sample and the managed emulator ran1200frames:65536-byte ROM, PNG160x144,
zero mode3 violations. Logs: `/tmp/koh-compiler-task-package.log`,
`/tmp/koh-compiler-task-package-smoke.log`, `/tmp/koh-compiler-task-package-run.log`.
This is not a tagged archive, AOT emulator or cross-platform release proof.

[Banked calls](rom-banking.md) passed 4/4 expanded ABI cases in 37.635s and a
755/755 workspace regression in 2581.751s, zero skipped
(`/tmp/koh-rombank-workspace.log`). Calls cross banks including 257/258, covering
recursion, long/aggregate returns, byrefs, void/narrow arguments and strings.
Linker-owned boot-logo finalization passed 23/23 native linker cases; corrected
raw golden fixtures passed 52/52 managed linker tests. These runs precede the
current presentation work and do not establish final migration acceptance.

Presentation predecessor3a9f3a1 completed native CI: Linux771/771679.078s,
macOS771/771614.780s, Windows769/769946.295s, zero skipped (two Unix assembler
cases differ). Actual job logs from43728226099 were inspected. Managed CI
43728226133 failed six legacy compiler tests on both hosts: DMG drip budget,
two private loop-IR assertions, 2048v2 gameplay and two JRPG acceptance cases.
These remain open regressions, documented in [SDK evidence](sdk.md), not a green
baseline or justification to weaken assertions. SDK-head validation is separate.

Previous unit: [presentation timing](presentation.md). The stronger showcase
checks exposed a HUD that never progressed. Generated-code optimizations alone
did not fix it; private copy/commit paths now reacquire vblank when late. Four
Debug/Release × DMG/CGB cases passed, including real boot, visible score changes,
sprite geometry, independent checker pixels and VRAM safety. The full 769-case
workspace run passed, zero skipped, exit0, in1997.889s
(`/tmp/koh-showcase-workspace.log`). After strengthening chronological score
assertions and correcting LCD-on clear, focused showcase/sprite8/8 and full-map/
text6/6 passed (`/tmp/koh-presentation-final-focused.log`,
`/tmp/koh-map-text-final.log`, both exit0). Separate workspace doctests passed
(currently no cases). The full769 run predates that clear correction and the
two new full-map cases; it is not a full771-case result. Four graphics contracts
now have complete replacements:24of565 complete,541 partial/outstanding, none
physically retired. All-start-phase GDMA proof remains open.
Neither this fixture nor its
passing results prove native SDK integration or source debugging.

## Earlier increment evidence

[HRAM-safe OAM DMA](oam-dma.md). Reachable metadata intrinsics
emit one linker-placed eight-byte HRAM routine, installed at startup. No fixed
HRAM address or shared WRAM page scratch is required. Blocking execution and raw
FF46 trigger probes cover Debug/Release on DMG/CGB, relocated HRAM including its
last usable range, bank restoration, full OAM/source guards and invalid metadata.
The full workspace passed **751/751**, zero skipped, in 878.612s
(`/tmp/koh-oamdma-workspace.log`). Subsequent test-only boundary extensions passed
**6/6** focused cases in 59.821s (`/tmp/koh-oamdma-final-focused.log`); 65 unrelated
driver cases were filtered. Separate workspace doctests passed (currently no
cases). Twenty of 565 legacy methods now have complete replacements; 545 remain
partial/outstanding, and none have been physically retired.

OAM head 5d60bb6 completed both hosted Rust and managed CI successfully.
Actual native logs: Linux 751/751 in 391.508s; macOS 751/751 in 578.793s;
Windows 749/749 in 767.198s, all zero skipped. Complete name sets differ only
by two Unix assembler path cases. Dependent AOT/publish jobs also succeeded.

The unchanged full Release graphics showcase now compiles to canonical ELF.
Its 80 allocated ROM sections total 48073 bytes, all currently ROM0. The Rust
linker correctly rejects placement (`/tmp/koh-full-gfx-link-after-dma.log`). This
is a known sample failure, making banked code/data the next product requirement.
The probe uses an isolated ordinary SDK project, not native Koh SDK integration.

String head 358beaf passed hosted Rust CI on all three platforms. Its managed
Windows CI failed `UnreadLargeRequestTimesOutAndClientCanRestart`: 5.1140190s
exceeded the five-second assertion; dependent jobs were skipped. Root cause is
unproven. LSP changes are outside this migration; no assertion was weakened.

Previous increment: [UTF-16 strings](strings.md), exact framework accessor binding,
typed String IR and canonical interned ROM0 literals. Value calls/statics/nulls,
checked indexing, actual Video.Init/Text.Draw/DrawToWindow and ordered helper/
foreach output execute in D/R×DMG/CGB. Conservative local/constant forwarding,
dead pure values/unread stores and compact byte/word copies resolve the retained
Debug text fixture's ROM0 overflow. Read-only review found no concrete defects.
Full workspace737/737 passed,0skips,exit0,1061.785s
(`/tmp/koh-strings-workspace.log`). Final297/297 focused cases passed,exit0,
117.812s (`/tmp/koh-strings-final-focused.log`), covering subsequent interning/
capacity tests, explicit unsupported-member diagnostics, recursive returns and
unused-result null traps. Stronger direct VRAM snapshot assertions subsequently
passed4/4 D/R×DMG/CGB cases,exit0,61.310s
(`/tmp/koh-strings-video-snapshots.log`). Separate doctests (no cases), CSharpier,
Rust formatting and diff checks pass. The full run preceded the final3 unit
cases/diagnostic refinements; it is not a claim of a full740-case rerun.
Seven additional legacy methods now have native behavioral replacements
(4PORT,3REPLACE);18of565 complete,547 still partial/outstanding, none physically
retired. Current nextest JSON discovery is740 cases including564 compiler cases,
zero ignored (`/tmp/koh-strings-list.json`, list/build exit0). Combined execution569 ROM scenarios (450 returns,119 traps), four full
framebuffer comparisons. Hosted validation of this new unit remains pending.

Predecessor reader head ec50f61 has completed successful
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43704915974) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43704915959) workflows.
Actual native logs: Linux714/714,542.603s; macOS714/714,359.201s;
Windows712/712,745.200s; zero skips. Complete name-set comparison differs only
by two Unix assembler path cases, with identical compiler coverage. This is
reader/array predecessor evidence, not validation of uncommitted String work.

Previous increment: [unmanaged arrays](arrays.md). Storage-based nominal element
types now lower typed array operations, byrefs and pins. Checked length/stride
scaling prevents overflow before allocation; odd3/5-byte elements,64-byte copies,
aligned payloads, empty4096-byte values and exact4KiB capacity execute with full
memory guards. The retained large Debug fixture exposed ROM0 costs; compact word/
bounds/access emission and cold placement of large locals fixed them without
relaxing capacities or reducing assertions. Review found no concrete defects.

Linux workspace691/691 passed,0skips,exit0,870.103s
(`/tmp/koh-aggregate-arrays-workspace.log`). A final nested-value fixture addition
subsequently passed4/4 D/R×DMG/CGB cases,exit0,28.644s
(`/tmp/koh-aggregate-arrays-final-focused.log`), executing48 ROM programs.
Discovery515 compiler cases; combined531 ROM scenarios (436 returns,95 traps),
four full-frame comparisons. Eleven of565 complete legacy replacements;554
still need completion auditing, none physically retired. Final-head hosted proof
remains pending. SDK/source-debug/full-game/legacy-deletion gates remain open.

Previous increment: value constructors in [unmanaged values](aggregates.md).
Nongeneric newobj uses private frame locals, ordinary typed calls and result
snapshots; default versus parameterless construction, chaining, value/ref
arguments, static initialization, recursive returns and same-frame loop reuse
execute on DMG/CGB. Readonly static/nested address reads now work; direct stores
remain restricted to their declaring constructor. Two Rust metadata cases add12
rejection probes and check actual loop newobj plus private-local isolation.

Final focused regression17/17 passed,exit0,60.713s
(`/tmp/koh-value-constructors-regression-final.log`). Prior full local run was
673/677,0skips,exit100,839.665s; four diagnostic failures were then corrected and
rechecked in that focused run. The corrected head34f5b10 subsequently passed hosted Linux677/677 (389.415s),
macOS677/677 (462.890s), Windows675/675 (555.538s), all zero skips. Completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43696752727) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43696752665) workflows
are successful. Windows omits only the two previously documented Unix assembler
path cases; actual test-name sets were compared, with identical compiler coverage. Workspace discovery is677 cases including501 compiler
cases. Combined execution coverage is482 ROM scenarios (412 returns,70 traps),
four full-frame comparisons. Doc tests (no cases)/formatting pass. Ten complete
legacy replacements remain;555 methods still require completion auditing.
Class/generic construction, aggregate arrays and wider runtime remain open.

Previous increment: [unmanaged values](aggregates.md). Bounded canonical layouts,
inline/reference IR types, field access, default initialization, typed copies and
value-type instance signatures now execute actual Sprite library methods and
larger by-value C# fixtures. Compact copies/zeroing above8bytes resolve ROM0
capacity failures without reducing the fixture. Whole-memory/bank guards cover
nested/static/explicit-overlap fields, all integer widths and overlapping copies.
Linux workspace nextest passed675/675,0skips,exit0,832.362s
(`/tmp/koh-aggregate-workspace.log`), including499 compiler cases. The final explicit
cpobj overlap variant passed4/4,exit0,25.109s
(`/tmp/koh-aggregate-cpobj.log`), retaining original Roslyn ldobj/stobj execution.
Post-commit Sprite/aggregate checks passed8/8,exit0,50.459s
(`/tmp/koh-aggregate-postcommit.log`). Combined native coverage is478 ROM
scenarios (408 normal,70 traps), four full frame comparisons. Separate workspace doctests (no cases) and formatting pass.
Ten of565 legacy methods have complete native behavioral replacements;555 still
need completion auditing, none physically retired. Constructors/newobj,
aggregate arrays, reference-bearing/generic values remain outstanding.

Hosted aggregate checkpoint `7863872` now has completed successful
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43693156643) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43693156641) workflows.
Completed native logs report Linux675/675 (417.995s), macOS675/675 (450.576s),
Windows673/673 (400.440s), all zero skips. Windows omits only Unix-specific
assembler `physical_include_keys_preserve_native_path_separators` and
`include_provenance_preserves_the_logical_symlink_path`; no compiler case differs.
Native build/check/doc/SDK-boundary steps also succeeded on all three hosts.
The SDK boundary here still uses the production C# compiler for C# authoring;
these jobs do not prove a native compiler SDK replacement. This is predecessor
proof, not validation of the subsequent uncommitted constructor changes.

Previous increment: [LCD-on transfers](lcd-on-transfers.md). Independent guards
exposed unsafe legacy timing assumptions on the native backend. CPU targets and
four-byte values, or GDMA registers/control, now prepare before waiting. LCD-on
GDMA bursts are limited to1024 bytes; LCD-off limits and public signatures remain
unchanged. Workspace617/617 passed before the final four-byte refinement;
formatted final focused cases passed4/4,exit0,23.357s. Still-live .NET TileSet
integration passed7/7 after a zero-warning build. Doc/fmt checks exited0.
Combined coverage now454 ROM scenarios (385 normal,69 traps), four frame checks,
and eight complete legacy behavioral replacements;557 methods need completion
auditing and none is physically retired. IRQ/double-speed timing and full native
SDK/game/debug acceptance remain open.

Previous increment: [static/array alignment](alignment.md) and actual
`Video.Init` execution. Focused Debug/Release × DMG/CGB emulator tests passed4/4
(`/tmp/koh-video-init-poison-fixed.log`, exit0,19.541s), including tile maps, OAM,
palettes, aligned backing storage and restored banking. Nine metadata boundary
cases passed (`/tmp/koh-alignment-validation.log`, exit0). Workspace nextest
passed617/617,0skips,exit0,451.447s (`/tmp/koh-alignment-workspace.log`). Expanded
alignment assertions subsequently passed4/4,exit0,16.764s
(`/tmp/koh-alignment-edges.log`):20 ROM executions covering empty4096 boundaries,
overflow before heap writes, local/branch aliases, scalar placement and factory
diagnostics. Final suite discovery includes441 compiler cases; combined executed
coverage is444 ROM scenarios (375 normal,69 traps), with four frame comparisons.
Workspace doctests (no cases) and rustfmt checks exited0. This is not a completed
migration or a native SDK integration claim.

## Inspected baseline

- Integrated master: `9cd147265eb0c660d94c7bf4eee6a97d9e1f4e1f`.
- Linker migration [PR #54](https://github.com/retro-dev-tools/koh/pull/54) merged on
  2026-11-14. GitHub state and remote master were checked before branching.
- Cargo workspace: `koh-asm`, `koh-objects`, `koh-link`; no compiler crate.
- Canonical object boundary: `koh_objects::Object`, `validate`, `write_elf`.
  Compiler code must reuse this boundary rather than serialize ELF itself.
- The live compiler remains `src/Compiler/Koh.Compiler`: 68 C# files,
  19,876 source lines at this baseline. These are inventory counts, not complexity
  estimates or a claim that every file has been audited.
- `CompileKohRom` invokes `CompilerRegistry`/`CompilerDriver` in process, then
  `Koh.Native.NativeLinker`. `Koh.Build.Tasks` references the compiler project.
- `CilFrontend` uses Mono.Cecil, resolves directories derived from reference
  paths, and optionally reads matching symbols. This does not prove correct
  assembly identity resolution or establish the native reader contract.
- `CompilerDriver` optimizes unless frontend errors exist, but calls the backend
  even after frontend errors. It does not invoke the IR verifier.
- Compiler tests occupy 72 C# files and 24,834 source lines. Inventory and semantic
  disposition are separate steps; no coverage is migrated yet.
- The baseline included a container-only pokecrystal benchmark harness alongside
  active `tools/benchmarks/Koh.Benchmarks`. Consumer audit found no CI/root target
  using the container scripts; both Koh phases were permanently unsupported by
  empty capability arrays. PR #55 removes that harness and report generator,
  preserving host-native benchmark discovery/execution and RGBDS verification.
  See [bounded cleanup](container-cleanup.md).

## Commit-sized work sequence

Each row is an intended coherent increment in this PR. Split further when review
or correctness needs it; no intermediate increment is a completed replacement.

| Order | Increment | Required evidence |
| --- | --- | --- |
| 1 | Record baseline, scope and progress | Integrated linker migration and preserved unrelated work |
| 2 | Inventory original tests | Exact source identities and explicit unaudited counts |
| 3 | Complete semantic audit and select supported contract | Reviewed test dispositions, independent semantics, library coverage evaluation, sample feature requirements |
| 4 | Native metadata/reference/PDB reader | Real Debug/Release Roslyn fixtures, malformed inputs, identity resolution, no assembly execution |
| 5 | Typed IR, lowering and verification | Stack merges, dominance, types, reachability, diagnostics, source identity |
| 6 | SM83 ABI and executable code generation | Independent CPU/memory expectations, arithmetic edge cases, calls, frames, interrupts, capacity failures |
| 7 | Runtime and game feature completion | Transitive Koh.GameBoy code, sample behavior, supported contract coverage |
| 8 | Native SDK/build/debug integration | Ordinary dotnet build/run, paths with spaces, incremental behavior, PDB to KDBG, debugger execution |
| 9 | Finish Rust test migration | Every compiler-owned scenario disposition resolved, rstest, nextest discovery and CI, separate doctests |
| 10 | Remove C# compiler and obsolete integration | No production fallback, obsolete references/packages/solutions removed, useful build tasks retained |
| 11 | Bounded Docker cleanup and performance evidence | Consumer audit, equivalent workloads, host measurements |
| 12 | Final validation and PR evidence | Samples executed, supported hosted platforms, known limitations, final-tree logs |

Implementation and tests should land together within increments. The test migration
row means completion of coverage, not postponement of all testing to the end.
Delete the old compiler only after replacement behavior is demonstrated.

## Scope and decisions still open

Preserve C# authoring, the existing Koh.GameBoy API, emulator, debugger and SDK
workflow. The request permits API changes in several sections but explicitly
excludes rewriting the game-facing API in section 12. Use the narrower scope:
preserve that API unless a specific required correction is documented. Do not
silently retire features to reduce implementation work.

Choose a direct CIL-to-IR-to-SM83 pipeline. Do not copy the generic frontend/backend
registry or create a second linker. Metadata library choice, IR operations,
supported language contract, calling convention, memory allocation, banking and
interrupt rules are pending audit, not inherited guarantees.

The old subset guide claims capabilities including wide integers, software float,
classes, delegates, generics, iterators and LINQ. Those claims require empirical
checks; neither old documentation nor a passing legacy test is an oracle.

## Acceptance ledger

- [x] Branch from integrated linker migration, PR #54.
- [x] Account for every legacy compiler test declaration in source-level audits.
- [ ] Complete architecture and test audit with independent expectations.
- [ ] Implement native compiler and documented supported contract.
- [ ] Prove game-level ROM execution and debugging.
- [ ] Integrate native compiler into SDK, CI and release packaging.
- [ ] Finish Rust coverage migration and delete legacy compiler.
- [ ] Record platform/performance evidence and bounded container cleanup.

No compiler build, test, sample execution, performance result or cross-platform
validation is claimed by the initial documentation commits.

## Audit results

All 565 test methods are enumerated in the [disposition status](test-disposition.md).
The source audits cover CIL/runtime semantics, backend/ABI and integration consumers.
Their dispositions are proposals, not migrated tests. The existing compiler suite
passed 814/814 executions locally with .NET SDK 10.0.115 (see disposition status).
Final supported contract, native semantic scenario coverage and native-compiled
sample execution remain open.

## Native reader increment

`rust/koh-compile` now reads bounded PE/CLI metadata and raw CIL method bodies.
The [reader decision and limits](metadata-reader.md) explain library selection,
malformed-input checks and pending reference/signature/PDB work. It does not yet
emit ELF or replace the SDK compiler.

Local Linux verification: `cargo nextest run --workspace --all-features --locked`
reported **198 tests run: 198 passed, 0 skipped**, including 22 native reader cases.
Real Roslyn fixtures cover Debug/Release and PE32/PE32+, temporary paths with
spaces, method identities and bodies, invalid local signature references, and
truncation/header mutation robustness. The original read tests failed before the
reader existed; a review-added local-token regression failed before its fix.

`cargo test --doc --workspace --all-features --locked` exited 0 (no doctest cases
currently exist). `cargo fmt --all -- --check` exited 0 with the installed stable
formatter. Completed logs: `/tmp/koh-compile-nextest.log`,
`/tmp/koh-compile-doctests.log`, `/tmp/koh-compile-fmt-check.log`. Host Cargo/rustc:
1.99.0; fixtures use .NET SDK 10.0.115. These local results are not hosted
Windows/macOS or minimum-Rust-version evidence. CI now installs the SDK before
nextest and runs documentation tests separately.

## CIL decoder increment

The [encoding decoder](cil-decoder.md) preserves IL byte offsets and operand
bits, computes branch/switch targets and rejects malformed encodings and targets.
Its .NET-catalog oracle runs only in tests. A decoded opcode is not yet a
supported lowering operation; there is still no native ELF compiler or SDK switch.

Local workspace nextest run: **232 run, 232 passed, 0 skipped**, exit 0, including
56 compiler-reader/decoder cases. Full command was
`CARGO_TARGET_DIR=/tmp/koh-compiler-decoder-target cargo nextest run --workspace --all-features --locked`
with `KOH_DOTNET` selecting SDK 10.0.115. Separate target output kept this build
isolated from the legacy .NET baseline's native build. Completed log:
`/tmp/koh-compiler-decoder-workspace.log`.

Hosted reader head `0f313ff` passed all 14 checks, including native nextest on
Linux, Windows and macOS. [Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43630616545)
and [CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43630616487).
These results precede the decoder commit and must not be described as its hosted
validation.

## Signature increment

The [bounded signature layer](signatures.md) reads methods, fields, locals,
TypeSpecs and MethodSpecs into the metadata library's existing result types.
It corrects signed compressed lower-bound handling, bounds recursion/counts,
preserves modifiers/generic shapes, separates MethodDef from call-site conventions,
and accepts modern .NET ref fields without claiming their execution is implemented.
Real Roslyn fixture assertions cover generic identities, byref/pointer parameters,
rectangular-array fields and function pointers in Debug/Release PE32/PE32+ images.

The initial signature run failed 47 cases before implementation. The current
workspace run reports **295 tests run: 295 passed, 0 skipped**, exit 0, including
63 new signature cases (119 compiler infrastructure cases in total). Command:
`cargo nextest run --workspace --all-features --locked`, with `KOH_DOTNET` selecting
SDK 10.0.115. Completed log: `/tmp/koh-signature-nextest.log`.

All 14 hosted checks passed for preceding decoder head `2340e29`, including the
three native host jobs. [Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43631738227)
and [CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43631738198).
Signature-head hosted validation remains separate. No legacy compiler method is
retired and no native ROM generation is claimed by these parsing tests.

## Reference-binding increment

The [reference binder](reference-binding.md) resolves full assembly identities,
types, overloads, fields, generic instantiations and explicit forwarders from
supplied images. Debug/Release Roslyn projects exercise transitive binding;
SDK reference metadata exercises strong-name identity without assembly execution.
Review found malformed zero-row scopes and unchecked nested generic arities;
both real-DLL mutation regressions failed before their fixes.
Inherited generic substitution, contextual generic constraints, body reachability,
typed lowering and executable code generation remain incomplete.

Local Linux `cargo nextest run --workspace --all-features --locked`, with
`KOH_DOTNET` selecting SDK 10.0.115, completed with **328 run, 328 passed, 0 skipped**
and exit 0. Log: `/tmp/koh-reference-nextest.log`. Separate workspace doctests
exited 0 with no cases (`/tmp/koh-reference-doc.log`); stable rustfmt check exited
0 (`/tmp/koh-reference-fmt.log`). No legacy compiler-owned method is retired.
All 14 hosted checks passed for preceding signature head `81d9836`, including
Linux, Windows and macOS native jobs. Binding-head hosted results are recorded below.

## Typed IR and initial integer lowering

The [IR contract and increment plan](ir.md) use typed SSA values, block parameters,
explicit local storage and method/IL identities. The verifier checks graph shape,
reachability, definition uniqueness, dominance, use order, edge signatures,
operation types, return types and entry initialization for local loads.

The initial native lowerer translates primitive integer CIL arithmetic,
comparisons, conversions, local/argument accesses and branches into verified IR.
Small storage gets explicit truncation/extension. Reachable unsupported execution
shapes are diagnostics, not placeholder code. Calls, objects, generic execution,
exception semantics, optimization and machine emission remain pending.

Tests include independently authored IL joins/loops and malformed cases, plus real
Roslyn Debug/Release fixtures checked against eight literal result scenarios per
configuration in a bounded test-only IR interpreter. Review caught host-width
arithmetic in that interpreter; all four new regressions failed before its fix.
This is IR evidence, not SM83 execution or retirement of old compiler coverage.
All 14 hosted checks passed for preceding binding head `215ce9e`; current-head
hosted evidence must be recorded separately.

Completed Linux verification: `cargo nextest run --workspace --all-features --locked`
with `KOH_DOTNET` selecting SDK 10.0.115 reports **364 run, 364 passed, 0 skipped**,
exit 0 (`/tmp/koh-ir-final-nextest.log`). This includes 188 compiler cases, 36 of
which exercise the new IR/lowering increment. The earlier run of 360 cases
preceded the four width regressions and is not final-tree evidence.

## Initial SM83 integer backend

The [initial SM83 ABI and executed slice](sm83-abi.md) use dynamic hardware-stack
frames, caller-owned return/argument storage, deterministic spilled values and
guarded capacity. Every operation in the current integer IR has machine emission,
including software multiplication/division/remainder and parallel block transfers.
This is not the final language/runtime compiler: managed calls, interrupts, objects,
generic execution, banking, startup/SDK integration and PDB/KDBG remain incomplete.

The focused Rust suite passes ten cases. Seven execute 140 independently checked
ROM scenarios through serialized canonical ELF, the Rust linker and the unchanged
C# emulator, including 18 real Roslyn Debug/Release scenarios. Ten intentionally
trap; 130 must return expected memory results with balanced SP. Guard sentinels
must remain intact. Three other cases cover ELF method/IL markers, maximum IR
offset robustness and oversized machine-code diagnostics. Log:
`/tmp/koh-sm83-reviewed.log` (exit 0, default test threading).

Review found diagnostic-offset overflow and shared test paths/build directories;
both are fixed. The offset test failed before its fix. Streaming emission now
stops at ROM0 capacity. No legacy compiler method is retired by this increment.
All 14 hosted checks passed for preceding IR head `579b910`; backend-head hosted
validation remains separate.

Completed Linux workspace command `cargo nextest run --workspace --all-features --locked`,
with SDK 10.0.115 selected through `KOH_DOTNET`: **374 run, 374 passed, 0 skipped**,
exit 0 (`/tmp/koh-sm83-nextest.log`). The suite includes 198 compiler cases; the
ten new backend cases are all discoverable by nextest. These results do not prove
SDK replacement or complete legacy test migration.

## Managed calls and body reachability

The [call increment](calls.md) adds typed call sites and whole-program signature
verification, iterative transitive body discovery, guarded caller storage, direct
and mutual recursion, and linker-visible stack UNION reservations. Only reached
methods are lowered; missing references and unsupported reachable code are errors.
Type/module initializer omission was reproduced with real C# and corrected to
explicit diagnostics pending static-storage/initialization implementation. Initializer
execution remains required, not retired. The caller-guard mutation caused protected
memory corruption and failed its test; the guard was restored for all final runs.

Completed local command `cargo nextest run --workspace --all-features --locked`
with `KOH_DOTNET` selecting SDK 10.0.115 reports **383 run, 383 passed, 0 skipped**,
exit 0 (`/tmp/koh-calls-nextest.log`). There are 207 compiler cases and 154 ROM
scenarios: 141 expected returns, 13 traps. Focused default-threaded tests also
passed (25 IR, 16 lowering, 14 backend cases; `/tmp/koh-calls-focused.log`).
All 14 hosted checks passed for preceding backend head `6e01f88`; call-head hosted
validation must be checked separately. No native CLI, SDK switch, game graphics,
PDB/KDBG or final legacy-test retirement is claimed.

## Native executable and cartridge startup

The [native driver](driver.md) now provides `koh-compile`, explicit references,
entry selection and canonical ELF output with startup and trap symbols. It uses
the existing object writer and linker. Nine driver cases add 18 emulator ROM
scenarios, including Debug/Release executable/library inputs, nested entry names,
transitive calls and stack exhaustion. Output safety and deterministic reference
ordering have direct assertions. SDK replacement remains pending.

Separate native-process DMG and CGB-only demonstrations both returned
`00 0000002A D000 OK`, exit 0, in the existing C# emulator. See driver.md for
artifact sizes and limits. This is initial CLI acceptance, not game acceptance.

The preceding call head `a1c7685` passed Linux locally but its hosted macOS native
job failed during Roslyn fixture compilation with nine CS0103 errors, before
native compilation. The fixture directly used Runtime while declaring only Bridge.
Locally disabling transitive project references reproduced those errors. Adding
the direct Runtime project reference produced a clean build under that same flag;
call tests now set the flag explicitly. New-head hosted macOS validation remains
required. Failed hosted job:
https://github.com/retro-dev-tools/koh/actions/runs/43644986002/job/132600633936

Hardware attributes currently fail closed rather than compiling desktop intrinsic
bodies or omitting interrupt roots. Actual intrinsic/interrupt lowering remains
required. No legacy compiler method is retired by this increment.

Completed Linux workspace run: **392 tests run, 392 passed, 0 skipped** in
283.774 seconds (`/tmp/koh-driver-nextest.log`). This includes 216 compiler cases
and 172 ROM scenarios (158 normal returns, 14 traps). The original terminal exit
record was lost during context transfer; the completed nextest summary was read
directly after confirming the process had terminated. Separate workspace doctests
exited 0 (no cases). No hosted result is claimed for this unpushed increment.

## Indirect memory and CFG simplification

The [memory increment](memory.md) adds explicit-width indirect integer loads and
stores, native/unmanaged pointer signatures, single-predecessor CFG chain merging
and conservative block-local spill reuse. Contiguous byte emission removes repeated
address setup. All transformations preserve memory-operation order; alias-based
optimization is not assumed. A combined Debug fixture exposed ROM0 overflow and
was retained while the underlying block/copy costs were corrected.

Focused tests passed **77/77**, exit 0, including the unchanged Debug/Release
memory fixture, existing arithmetic/call/recursion/cyclic-transfer cases, width
verification and a stack scratch-boundary check. Two additional block-order loop
cases are included in the subsequent workspace run. Original expected integer
results are also checked after simplification; repeated simplification is stable.
Logs: `/tmp/koh-memory-optimized-focused.log` and `/tmp/koh-memory-nextest.log`.
Whole legacy methods are not yet retired. Managed byrefs, aggregate/object/array
memory, globals/initializers, real hardware intrinsic lowering and IRQ remain open.

Preceding CLI head `b66b36e` completed all 14 hosted jobs successfully, including
native Linux/Windows/macOS. The completed macOS job log reports
`392 tests run: 392 passed, 0 skipped`; this supersedes the preceding call-head
fixture failure. [Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43651123570)
and [CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43651123593).
These hosted results do not cover the newer memory increment.

Completed Linux workspace command `cargo nextest run --workspace --all-features --locked`
with SDK 10.0.115 selected by `KOH_DOTNET`: **407 run, 407 passed, 0 skipped**,
exit 0, 291.998 seconds. There are 231 compiler cases and 176 ROM scenarios
(161 normal returns, 15 traps). Completed log: `/tmp/koh-memory-nextest.log`.
Separate workspace doctests exited 0, no cases (`/tmp/koh-memory-doc.log`).

## Hardware register/region intrinsics

The [hardware contract](hardware.md) now lowers canonical Koh.GameBoy register
and region attributes at call sites, including property accessors and direct
method aliases. Addresses come from validated metadata blobs. Byte MMIO accesses
use existing typed memory IR; desktop bodies and their initializers remain outside
reachability. Unknown kinds, malformed payloads, invalid signatures/addresses and
duplicate declarations diagnose. Control/heap/DMA/interrupt support remains open.

Debug/Release real Hardware/Gb fixtures pass via the native executable, canonical
ELF, Rust linker and existing C# emulator. They check MMIO, LCD-off VRAM access,
pointer offsets, custom aliases and an excluded throwing desktop body. Four scalar
cases cover mixed native/int32 addition/subtraction. The initial focused run
exposed a missing subtraction arm in the test-only reference interpreter; that
arm now uses independent wrapping arithmetic, preserving the expected results.

Preceding memory head `5409d45` passed all 14 hosted jobs. Its completed macOS
log reports `407 tests run: 407 passed, 0 skipped`.
[Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43652752199),
[CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43652752215).
These results do not cover the newer intrinsic increment. Full migration,
sample/debug/SDK acceptance and retirement of legacy compiler tests remain open.

Completed Linux workspace nextest: **420 run, 420 passed, 0 skipped**, exit 0,
321.348 seconds (`/tmp/koh-intrinsic-nextest.log`). There are 244 compiler cases
and 178 ROM scenarios (163 normal returns, 15 traps). Full command:
`cargo nextest run --workspace --all-features --locked`, with `KOH_DOTNET`
selecting SDK 10.0.115. Current-head hosted validation remains separate.

## Zero-initialized static storage and typed primitive references

The [static-storage increment](statics.md) adds symbolic field addresses,
cross-assembly static integer loads/stores, packed zero-fill WRAM0 storage and
startup clearing through a linker relocation. Primitive managed references retain
their scalar target type in IR and across calls/returns; mismatched reference
arguments and dereference widths diagnose. The first code-generation attempt
failed observable execution because HL was clobbered before saving an address's
high byte; preserving BC corrected that failure.

Completed Linux workspace command `cargo nextest run --workspace --all-features --locked`
reports **443 passed, 0 skipped**, exit 0, 224.302 seconds. There are 267 compiler
cases and 180 ROM scenarios (165 normal returns, 15 traps). Log:
`/tmp/koh-statics-nextest.log`. The two static Roslyn cases subsequently passed
again with stronger poison readback and all-field initial-zero assertions, exit 0
(`/tmp/koh-statics-poison-final.log`). This targeted strengthening followed the
workspace run; it did not change production compiler code. Separate workspace
doctests and stable formatting checks exited 0.

Initializers are still required: constructor-dependent fields, readonly writes,
generic storage, RVA/literal/instance shapes and aggregate/reference fields remain
diagnostic. This commit does not silently replace constructors with zeroed storage.
No whole legacy test method is retired. The initial 256-byte static capacity is
documented alongside pending RAM/heap/banking work, not asserted as final product
capacity.

Preceding intrinsic head `0fe6192` passed all 14 hosted jobs; the completed macOS
log reports `420 tests run: 420 passed, 0 skipped`.
[Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43654007601),
[CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43654007574).
New static-storage hosted validation must be checked separately.

## Executed type and module constructors

[Initializer lowering](initializers.md) now includes ordinary supported constructor
bodies through generated, verified once guards. Module and strict-type method checks
preserve order; `beforefieldinit` types defer to field access and leave unused
initializers unreachable. Recursive/cyclic access observes initialized-so-far/default
state. Readonly writes require the declaring constructor. Unsupported exception
bodies still diagnose at their actual member/IL operation, replacing blanket rejection.

Completed local command `cargo nextest run --workspace --all-features --locked`
with SDK 10.0.115 selected through `KOH_DOTNET`: **450 passed, 0 skipped**, exit 0,
232.391 seconds (`/tmp/koh-init-nextest.log`). There are 274 compiler cases and
186 ROM scenarios (171 normal returns, 15 traps). Focused driver/backend tests
also passed 32/32 (`/tmp/koh-init-focused.log`). Real Debug/Release fixtures cover
module dependencies, readonly initialization, once counters, recursion, cycles,
BFI deferral, dead initializer omission and static-store operand ordering.
Removing generated guards in a test-only object mutation must produce failure 1
instead of 42. The prior initializer-dependent field entry now executes and returns 17.
No legacy compiler method is yet retired; broader runtime, IRQ, exception/generic
semantics, PDB/SDK/game acceptance and compiler deletion remain incomplete.

Preceding static-storage head `0e3cd05` passed all 14 hosted jobs; its completed
macOS log reports `443 tests run: 443 passed, 0 skipped`.
[Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43655742204),
[CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43655742198).
New initializer-head hosted validation remains separate.

## Primitive local/argument addresses and reference checks

[Local reference lowering](local-references.md) adds short/wide `ldloca`/`ldarga`,
stable addresses into dynamic invocation storage and normalization of narrow local
loads after aliased writes. A bounded origin graph rejects managed references to
the current frame returned through aliases, block arguments or reference-returning
calls. Its conservative reassignment/scoped-return limitations are documented.
Managed null dereferences abort with fault 4; native bus address zero remains legal.

Local workspace command `cargo nextest run --workspace --all-features --locked`,
SDK 10.0.115 through `KOH_DOTNET`: **472 passed, 0 skipped**, exit 0, 232.609s
(`/tmp/koh-local-ref-nextest.log`). There are 296 compiler cases and 196 ROM
scenarios (179 normal returns, 17 traps). Focused predecessor checks passed 117/117.
Afterward, stronger positive-to-negative byte/short transitions passed the two
Roslyn cases again, exit 0 (`/tmp/koh-local-ref-signed-final.log`); this changed only
fixture assertions/setup, not compiler code. Separate doctests/fmt exited 0.

Eight-level recursion verifies ancestor addresses and independent per-frame guards;
additional cases check addressed by-value arguments, long mutation, borrowed returns,
null read/write behavior, address encodings, typed storage and frame escapes.
This advances primitive ref/ABI requirements, not aggregate/float references or
whole legacy-method retirement. Arrays, objects, broader runtime, IRQ/banking,
PDB/SDK/sample acceptance, test migration/deletion and performance work remain open.

Previous constructor head `d73963a` passed all 14 hosted jobs; its completed macOS
log reports `450 tests run: 450 passed, 0 skipped`.
[Rust workflow](https://github.com/retro-dev-tools/koh/actions/runs/43657584065),
[CI workflow](https://github.com/retro-dev-tools/koh/actions/runs/43657584034).
New local-reference head hosted validation must be checked separately.

## Primitive arrays and checked WRAM heap

[Array lowering and ABI](arrays.md) add mutable primitive SZARRAYs, null joins,
runtime length, checked element references and a linker-reserved bank-1 heap.
Allocation checks full-width inputs and remaining capacity before writes; objects
are initialized explicitly. Managed reference accesses preserve the caller's SVBK.
The allocator's internal CALL is included in frame guards. Object/reference,
aggregate, float and multidimensional arrays and RVA initialization remain pending.

The final focused array/IR/lowering selection passed **29/29**, exit 0
(`/tmp/koh-arrays-focused-final.log`). Four real Debug/Release DMG/CGB driver cases
execute 40 ROM scenarios and 16 rejection probes; IR/lowering cases validate
element identities, null joins and malformed operations. Review caught and fixed
native/int16 opcode confusion. Additional ref-return and loop-join fixtures exposed
ROM0 costs; shared allocation helpers, direct word address arithmetic, contiguous
comparison/extension and proven-boolean branch tests resolve them without reducing
assertions.

The corrected, formatted tree passed the full Linux workspace command
`cargo nextest run --workspace --all-features --locked`: **501 passed, 0 skipped**,
exit 0, 251.829s (`/tmp/koh-arrays-verified.log`). This includes 325 compiler cases
and 236 ROM scenarios overall (187 normal returns, 49 intentional traps). An
earlier complete run passed 500/501: the rejected return-type diagnostic had lost
its operation context. The implementation restores that context; its assertion
was retained. Separate workspace doctests (no cases) and stable fmt checks exited
0. Source review checked allocator SP offsets/capacity and bank preservation,
without claiming runtime evidence beyond the executed cases.

Prior local-reference head `8267af0` now has completed successful hosted
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43660015702) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43660015743) workflows.
That is predecessor evidence; the array head requires separate hosted validation.
No legacy test method is fully retired and the production SDK still uses C#.

## Mutable RVA array literals

[RVA initializers](rva-initializers.md) add bounded field-data reading, symbolic
metadata-only field handles, exact framework-helper identity/signature checks and
runtime payload copying from canonical read-only ELF sections. Arrays remain
distinct mutable allocations, including static readonly references. Partial/empty
copies follow the helper contract; invalid size faults7 before payload writes.
Compile-time handles cannot cross machine calls or acquire fabricated constants.
Opaque RVA storage requires canonical ValueType ancestry and explicit layout.

Focused reader/IR/driver checks passed **34/34**, exit0, 81.735s
(`/tmp/koh-rva-review-fixes.log`). Four D/R x DMG/CGB cases execute24 ROM scenarios
and12 malformed helper probes; reader cases include three PE variants and eleven
malformed metadata mutations. Review found and fixed the standalone IR call-ABI
gap; negative tests preserve its rejection. The earlier526-case workspace run was
intentionally interrupted after498 passes to complete review fixes; it is not a
successful full-suite result. The corrected535-case rerun is recorded separately.

The corrected formatted tree passed Linux
`cargo nextest run --workspace --all-features --locked --no-fail-fast`:
**535 passed, 0 skipped**, exit0, 333.191s (`/tmp/koh-rva-final-nextest.log`). This
includes359 compiler cases and260 ROM scenarios (203 normal returns,57 traps).
Rust formatting and separate workspace doctests (currently no cases) exit0.

Predecessor array head `4a32ba8` has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43664185371) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43664185401) workflows.
Native Linux, Windows and macOS jobs include successful Test steps; completed
macOS log confirms501/501, no skips. RVA-head hosted evidence remains separate.
Real sample graphics, broader runtime/aggregate/enum, IRQ/banking, PDB/debug/SDK,
whole-test migration/deletion and performance/container gates remain open.

## Enum and stack-buffer increment

[Enum storage, fixed stack buffers and physical spill widths](stack-enums.md)
support the actual 2048 Board component without changing its source. Four Rust
driver cases cover Debug/Release and DMG/CGB, with independent board merge and
memory expectations. Complete graphical-game and SDK acceptance remain open.
The ABI retains full evaluation widths across calls; upper-bound analysis reduces
only provably zero spill bytes. Partial-alias regression tests caught an incorrect
max-versus-OR combination before this increment was committed.

Focused arithmetic2/2, partial-alias2/2 and stack-IR9/9 runs passed with exit0.
Logs: `/tmp/koh-width-boundaries.log`, `/tmp/koh-alias-regression.log` and
`/tmp/koh-stack-ir-final.log`. The first stack-IR run passed8/9: its cycle fixture
incorrectly branched to the prohibited entry block. Moving that cycle to a valid
nonentry block tests the intended allocation diagnostic without changing it.
Linux `cargo nextest run --workspace --all-features --locked --no-fail-fast`
completed **552/552 passed, 0 skipped**, exit0,365.322s
(`/tmp/koh-board-workspace-nextest.log`). This includes376 compiler cases and352
ROM scenarios overall (291 normal returns,61 intentional traps). C# formatting
completed successfully; the focused post-format run passed8/8, exit0,30.588s
(`/tmp/koh-board-formatted.log`), including all four real Board configurations.
Read-only backend review found no confirmed defect; a tentative enum-array
identity concern was withdrawn after checking CLI reduced-type compatibility.

Predecessor RVA head `293585d` has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43666575641) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43666575657) workflows,
queried live by commit SHA. These are predecessor results, not current-tree proof.

## CIL table-switch increment

[Typed switches](ir.md#table-switches) preserve the remaining stack on all default
and case edges, verify int32 selectors and emit full-width SM83 comparisons.
Empty/duplicate tables and backward targets execute. Actual Debug/Release Roslyn
fixtures contain `switch` and produce an independently computed wrapping checksum.
This replaces representation assumptions with observable control-flow semantics;
it does not establish broader runtime or SDK/game acceptance.

The raw-CIL regression first failed with missing opcode0x45 lowering
(`/tmp/koh-switch-red.log`). Strengthened focused tests passed18/18, exit0,9.741s
(`/tmp/koh-switch-final-focused.log`), including13 emulator ROM scenarios. An
earlier17-case run passed16/17 because its expected diagnostic used lowercase
`undefined` while the existing message begins `Undefined`; the fixture was fixed.
Read-only review found cumulative stack-edge cloning could exceed the budget
before verification. The preallocation guard and retained-stack regression now
pass; review confirmed the fix. A redundant mutable test binding was then removed.
The formatted tree passed Linux
`cargo nextest run --workspace --all-features --locked --no-fail-fast`:
**570/570 passed,0skips**, exit0,374.746s (`/tmp/koh-switch-workspace.log`). This
includes394 compiler cases and365 ROM scenarios (304 normal returns,61 traps).
The disposition matrix records complete native behavioral replacements for four
legacy switch methods (three PORT,one REPLACE); their definitions remain until
the old compiler test project is removed. The remaining561 methods still require
completion auditing. The SDK remains on C# and the full migration is incomplete.
Separate workspace documentation tests (currently no cases) and stable rustfmt
checks exited0: `/tmp/koh-switch-doc.log`, `/tmp/koh-switch-fmt.log`.

Predecessor Board head `448f35b` now has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43672970728) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43672970693) workflows.
Native Linux/Windows/macOS Test steps succeeded; completed macOS job132686867798
log confirms552/552 passed,0skips,298.190s. These are predecessor results, not
verification of the uncommitted switch unit.

## Pinned pointer increment

[Pinned byref/array locals](pinned-pointers.md) keep heap bank1 mapped for native
copy routines and restore the caller's SVBK when the final pin ends or the method
returns. Per-frame flags preserve nested/recursive invocations and tolerate
cleanup order. Unbanked pins leave SVBK unchanged. Known explicit bank writes
reachable from a pinned method diagnose; raw bus manipulation remains unsafe.
Pin stores participate in verification, optimization and frame-reference escape.
Native zero cleanup is guarded against nonzero reference fabrication (fault8).

The real D/R × DMG/CGB fixture initially failed4/4 with unsupported pinned-local
types (`/tmp/koh-pinned-red.log`). After implementation, strengthened checks passed
14/14, then15/15 with the review-found invalid-initialization-index regression:
`/tmp/koh-pinned-review-fixes.log`, exit0,19.700s. This selector includes one old
reservation test;14 tests are new. Four driver cases execute28 ROM scenarios and
eight compile rejection probes; the raw cleanup test executes five ROM scenarios.
Nine IR cases cover valid and malformed pin lifetimes/storage. Actual referenced
`Koh.GameBoy.Mem.Copy` writes known LCD-off VRAM bytes and mutates array aliases.
These are not full graphics, DMA, TileSet or SDK acceptance.

Read-only review found local state indexing could precede bounds validation on a
malformed pin initialization. The implementation now validates the index first;
the regression and review confirmation preserve the diagnostic boundary. The
formatted pin tree passed Linux
`cargo nextest run --workspace --all-features --locked --no-fail-fast`:
**584/584 passed,0skips**, exit0,394.810s (`/tmp/koh-pinned-workspace.log`). This
includes408 compiler cases and398 ROM scenarios (333 normal returns,65 traps).
Separate workspace doctests (currently no cases) and stable rustfmt checks exited0
(`/tmp/koh-pinned-doc.log`, `/tmp/koh-pinned-fmt.log`).

Predecessor switch head `2c44000` has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43674121192) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43674121261) workflows.
Native Linux/Windows/macOS Test steps succeeded; the completed macOS
job132690390503 log confirms570/570,0skips,190.157s. These do not verify the new
pin implementation.

## Native graphics execution

[Actual tile loading and rendered frames](graphics-execution.md) add four Rust
D/R × DMG/CGB cases,12 ROM executions and four full-frame pixel comparisons.
The real GameBoy library copies tile data through CPU and aligned CGB GDMA paths,
preserves caller banks and renders explicitly authored bit planes/palettes. Full
poisoned bank snapshots and programmed DMA state are checked in Rust. PNG uses
the existing emulator framebuffer writer via a test-friend declaration; emulator
algorithms and game APIs are unchanged. This is library/frame evidence, not full
Video.Init or complete sample gameplay acceptance.

First copies4/4 passed (`/tmp/koh-tiles-first.log`), then rendered4/4 passed
(`/tmp/koh-tiles-render.log`); formatted DMA checks4/4 passed,exit0,17.682s
(`/tmp/koh-tiles-dma.log`). Read-only review found no concrete issues. The
formatted tree passed Linux
`cargo nextest run --workspace --all-features --locked --no-fail-fast`:
**588/588 passed,0skips**, exit0,412.346s (`/tmp/koh-tiles-workspace.log`). This
includes412 compiler cases and410 ROM scenarios (345 normal returns,65 traps),
with four full-frame comparisons. Full SDK/game/debug acceptance remains open.

Standalone Release Roslyn → native CLI ELF → native CLI linker → existing C#
emulator runs also passed for DMG and CGB, including independent complete-pixel
checks and fresh PNG artifacts. The documented direct project builds were run
serially and reproduced the same pixels. Logs `/tmp/koh-tiles-demo.log` and
`/tmp/koh-tiles-root-demo.log`, both exit0; artifacts and `evidence.json` under
`/tmp/koh-native-tiles-demo-pXXzIw`. Both ROMs are32768bytes. DMG ELF80008bytes and
PNG822bytes, result `00 00000044 D000 OK`; CGB ELF82380bytes and PNG805bytes, result
`00 00000043 D000 OK`. Raw RGBA SHA256 values:

- DMG: `5d21f5266cb14dfce3e6fefd01ad48ae41edf280923492ead5dcefd82835824a`.
- CGB: `26e9b35f016a5b1b4e0bed412bb384df061ead6d4f82baf77e66bed6b0f6dfc7`.

PNGs were visually inspected after pixel validation. These are local component
artifacts, not compilation performance or full sample-game/debug evidence.
Separate workspace doctests (currently no cases) and stable rustfmt checks exited0
(`/tmp/koh-tiles-doc.log`, `/tmp/koh-tiles-fmt.log`).

Predecessor pin head `19e4949` has successful completed
[Rust](https://github.com/retro-dev-tools/koh/actions/runs/43676239605) and
[CI](https://github.com/retro-dev-tools/koh/actions/runs/43676239601) workflows.
Native Linux/Windows/macOS Test steps succeeded; completed macOS job132696913473
log confirms584/584,0skips,271.132s. Current graphics-head hosted proof is separate.
Graphics commit `7c8b917` passed post-commit focused checks4/4,exit0,18.300s
(`/tmp/koh-tiles-postcommit.log`). The separate roadmap refresh changes only
documentation; it does not add code verification claims.

## Scalar optimizer contract migration

Eleven [comparison/conversion source methods](optimizer-scalar-contracts.md)
now have complete native replacements:70 new parameterized cases,920 ROM
executions and44,440 before/after IR evaluations. Both full affected targets
passed310/310, zero skipped,510.277s, exit0. Ledger191/565 complete,374
outstanding; no legacy compiler/test definitions physically removed.

## Real framework scene-flow acceptance

[Scene flow](scene-flow.md) closes one additional compiler-owned source method
with four native Debug/Release × DMG/CGB cases. Actual Game.Run executes two
deferred transitions and preserves constructor state across three scenes.
Final neighboring object/virtual/tileset regression16/16 passed112.176s, exit0.
Ledger192/565 complete,373 outstanding; no legacy definitions removed.

## Leaf interrupt runtime and reference correction

All five hardware vectors now root validated static leaf handlers in Rust.
ROM0 wrappers save AF/BC/DE/HL, call nonbanked bodies and restore before RETI;
main guards reserve the worst handler frame plus16 bytes. Real VBlank counts,
software requests, high MBC5 banks and deep-stack CPU restoration execute in
Debug/Release × DMG/CGB. Six source methods/11 assertion sites close: ledger
198/565 complete,367 outstanding; zero definitions physically retired.
The separately committed RETI correction fixes delayed IME in the C# emulator
rather than adapting compiler output to that bug. See [IRQ contract](interrupts.md)
and [reference correction](reti-reference-correction.md). Whole migration remains
incomplete; richer handlers/reentrancy, game/debug/SDK/release/performance/host
acceptance and C# compiler removal are still required.

## Leaf IRQ register IO

[Safe byte register accesses](interrupts.md#safe-register-io-follow-up) now let
actual VBlank handlers update scroll/palette state and a Joypad handler mask IE.
Bank/DMA/boot/speed-control writes, wide/dynamic IO and arbitrary pointers diagnose.
The larger VBlank body also passes full CPU restoration at a failing stack guard.
Current24 ROMs/52 diagnostics,207/207 neighboring checks and6/6 final IRQ checks
pass locally. Ledger remains198/565; no extra source dispositions are claimed.

## Bounded IRQ helper-call ABI

[Scalar IRQ call graphs](irq-calls.md) now use one ordinary ABI/body in ROM0,
address-based bank-switch elision and a one-byte context flag for shared guards.
Real16/32/64-bit calls, reference-library inclusion, modular overflow, shared
main helpers, bankzero/highbanks, exact CPU restoration and source mapping are
covered. The broad384/384 selection includes all four real2048 SDK game cases.
KDBG v1/high-bank unwinding remain explicit separate limitations, not execution
acceptance. Ledger remains198/565; no source test definitions physically removed.

## Generic rectangular-array arguments

[Scoped rectangular-array arguments](generic-rectangles.md) now pass through
generic calls across assemblies without losing element identity. Generic default
initialization and byref swaps/clears use typed array reference cells through the
existing storage/IR/ABI. Packed structs with colliding local/foreign metadata rows
and signed wide fields execute in Debug/Release × DMG/CGB; rank-three generic
arguments still diagnose. Constructed generic declaring layouts/statics remain
unfinished. No additional legacy dispositions are claimed; ledger198/565 unchanged.
The final compiler-library/lowering/SM83/generic/rectangle selection passes544/544
in741.555s, exit0 (`/tmp/koh-generic-rectangle-regression.log`);400 other driver
cases are excluded. Full-workspace execution remains separate.

## Closed generic declaring storage

[Closed types](closed-types.md) now retain separate declaring/method argument
scopes through packed value/reference layouts, direct methods, bound callbacks
and per-instantiation static initialization. Foreign PDB provenance survives ELF
and linked KDBG. No ABI or object format change is introduced. Constructed
inheritance/virtual/interface/constrained dispatch and variance remain unfinished.
The final selected library/closed-type/existing-generic run passes221/221 in83.682s
(`/tmp/koh-closed-types-owned-final.log`),430 excluded; this is not a full-workspace
execution claim. New coverage includes41 ROM scenarios,29 rejected compilations
and11 parameterized library cases. Workspace all-feature/all-target check and
separate rustdoc pass. Ledger remains198/565,367 outstanding; no legacy removal.

Post-commit product regression: all four real2048 SDK game cases passed in
228.987s, exit0 (`/tmp/koh-closed-sdk-game.log`),24 other SDK cases excluded.
Debug/Release × DMG/CGB builds execute real boot, title/Start, eight moves, exact
board/score/spawn transitions, glyph/tile/pixel checks and mode3 safety. This is
scene-based2048 acceptance, not every sample or arbitrary-PC debugger acceptance.

## Original 2048 source-test closure

The [original2048 matrix](original-2048.md) closes all16 source methods/32
assertion sites, including real SDK whole-game execution and retained board edge
conditions. Final12/12 selected cases pass125.675s, exit0;464 other driver/SDK
cases excluded. This includes the four existing board/stack-buffer regressions.
Whole-game checks cover full cartridge logo/checksums, boot, four input moves,
score/spawn transitions, HUD/map bytes, exact OAM and every board/cursor pixel.
The ledger advances to214/565 complete,351 outstanding; no physical retirement.

## Graphics showcase timeline closure

The [five remaining showcase methods](showcase-timeline.md) now have complete
PORT replacements retaining24 original assertion sites/36 expanded assertions.
Every coherent completed phase through79 must appear in order with exact sprite
OAM and DMG/CGB palette packets. Final4/4 passes47.303s,exit0,440 driver cases
excluded (/tmp/koh-showcase-timeline-final.log). No production game/emulator
changes. All nine showcase methods are closed; ledger219/565 complete,346
outstanding,zero physically removed.

## Original cube deadline and integration closure

The [three legacy cube methods](cube-phase-sweep.md#legacy-cube-sample-requirements)
now retain all eight source assertion sites/16 expanded assertions, with stronger
geometry/animation checks. Release full-frame/DMG again passes at250 frames;
double-buffered/CGB retains200. Multi-byte memory shifts, proven constant-return
call folding and bulk tilemap clearing reduce cost without an ABI/API change.
Source-width and near-exhaustion stack regressions protect the selected design.
All45 sample/art-integration source methods now have native replacements; the
compiler's remaining source groups still require closure. Ledger222/565 complete,
343 outstanding,zero physically removed. Final350/350 selected cases pass546.837s,exit0
(/tmp/koh-cube-budget-width-final.log),594 excluded. Both source-debugger and
four original2048 SDK configurations pass. Workspace check, formatting, separate
rustdoc and discovery1788 pass; all17 new cases are unignored. This remains a
selected execution result, not a current full-workspace pass.

## Comparison/branch source closure

Seven legacy methods now have native [predicate/consumer replacements](backend-audit.md#backendssm83comparebranchfusiontestscs-7). All18 original rows and both42/42,42/43
multi-use results survive;64-bit high-only and intervening-effect cases extend
coverage. Four Rust cases execute246 semantic ROMs plus12 measured calls. Final
4/4 pass14.056s,exit0; earlier neighboring45/45 pass78.238s. These are selected
checks. No production compiler changes or old private fusion model are added.
Ledger229/565 complete,336 outstanding; no physical legacy removal.

## Bank-qualified native test stops

Publishedc08a609 CI44088642289 failed after1070 Rust passes at the Debug scene-flow
packet assertion. Local Debug/DMG and Debug/CGB reproduced zeros instead of the
unchanged123456789/code1234/frame4 packet. The test host compared bare PC with a
function symbol, permitting an earlier stop at the same window address in another
ROM bank. Four independent tiny ELF/ROM probes (source banks2/257, DMG/CGB) first
failed before writing their expected42 packet.

Native test stops now require bank:address. Shared driver and every direct SDK/
DMA caller pass both linked symbol fields. The host checks ROM0/MBC1 mode and
full9-bit MBC5 state rather than the byte-sized display property. No production
emulator/debugger or compiler algorithm changed. Existing scene-state assertions
remain intact; a fresh8/8 then final22/22 pass239.993s,exit0
(/tmp/koh-stop-bank-final.log),458 other driver/SDK cases excluded. This is a
compiler-owned observation fix, not a new legacy disposition; ledger229/565
complete,336 outstanding. High-bank debugger/KDBG limits remain separate.
