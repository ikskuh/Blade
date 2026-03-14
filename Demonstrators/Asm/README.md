# Inline Assembly Demonstrators

These examples show different inline-assembly use cases in Blade:

- `math_routines.blade`: tight math/dataflow-heavy routines written with plain `asm`.
- `io_regular_asm.blade`: plain `asm` blocks that perform observable IO side effects and therefore must not disappear.
- `volatile_routines.blade`: `asm volatile` blocks where the exact instruction text and ordering matter.
- `optimizer_exercises.blade`: small routines meant to exercise copy propagation, dead inline-asm removal, and raw fallback behavior.
