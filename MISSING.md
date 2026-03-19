# Missing end-to-end compiler features (Blade/)

- `ParameterSyntax` accepts a storage-class token syntactically, but binder always rejects parameter storage classes as unsupported.
- Global `lut` / `hub` storage-class declarations are recognized, but binder reports them as unsupported instead of carrying them through codegen.
- Backend lowering still has a generic `E0401 UnsupportedLowering` path for opcodes/lowerings that are not implemented.
- `yield` / `yieldto` lowerings are placeholders (`TODO: CALLD`) and do not emit real coroutine transfer instructions.
- Recursive/coroutine/interrupt call lowering is not end-to-end: non-leaf tiers still fall back to generic `CALL` or comments instead of fully specialized call/return sequences.
- `pseudo.rep*` / `pseudo.noirq*` lowering is incomplete (`body length TBD`, iterator/end handling left empty).
- General `bitfield.insert.*` fallback is unimplemented beyond the aligned 1/4/8/16/32-bit cases.
- `store.*` lowering for multi-operand forms reports unsupported lowering and falls back to comment+`MOV` instead of complete store semantics.
- SSA phi move emission has an unsupported fallback when target block parameter registers cannot be resolved.
- Call-graph driven liveness/export features via attributes (e.g., `[Used]`, `[LinkName]`) are still TODO and not wired through parser→binder→IR→codegen.
