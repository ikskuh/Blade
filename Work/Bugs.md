# Current known compiler bugs

All open bug entries were moved into `TASKS.md` (see `Bug Fix Backlog`, `BUG-3` through `BUG-9`) on 2026-04-08.

Newly discovered issues should be logged here first (as a short repro + description), then transported into `TASKS.md`
once they become scheduled work items.

- Inline asm current-address operands (`$` / `#$`) were lowered correctly but emitted as the sanitized label `l_` in final assembly instead of literal `$`. Repro: `Demonstrators/Asm/asm_current_address_rep_loop.blade`.
- `extern var foo: u32;` at module scope was incorrectly treated as a global declaration instead of an automatic top-level variable, so it compiled instead of producing an extern-on-automatic diagnostic. Repro: `Demonstrators/Bugs/illegal_extern_var.blade`.
