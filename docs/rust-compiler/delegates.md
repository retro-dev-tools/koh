# Native delegates and callbacks

Status: implemented for the contract below; final workspace validation is in
progress. The full compiler migration remains incomplete.

## Language and metadata contract

C# authors use ordinary Action, instantiated Func and custom delegate types.
Roslyn emits their CLI metadata and CIL; Rust reads and validates the runtime
constructor and Invoke signatures without executing framework assemblies.
Recognized delegate ancestry is anchored to trusted framework identities.
Generic instances retain original nominal type arguments in their assembly
scopes independently of ABI storage. Distinct enums and their underlying
integers therefore remain distinct delegate types.

Supported operations include static method groups, closed reference-class
instance targets, capturing/noncapturing lambdas, verified virtual method groups,
constructor/field/static/parameter/return/array storage, mutable shared captures,
and the supported argument/return/byref types. ldftn/ldvirtftn become typed
metadata handles; they cannot escape into integer storage or the machine ABI.
The latter checks its binding receiver immediately. Constructors also check
closed instance targets before allocation; null invocation faults explicitly.

[ECMA-335, sixth edition, III.4.21](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
defines the verified ldftn/newobj and dup/ldvirtftn/newobj sequences and prohibits
branches into their interiors. That CLI-verifiable virtual sequence requires a
virtual method. Koh additionally supports the correct, not CLI-verifiable,
`dup/ldvirtftn/newobj` shape naming a nonvirtual instance method, including closed
MethodSpecs. III.4.18 correctness requires a nonstatic method defined for the
receiver; its null check still occurs before allocation. Koh checks receiver and
signature types and preserves the same no-interior-branches sequence restriction.
No dynamic dispatch slot is invented for a nonvirtual target. This deliberately
extends the earlier verifiable-pattern-only boundary without changing the ABI.
These relationships are checked before SSA block parameters rename values;
comparing raw SSA IDs was an incorrect initial implementation.

### Generic declaring and method scopes

Delegate signatures inside generic classes now bind both declaring-type `!n`
and method `!!n` arguments. The existing scoped signature binder previously
accepted only method arguments on its delegate path, incorrectly rejecting
ordinary `Func<T,T>` fields while laying out their containing class. Delegate
constructor/Invoke member binding now receives the same caller scopes before
selecting the nominal callback kind. Same-numbered parameters from the two
scopes remain distinct; argument storage widths and signedness are retained.
No generated class names or LINQ-specific patterns are recognized.

Ordinary closed generic class method groups and returned capturing lambdas are
covered, including a generic closure retaining a full64-bit captured value.
This changes compiler internals only; the C# authoring API, callback heap layout
and far-call convention remain unchanged. Foreign named arguments inside a
constructed delegate signature still require richer scoped layout binding and
diagnose explicitly. Neither parameter namespace may be silently substituted
from the other or reinterpreted in the delegate definition's assembly.

Four new rstest cases build actual Debug/Release C# and execute32 DMG/CGB ROMs:
int, wrapping byte, negative short, long, distinct class/method arguments,
array reference, returned generic capture and generic-owner method group.
Each returns42, restores SPD000 and preserves guards. Full256-byte packets check
wide/captured123456780000002A and untouched5A bytes independently of old compiler
output. Unit tests check distinct scopes, missing arguments and foreign-name
rejection for both parameter namespaces.

Before the fix, all four cases failed on `!0 requires instantiated storage`
(`/tmp/koh-generic-callback-red.log`, exit100). The first six-entry checkpoint
passed20/20 in11.916s (`/tmp/koh-generic-callback-first.log`, exit0). Expanded
binding/generic/layout/reference validation passed454/454 in198.597s, exit0,
443 explicitly unselected (`/tmp/koh-generic-callback-final.log`). After adding
the returned-capture and owner-method entries, all affected delegate fixtures
and scoped-substitution checks passed86/86 in42.660s, exit0,747 explicitly
unselected (`/tmp/koh-generic-callback-captures.log`). No production changes
followed that expanded checkpoint. All five LINQ methods remain outstanding;
this is a shared compiler correction, not a claim that pipeline reductions work.
Fresh nextest discovery reports2053 workspace/1876 compiler cases, zero ignored
(`/tmp/koh-generic-callback-discovery.json`, exit0). Final rustfmt/CSharpier and
diff checks pass. Discovery is not full workspace execution; current-head SDK,
debugger and three-platform acceptance are not claimed by this focused increment.

## Heap and calling ABI

A delegate consumes eight bytes in the ordinary checked managed heap:

| Offset | Width | Meaning |
| --- | --- | --- |
| 0 | 2 | Allocator payload count,6 |
| 2 | 2 | Callback wrapper code address |
| 4 | 2 | Callback wrapper ROM bank |
| 6 | 2 | Retained target object reference; zero for static targets |

Words use little-endian order. References point at the allocation header.
Nominal delegate types are verified in IR; object casts and arbitrary pointer
fabrication do not expose this payload as an ordinary class object.

Each target/delegate instantiation receives an ordinary compiler-generated
wrapper with receiver-plus-arguments parameters. A static target ignores that
receiver; an instance wrapper checks/casts it to the declaring class before the
normal call. A virtual wrapper resolves the retained object's immutable runtime
type through the existing closed-world dispatch binder. It preserves the
method selected by binding that same object; changing a local variable later
cannot change the retained object.

Invoke reads address, bank and receiver under the normal temporary WRAM bank1
selection, restores the previous selection, and uses the existing far-call ABI
and caller-bank cookie. Explicit bank writes performed by user callbacks retain
their normal observable effect. Pin verification includes all reachable callback
targets and rejects such writes while a pin can be active. Byref escape analysis
includes indirect results. The 16-bit bank word preserves MBC5's ninth bank bit.
Extended SP addressing uses DE, so the code address is loaded into BC before DE
receives the bank. Results and arguments use the ordinary supported widths.

Wrapper, source-method, closure and virtual-implementation dependencies are
included transitively. No managed execution engine or secondary heap is added.
ELF relocation expressions carry code addresses and banks to the Rust linker.
Allocation, stack and ROM capacity limits remain explicit compiler/runtime
faults. Null faults use code4; allocation exhaustion uses code5.

## Supported boundary

Exact nominal target signatures are supported, including valid variance-marked
declarations with the exact instantiated signature. [Generic parameter flag
words are validated](delegate-parameter-flags.md). Variance conversions/adaptation, closed-static
and open-instance forms, multicast/equality APIs, async delegate APIs, arbitrary
calli and boxed value-type receivers still
require explicit support decisions and diagnostics. This is a visible current
boundary, not approval to retire useful legacy coverage. Interface/general
generic/language-runtime completion remains a whole-migration gate.

## Evidence and limitations

Actual Roslyn Debug/Release assemblies run through canonical ELF and the Rust
linker in the existing C# emulator. Normal cases force each managed function,
wrapper and target into a separate ROM bank starting at1 or257. Assertions check
literal results, restored SP, frame guards and untouched CGB bank2. The wide
case retains a live200-byte stack allocation across invocation. Constructor
fields, static reassignment, returned factories, callback arrays, mutable
captures, byte/enum parameters and64-bit/ref arguments have independent known
expected values.

An integrated checkpoint passed478/478 cases in134.829s, exit0:
`/tmp/koh-delegate-integrated.log`. Additional verifier effects pass9/9;
null/nominal rejection cases pass6/6 in34.523s; static-lambda/sealed-token cases
pass10/10 in61.192s; returned callback factories pass4/4 in30.469s. Expanded
Debug/DMG normal cases pass10/10 in60.268s. Those are selected checkpoints;
`/tmp/koh-delegate-workspace.log` records the final full run:1,193/1,193
passed in2,764.108s, zero skipped, exit0. All44 frozen sources still match.
Separate rustdoc passes for four crates/zero doctests. At this callback checkpoint the source-method ledger
is62/565 complete,503 outstanding and zero physically retired; later increments
are recorded in test-disposition.md.

Review caught enum identity being erased by storage normalization; raw nominal
comparison fixes both instance interning and target binding. Forged enum/byte
callbacks bound to an int target now reject. Another review suggestion concerned
nonvirtual ldvirtftn construction; it was initially withdrawn under the standard's
verified-construction rule. The later explicit extension above supports this
correct but unverifiable CIL shape with typed receiver/target checks. Metadata tests reject malformed runtime contracts
and forged framework-reference identities.

A Release null-binding fixture initially had no binding operation in its IL:
Roslyn removed the unused conversion, leaving ldnull/pop/return42. The fixture
now stores the callback in a static field, retaining the operation in both build
configurations. The null-fault assertion and pre-allocation heap check remain.
Native compilation follows the assembly's actual CIL, not reconstructed C#.

The nonvirtual-pointer regression patches actual Roslyn SealedGroup `ldvirtftn`
tokens to Counter.PlainAdd and a closed Counter.Identity<int> MethodSpec. Both
return21 rather than the override's42. A patched null binding retains fault4,
restored frame guards and a completely unallocated heap. The original rejection
is recorded in `/tmp/koh-nonvirtual-ldvirtftn-red.log`; initial four-configuration
acceptance passes4/4 in24.462s before the generic/null extensions. Final results
pass496/496 in86.152s, exit0 (`/tmp/koh-nonvirtual-ldvirtftn-final.log`), including
compiler-library, IR, generic, pointer and null regressions. Read-only review found
no confirmed blockers. No feature retirement or whole-migration completion
is implied.

The unchanged JRPG now builds through ordinary Release SDK/native tool discovery
into a131072-byte ROM, zero warnings/errors,45.81s, exit0:
`/tmp/koh-jrpg-callback-build.log`. This establishes compilation, not gameplay,
source-level breakpoints or hardware validation. Full workspace/cross-platform
results and the test-disposition ledger must be checked before final acceptance.
