# Current known compiler bugs

All open bug entries were moved into `TASKS.md` (see `Bug Fix Backlog`, `BUG-3` through `BUG-9`) on 2026-04-08.

Newly discovered issues should be logged here first (as a short repro + description), then transported into `TASKS.md`
once they become scheduled work items.

- `extern var foo: u32;` at module scope was incorrectly treated as a global declaration instead of an automatic top-level variable, so it compiled instead of producing an extern-on-automatic diagnostic. Repro: `Demonstrators/Bugs/illegal_extern_var.blade`.
