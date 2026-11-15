# Type and module initialization

Native compilation executes supported `.cctor` bodies as ordinary lowered CIL.
It does not evaluate managed code on the host or replace constructors with default
values. Constructor dependencies participate in the same iterative reachability
queue, verifier, optimizer, dynamic stack ABI and ELF pipeline as game methods.

## Triggers and ordering

The rules follow [ECMA-335](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
I.8.9.5 and II.10.5.3, within the supported single-threaded subset:

- Strict types check initialization before static method execution or field access.
- `beforefieldinit` types defer until field access. Pure methods do not force their
  unused initializer or its dependencies into the ROM.
- A module checks its initializer before executing its managed methods or accessing
  its fields. This uses the [module initialization guarantees](https://github.com/dotnet/runtime/blob/main/docs/design/specs/Ecma-335-Augments.md#module-initialization-guarantees).
- Static-store operands are evaluated before the field-access check. Constructor
  calls must preserve the pending value, then the actual store overwrites any
  constructor assignment to that field.

Deferral is Koh's deterministic choice where the specification permits earlier
initialization; it is not a claim that every desktop runtime chooses that timing.
No base-type initializer is implicitly executed merely because another type is
initialized. Actual field/call dependencies determine the required checks.

Each reached constructor has one byte of linker-placed, startup-zeroed state:
0 means unstarted, 1 means executing, 2 means completed. A generated guard sets 1
before calling the body and 2 after a normal return. Nonzero state returns without
calling again. Recursive accesses on the same execution thread therefore observe
the fields initialized so far, including default zeros in cycles, without infinite
recursion. All guards use verified IR memory/call/control-flow operations.

## Identity, storage and validation

Generated guard function identities use reserved compiler tag FF plus the original
constructor's MethodDef row. FF is not a CLI metadata table and is never accepted
from a managed call token. `InitializerAddress` retains the actual assembly/MethodDef
identity for its state allocation. Source constructors keep their real identities
and IL offsets. Generated helper instructions have synthetic locations; method-entry
and field checks associate with their triggering IL location. PDB/KDBG integration
must distinguish generated helpers from source methods.

The guard and its state are discovered only when required. State bytes share the
existing packed BSS/static capacity and use ordinary canonical ELF relocations.
Constructor names, static/special flags, signatures and nongeneric declaring types
are validated before a guard is emitted. Readonly static writes/addresses are
allowed only from the declaring constructor; reads remain normal field loads.
Explicit managed calls to `.cctor`, or selecting a constructor as cartridge entry,
diagnose rather than bypass once-only initialization.

Intrinsics remain explicit hardware contracts: the desktop buffers/initializers
behind a register or region accessor are not ordinary reached game code. There is
no initializer exemption for unrelated managed helper methods.

## Execution evidence

Real Debug/Release Roslyn fixtures exercise both application and reference-library
module initializers, readonly field initializers, entry-type initialization,
recursive calls into a constructor's type, cyclic types reading default-zero state,
repeated accesses, deferred field initialization and dead initializer exclusion.
A trace has independently derived order 2,1,3,4,5,6,7,8,9; the last two digits check
right-hand-side evaluation before the target field's constructor. Counters must
remain one and the final store must retain 42 across the constructor call. ROMs
execute in the existing C# emulator against verified nonzero RAM poison.

A test-only object mutation removes generated guard calls. The same program must
then return its initialization-failure value 1 instead of 42. This calibrates the
execution assertions against omitted initialization; the production object remains
unchanged. The earlier static-field fixture now executes its initializer-dependent
entry and expects 17, replacing a temporary unsupported-feature assertion.

Existing throwing type/module fixtures must still fail compilation, now at the
actual unsupported exception construction opcode/member rather than a blanket
initializer boundary. One referenced-library fixture method intentionally suppresses
CA2255: testing a library module initializer is precisely its advanced scenario.
This was required by an observed Roslyn analyzer failure; no production analyzer,
NuGet audit setting or test assertion was disabled.

## Remaining limits

Exception handling, generic storage/constructors, object/array initialization and
other unsupported body operations still diagnose. Failed-constructor exception
caching/recovery is not implemented. A supported native arithmetic/stack fault in
a constructor aborts the ROM through the normal trap path, with no resumed program.
Managed threads and interrupt-driven reentrancy are unsupported. IRQ migration must
define initialization/IME restrictions before allowing constructors to enable IME.
This increment does not establish game-level SDK/debug acceptance or retire an
entire legacy test method.
