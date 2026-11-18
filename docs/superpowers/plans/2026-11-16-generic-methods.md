# Generic method instantiation

**Goal:** Compile reachable closed generic methods through the existing native
pipeline, preserving typed semantics, diagnostics and PDB provenance.

**Architecture:** Cache concrete method instances in the resolver. Each instance
has a unique internal function identity and an original MethodDef. Substitute
method arguments in signatures, locals, operand types and nested MethodSpecs;
ordinary reachability, IR verification, optimization and SM83 emission follow.
This is a staged increment in PR55, not a separate compiler or completed subset.

**Scope:** `generics.rs`, resolver storage, CIL lowering, source-symbol emission,
and Rust-driven Roslyn fixtures. Existing ABI/object format remain unchanged.
Class instantiation, constraints, named cross-assembly types, virtual generics and
delegates must receive their own implementation/acceptance rather than silently
passing through this first binding path. Unsupported shapes diagnose explicitly.

1. Write a real C# fixture with transitive byte/ushort instantiations, wide values,
   recursion, string/array references and distinct expected results. Observe the
   existing generic-call rejection in nextest before production changes.
2. Add bounded substitution and instance caching; wire signatures, local storage,
   calls and operand types into existing lowering. Reject unbound arguments and
   unvalidated constraints, retaining method/IL diagnostics.
3. Preserve original MethodDef source locations while emitting distinct instance
   symbols. Assert canonical ELF/linker round trips and execution on DMG/CGB in
   Roslyn Debug/Release, with stack and bank guards.
4. Add invalid/open/expanding-instantiation boundary tests and determinism checks.
   Run focused nextest, compiler/IR/backend regression, workspace check, rustfmt,
   separate rustdoc and CSharpier for tracked fixture source. Update dispositions
   only for source requirements with complete independent replacements.
5. Commit a working increment to the existing draft PR55, update its checklist
   and record actual exact-head CI state. Keep the full migration active.

Focused command (executed red, exit100):
`cargo nextest run -p koh-compile --test driver -E 'test(generic_method_instances_execute_transitively::case_1)'`
Use repository-documented .NET10.0.115 and serial shared target directory options.
The rest of this plan remains unverified until its actual logs are read.
