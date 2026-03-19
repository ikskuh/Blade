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

## Incomplete spec (from `Docs/reference.blade`, excluding items already tracked in `TASKS.md` / `TODO.md`)

- `assert` statements from the reference language are not implemented in the frontend/binder pipeline yet (no `assert` keyword handling or assert statement binding).
- The reference says interleaving top-level code with declarations should emit a warning, but diagnostics currently define only `E*` codes and no warning path for that rule.
- Integer literals are currently lexed into signed `long` values, so reference examples that exceed signed-64 range (e.g. `0xFEdcBA9876543210`) are rejected during lexing.


## IR instructions not implemented yet

- `yield` and `yieldto:<target>` are emitted as TODO comments instead of real CALLD/continuation lowering.
- `pseudo.rep.setup`, `pseudo.rep.iter`, `pseudo.repfor.setup`, `pseudo.repfor.iter`, `pseudo.noirq.begin`, and `pseudo.noirq.end` are still unsupported pseudo-ops (TBD placeholders / empty iteration handling).
- `store.<space>` with multi-operand forms (indirect/complex store forms) reports unsupported lowering and falls back to comment + `MOV`.
- `bitfield.insert.<offset>.<width>` is only implemented for 1/4/8/16/32 aligned shapes; other forms report unsupported lowering.
- `binary.<op>` and `unary.<op>` variants not present in the current operator enums are unsupported.
- Unknown LIR instruction/opcode forms (including non-`LirOpInstruction` nodes that reach the lowerer) fall through to unsupported lowering.
- SSA phi argument transfer can still hit unsupported lowering (`phi-move`) when target block parameter registers are unresolved.
