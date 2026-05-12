---
# blade-8zb6
title: Optimize storage of global `bool` variables
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

`bool` variables can be trivially tightly packed into a register. Each boolean
variable takes exactly a single bit.

The following operations map incredibly nicely to the Propeller 2 architecture:

- `a = true;` => `BITH backing, #index`
- `a = false;` => `BITL backing, #index`
- `a = !a` => `BITNOT backing, #index`
- `if(a)` => `TESTB backing, #index WZ`, `IF_Z JMP`
- `if(!a)` => `TESTB backing, #index WZ`, `IF_NZ JMP`
- `if(a & b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b ANDZ`, `IF_Z JMP`
- `if(a | b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b ORZ`, `IF_Z JMP`
- `if(a ^ b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b XORZ`, `IF_Z JMP`
- `if(a != b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b XORZ`, `IF_Z JMP`
- `if(a == b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b XORZ`, `IF_NZ JMP`
- `a = (x == y)` => `CMP x, y WZ`, `BITZ backing, #index`
- `a = (x != y)` => `CMP x, y WZ`, `BITNZ backing, #index`
- `if(a) { x |= 0x23; } else { x &= ~0x23; }` is `TESTB backing, #index WZ`, `MUXZ x, #$23`
- `if(a) { x = -x; }` is `TESTB backing, #index WZ`, `NEGC x`
- `if(a) { x += y; } else { x -= y; }` is `TESTB backing, #index WZ`, `SUMC x, y`
