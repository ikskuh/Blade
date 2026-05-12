---
# blade-y4xd
title: New rules on interrupt handlers + function pointers
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

Interrupt handlers must be installed similar to this:

```blade
// Function pointer syntax:
type Int3Handler = *int3 fn();

// Extern variables for the handler locations:
extern reg var IRET1: u32         @(0x1F5);
extern reg var IJMP1: *int1 fn()  @(0x1F4);
extern reg var IRET2: u32         @(0x1F3);
extern reg var IJMP2: *int2 fn()  @(0x1F2);
extern reg var IRET3: u32         @(0x1F1);
extern reg var IJMP3: Int3Handler @(0x1F0);

// Handler functions:
int1 fn int1_fn() {}
int2 fn int2_fn() {}
int3 fn int3_fn() {}

// Functions that are having a pointer taken must not be elided and are considered reachable,
// otherwise they'd be eliminted by DCE and the pointers would go into emptyness.
// Setup of the handlers must work like this:
IJMP1 = &int1_fn;
IJMP2 = &int2_fn;
IJMP3 = &int3_fn;

// Function pointers require explicit calling convention annotation:
var reg fptr1: *fn(a: u32, b: u32) = undefined;
var reg fptr2: *fn(a: u32, b: u32) -> u32 = undefined;

fptr1(10, 20);
var out: u32 = fptr2(10, 20);
```
