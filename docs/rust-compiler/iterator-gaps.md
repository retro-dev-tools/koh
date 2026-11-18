# Iterator investigation history

The three original CilIteratorTests methods now have native replacements. See
[structured cleanup and iterator execution](finally-iterators.md) for the current
contract, independent expectations, SDK/debugger evidence and remaining limits.

The investigation exposed shared compiler bugs rather than invalid C#:

1. Null interface Extends was converted to TypeDef02000000.
2. Valid variant interface declarations were blanket-rejected.
3. AllowByRefLike permission incorrectly rejected ordinary closed arguments.
4. All extra method sections were rejected, including normal finally cleanup.
5. System.IDisposable was misclassified as an unknown primitive.
6. Nongeneric dispatch discarded a returned generic interface's assembly scope.

These were fixed in shared reading/binding/lowering paths. The native framework
CurrentManagedThreadId getter additionally needed an explicit target contract:
one logical managed thread, identity1; it is not arbitrary host code execution.
No generated iterator type/field names are special-cased.

Earlier296/296,300/300 and308/308 focused results are intermediate evidence for
null-base, exact-variance and anti-constraint fixes. The final iterator/cleanup
selection passed315/315. Ordinary yields and consumer disposal execute; catch,
filter, fault, exceptional unwinding and yield bodies that require fault clauses
remain unsupported with explicit diagnostics.
