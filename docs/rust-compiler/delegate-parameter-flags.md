# Delegate parameter flag validation

The Rust delegate binder previously validated arity, ancestry and runtime method
contracts but skipped generic-parameter flag words. A reached delegate could
therefore carry impossible variance bits, contradictory reference/value special
constraints or reserved bits and still compile. The binder now rejects those
words before registering a delegate. The diagnostic identifies the parameter;
CIL callers retain their member and IL-offset context.

[ECMA-335 II.22.20 and II.23.1.7](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf)
permit covariance/contravariance on delegate classes and interfaces. The two
variance alternatives cannot be combined. The known bit mask also accounts for
the [.NET10 `AllowByRefLike` flag](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Reflection/GenericParameterAttributes.cs).
Recognizing a flag word does not implement every constraint or byref-safety
feature associated with it.

Valid exact nominal calls on `Producer<out T>` and `Consumer<in T>` already
worked; they are preserved. This does not add conversion between different type
arguments, cross-instantiation interface variance,
boxing or other unsupported runtime shapes. [Delegate generic arguments now use
canonical scoped binding](foreign-callback-scopes.md). A type-definition lookup is also
not an instantiation/constraint check. The initial suspected malformed-metadata
diagnostic on valid delegates was not reproduced on those actual binding paths;
the confirmed defect was acceptance of malformed flag words.

The check reuses the existing delegate path and ownership/arity rules. It filters
the owning TypeDef's parameters, accounts for the metadata scan under the existing
delegate-work limit, and skips scanning when the checked arity is zero. No
parallel type model, new public API, dependency, runtime fallback or unsafe Rust
is added. General variance-position/constraint validation is not established by
this flag-word test and remains part of the broader supported-subset audit.

## Native acceptance

`tests/fixtures/Variance` now includes real covariant/contravariant delegate calls
and the existing interface/boxing probes. The SDK emits Debug and Release DLLs.
Four new rstest cases in `tests/driver/variance.rs` retain exact signatures:

- The original variant word and a nonvariant word both compile/link/run with
  independently expected42: eight ROM executions in total.
- The checked delegate allocation has a6-byte payload, null static receiver,
  code/bank identifying a linked method, and unchanged bytes after the allocation.
  SP/result/guard checks remain enabled; a real delegate is not falsely expected
  to allocate nothing.
- Each of03/0C/0D/0E/40/41 is rejected in both directions/configurations:
  24 malformed inputs, each asserting the diagnostic, caller and IL offset.
- The original compound-generic rejection was an implementation boundary. Its
  replacement executes the valid program and checks42 and the complete heap;
  flag/allocation/invalid-input assertions remain. See the subsequent scope results.

CLR execution of the ordinary fixture is investigative compatibility evidence;
the normative flag rules and independent ROM outputs supply acceptance. No
malformed assembly is executed by the metadata reader or compiler.

The decisive red run failed4/4 because invalid generic delegate flags were
accepted (`/tmp/koh-delegate-flags-validation-red.log`, exit100), after valid
calls and their allocation assertions had succeeded. The first positive check
then passed8/8 in6.692s. Expanded compiler-library/reference/delegate/generic
regression passed **326/326 in82.642s**, exit0,426 explicitly unselected
(`/tmp/koh-delegate-flags-regression.log`). Final stricter caller-context checks
passed8/8 in6.593s, exit0 (`/tmp/koh-delegate-flags-final.log`).

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --test references --locked --no-fail-fast \
  -E 'binary(koh_compile) | binary(references) | test(variance::) | test(delegates::) | test(generic_types::) | test(generics::)'
```

These new regressions close no additional legacy test disposition. Full migration
and final current-head hosted validation remain incomplete.

Subsequent [exact variant interface binding](exact-variance.md) admits valid nominal
interface calls; it does not add cross-instantiation variance conversions.
Subsequent scoped delegate closure validates variance positions and special
constraints before interning, including an unused type argument's runtime
declaration. The flag-word-only checkpoint is not the current boundary.
