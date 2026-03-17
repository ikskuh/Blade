# Language Feature Demonstrators

These examples exercise recently implemented frontend and lowering features:

- `pass_array_literals.bound.blade`: bound-tree view of explicit, spread, and empty contextual array literals.
- `pass_array_literals_bool.bound.blade`: bound-tree view of non-`u32` array literals using `bool` elements and spread fill.
- `pass_array_literal_inference.bound.blade`: array literal element-type inference without an expected target type.
- `pass_array_literals_mir.blade`: MIR lowering for explicit, spread, and empty array literals.
- `fail_array_literal_type_mismatch.blade`: rejects array elements that are not assignable to the inferred/contextual element type.
- `fail_array_literal_bool_type_mismatch.blade`: rejects non-`u32` array literals whose elements do not match the contextual `bool` element type.
- `fail_array_literal_spread_not_last.blade`: rejects spread elements that are not in the final slot.
- `fail_array_literal_requires_context.blade`: rejects empty and spread-only literals without an expected array type.
- `fail_array_literal_unknown_length_context.blade`: rejects empty/spread literals when the contextual array length is not known.
- `const_and_named_args.blade`: local `const` bindings with runtime initializers and named call arguments.
- `operators_and_address_of.blade`: new unary and binary operators, short-circuit boolean operators, and address-of on locals.
- `casts_and_bitcasts.blade`: explicit `as` casts and same-width scalar `bitcast` conversions.
