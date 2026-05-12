---
# blade-8tp6
title: Task/Layout Refactoring
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:33Z
updated_at: 2026-05-12T21:49:33Z
parent: blade-46a7
---

Imported from TODO.md.

### Proper image size planning

- The memory map does not contain the images themselves.
  - Each `cog task` image is 504 long values (32 bit), so 2016 byte
  - Each `lut task` image is (for now) also 512 long values large, so 2048 byte
  - Each `hub task` image has zero size, but will (later) require additional memory allocations to cater for its initializer-function and `cog fn` loader.
  => We can start by pretending each image is exactly 2048 byte large for the first implementation.
- The entry point image for `cog task main` must be locateed at hub address 0. No `hub fn` must be located below hub address 0x400.

### Proper layout resolution

Right now, we perform a global layout resolution, which is wrong.

In general, Layouts have to be conflict free for:

- actually used variables/declarations
  - We don't have to care for variables that are not used in the source code
  - Future `[Used]` attribute may change this behavior
- `hub` (which is the trivial case)
  - All hub variables get unique values
- `cog` and `lut`

### Improvement on cog codegen

We can actually fuse cog resource layouting and code layouting, as we know the code size in instructions.

### Implement Mir/Lir validation

Right now, it's possible to construct IR code that uses never-set values. This is illegal and should be asserted that we're always producing sane code. This assertion must run between lowering, all optimization steps and the emission of this IR.

#### Technical Debt: Symbol naming for external symbols isn't well specified

Two layouts can declare a symbol `rt_result`, which will be correctly split into two symbols by the cmpiler.

This yields the issue that we cannot refer to one through `extern var rt_result: u32;`  as the compiler still (correctly) performs the symbol distinction.

Correct solution here is the introduction of a `[linkname("")]` attribute for the variable.

### Improvement: Remove emission of unreferenced globals

Right now, all `hub const` values are emitted into the binary imgae. This is only necessary when the values are pointed to, which is something we can detect.

All unpointed values can be erased.

### g_global_yield_state must be deleted

it can be safely replaced by "yield to INA", which is effectively a value discard.

This means we should introduce this concept on a broader scale to implement it.
