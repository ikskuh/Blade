# Type System Demonstrators

These fixtures exercise the CS-2 and CS-6 type-system work:

- `pass_empty_aggregate_types.blade`: empty struct/union/enum/bitfield aliases bind cleanly.
- `pass_enums.blade`: contextual and qualified enum literals plus open-enum casts.
- `pass_unions.bound.blade`: union member access and assignment bind like struct fields.
- `pass_pointers_mir.blade`: single vs multi-pointer rules and volatile MIR memory ops.
- `pass_bitfields_codegen.blade`: aligned bitfield reads/writes lower to specialized P2 instructions.
- `fail_enum_literal_context.blade`: bare enum literals without context are rejected.
- `fail_pointer_family_ops.blade`: deref and indexing stay split between `*T` and `[*]T`.
- `fail_pointer_assignability.blade`: qualifier dropping, weaker alignment, and storage mismatch are rejected.
- `fail_bitfield_overflow.blade`: bitfield declarations that exceed the backing width are rejected.
- `fail_union_and_enum_type_rules.blade`: unknown union fields, cross-enum assignment, and enum arithmetic are rejected.
