# Struct Literal Lowering

## Summary
Implement struct literals as abstract aggregate values through MIR and LIR, then lower them in ASMIR to a contiguous byte-accurate memory image split into 32-bit register lanes. Register-space structs must use the same natural layout as hub-space structs: field offsets and padding come from the existing `StructTypeSymbol` / `AggregateMemberSymbol` layout, not from “one field equals one register.”

Use the repo’s valid hardware expectation spelling: `// EXPECT: pass-hw`.

## Key Changes
- Define one aggregate layout helper used by lowering:
  - Struct byte layout is the existing binder-computed layout: natural alignment, padding, and `StructTypeSymbol.SizeBytes`.
  - Register/LIR physical representation is `ceil(SizeBytes / 4)` 32-bit lanes.
  - Small fields (`bool`, `bit`, `nit`, `nib`, `u8`, `u16`) occupy their normal hub-layout byte range inside a lane, not a whole lane.
  - Runtime field initializers are lowered as normal expressions first, then stored into the target field byte range.
- Keep MIR aggregate-aware:
  - Preserve `MirStructLiteralInstruction`, `MirLoadMemberInstruction`, and `MirInsertMemberInstruction` as semantic aggregate operations.
  - Ensure struct literals carry field/member identity plus initializer value IDs, not pre-packed integers.
  - Keep nested struct values as aggregate values so assignments like `Outer { .inner = Inner { ... } }` do not flatten names or lose provenance.
- Keep LIR aggregate-aware where reasonable:
  - Preserve aggregate operations in LIR with typed `AggregateMemberSymbol` metadata and struct result types.
  - Do not encode field layout decisions into LIR opcode names or field-register mappings.
  - Update LIR optimizers to treat aggregate-valued instructions as normal SSA values until ASM lowering.
- Lower aggregate values in LIR → ASMIR:
  - When an aggregate LIR value is materialized, allocate/register-associate a contiguous lane set for that value.
  - `structlit` zeroes all lanes, evaluates each initializer operand, and stores it into the correct byte offset using lane-relative byte/word/long writes.
  - `load.member` reads the member byte range from the receiver’s lane set and returns either a scalar value or an aggregate lane set for nested aggregate fields.
  - `insert.member` copies all receiver lanes, then overwrites the member byte range with the new scalar or aggregate value.
  - Field stores become address-plus-offset operations internally; once ASMIR is emitted, instructions address lanes and byte offsets rather than “struct fields.”
- Extend storage and ABI handling for aggregate lanes:
  - Register/LUT aggregate storage reserves `ceil(SizeBytes / 4)` LONG lanes; hub aggregate storage reserves `SizeBytes` bytes with existing alignment.
  - `load.place` / `store.place` for aggregate values copy all lanes, using offset operands where needed.
  - Phi moves, branch/goto arguments, function arguments, and register returns copy aggregate lane sets as contiguous lanes.
  - The calling convention allocates enough internal register places for each aggregate parameter/return lane while preserving existing scalar behavior.
- Remove completed unsupported-lowering paths:
  - `structlit`, ordinary `load.member`, and ordinary `insert.member` should no longer report `E0401` for supported struct layouts.
  - Keep unsupported diagnostics only for genuinely unsupported cases, such as non-struct aggregate gaps or bitfield insert shapes not covered today.

## Demonstrators And Tests
- Change `Demonstrators/IR/pass_struct_literal_pipeline.blade` to `// EXPECT: pass` and update its note.
- Add `Demonstrators/HwTest/hw_struct_literal_lowering.blade` with `// EXPECT: pass-hw`.
  - Use a struct mixing small and word-sized fields, for example `flag: bool`, `tag: u8`, `count: u16`, `left: u32`, `right: u32`.
  - Build the struct from runtime parameters, pass it through at least one function boundary, read fields back, and combine them into `rt_result`.
  - Include runs that prove small fields are packed into the correct byte offsets and that the two `u32` fields survive multi-lane transport.
- Add focused tests for:
  - MIR/LIR preserving aggregate operations and member identity.
  - ASM lowering of byte/word/long field stores into lane-relative operations.
  - Struct literals with runtime initializers, not only constants.
  - Nested struct literal initialization and nested member reads/writes.
  - Aggregate values through block parameters, function arguments, and function returns.
- Update existing aggregate gap tests and docs:
  - Remove expectations for `unhandled: structlit`, ordinary `load.member`, and ordinary `insert.member`.
  - Update `Work/MissingIr.md` so completed struct literal/member lowering is no longer listed as reachable unsupported lowering.

## Assumptions
- Existing binder layout is the source of truth for struct size, alignment, field offsets, and padding.
- Register-space struct representation is a byte-accurate image of hub-space layout split into 32-bit lanes.
- Small scalar fields are byte-addressable within the image according to their runtime size; they are not bit-packed unless the type is a `bitfield`.
- LIR remains typed enough to carry aggregate values; actual lane expansion is an ASM lowering concern.
- Verification for implementation will include MCP `build_compiler`, targeted regression fixtures, `just coverage`, `just accept-changes`, and `git diff` review.
