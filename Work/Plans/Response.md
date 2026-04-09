- Types, imports and values cannot alias. They are in the same namespace, and trigger an "X already declared here" diagnostic.
- Shadowing is forbidden. Period.
- Modules are imported either as their module name ("import foo;" imports a symbol "foo"), while imports with file names require an alias declaration. An alias declarations always overwrites the name of the symbol that is placed in the namespace)
- Blade has no concept of "private", so every global symbol is exported (this does not include top-level local variables like `var x: u32 = ...`, only globals with explicit storage)
- A module exports every global symbol, this includes imported modules. This allows `std.foo.bar.bam` to be used. Otherwise it would not be possible to have nested modules/namespaces available.
- `import foo;` imports a symbol "foo", while `import foo as bar;` imports a symbol "bar" (but never a symbol "foo"), so yes, we keep aliasing for both forms.
- "builtin" is not a special module except that it has no file on disk and can potentially synthetic types and values not available to the outside world. It is not special in the syntax or semantics otherwise. This means "import builtin as b;" is allowed.
- "Same module" is based on GetFullPath. This may change in the future, so take care that it's not tied in too deeply into the code at many places.
- Module imports are not case-insensitive. `import foo;` and `import Foo` may import different modules. On Windows, file-path comparison is case-insensitive, but module name comparison must be case-sensitive.
- Circular are forbidden.
- One diagnostic with a cycle chain
- All constructs must be usable at comptime as if they are local. There must not be a synthetic border between modules.
- A const global (reg const foo: u32 = ...) must be usable at comptime, as the value cannot change (it's immutable storage, thus comptime can access it). Otherwise, it's not possible to declare two arrays with the same size sourced from one value!
- Modules are eagerly bound right now.
- `a.b.c` must work for every construct (accessing fields, modules, enum members, ...)
- `type Foo = u32;` must work the same as `type MS = builtin.MemorySpace;`, so builtin.MemorySpace should be an expression allowed in a type context.


As types can alias, we need a `TypeSymbol` which points into a `BladeType`.

`TypeSymbol` is the wrong name as of today, so we have to rename `TypeSymbol` into `BladeType`, then reintroduce a new `TypeSymbol(string name, BladeType type) : Symbol(name)` which is a reference to the actual type. This allows type aliases to work nicely.

Now create a proper plan to implement these changes, unless there's still open questions.
