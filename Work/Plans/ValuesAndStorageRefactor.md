# Atomic Refactor: Typed Blade Values and Module-Level Data Blocks

## Summary
Replace the current late, function-embedded storage emission with a true module-level data model, and replace `object?`-based value handling with typed Blade values tied to the analyzed type hierarchy.

The refactor is **single-cut** and removes the old compatibility shapes instead of layering shims on top. After the refactor:
- functions contain only code nodes
- module-level storage/constant/external blocks are first-class typed objects
- runtime-emittable values use `RuntimeBladeValue`
- comptime-only values use `ComptimeBladeValue`
- `TypeFacts` is deleted and replaced by properties/methods on concrete type symbols
- writer logic formats symbols by symbol type, not by name heuristics

## Key Model Changes
### 1. Split the type hierarchy
Refactor `TypeSymbol` into two immediate families:
- `RuntimeTypeSymbol`
- `ComptimeTypeSymbol`

Re-parent all concrete types under one of those two branches:
- `RuntimeTypeSymbol`: bool, integer widths/sign variants, pointers, arrays, structs, unions, enums, bitfields
- `ComptimeTypeSymbol`: void, module, function, string, range, unknown, integer literal, undefined literal

Add to `TypeSymbol`:
- `bool IsLegalRuntimeObject(object value)`

Add to `RuntimeTypeSymbol` the instance-owned layout/normalization surface that replaces `TypeFacts`, including:
- scalar bit width / signedness where applicable
- `SizeBytes`
- `AlignmentBytes`
- storage-space size/alignment queries for reg/lut/hub
- runtime normalization/validation for CLR payloads

Delete `TypeFacts` entirely. Every current `TypeFacts.*` call site must move to the appropriate `RuntimeTypeSymbol` property/method or concrete subtype property.

Split `PrimitiveTypeSymbol` into real subtypes:
- `ScalarTypeSymbol` as the common runtime scalar base
- `BoolTypeSymbol`
- `IntegerTypeSymbol`
- pointer types continue as dedicated runtime types, but derive from `ScalarTypeSymbol` semantics where appropriate

`BuiltinTypes` must return concrete subtype instances, not generic primitive placeholders.

### 2. Introduce typed Blade values
Add:
- `abstract class BladeValue(TypeSymbol Type, object Value)`
- `sealed class RuntimeBladeValue(RuntimeTypeSymbol Type, object Value)`
- `sealed class ComptimeBladeValue(ComptimeTypeSymbol Type, object Value)`

Introduce singleton CLR payload marker types and remove `null` as a semantic value:
- `VoidValue.Instance`
- `UndefinedValue.Instance`

Define the legal CLR payloads exactly:
- bool: `bool`
- signed integers: exact signed CLR width (`sbyte`, `short`, `int`)
- unsigned integers and pointers: exact unsigned CLR width (`byte`, `ushort`, `uint`)
- arrays: `IReadOnlyList<RuntimeBladeValue>` / `IReadOnlyList<ComptimeBladeValue>` as appropriate
- structs/unions: `IReadOnlyDictionary<string, BladeValue>` with field/member names as keys
- function/module comptime values: `FunctionSymbol` / `ModuleSymbol`
- void/undefined: the singleton marker types only

Refactor comptime evaluation so successful evaluation results are typed Blade values, not `ComptimeType + object?`. Keep `ComptimeFailure` as the failure channel, but remove the current enum/object payload design.

### 3. Replace function-embedded data with module-level typed blocks
Delete the use of `AsmSectionNode` and `AsmDataNode` as storage modeling primitives.

Add to `AsmModule` a first-class module data model:
- code functions remain separate
- data blocks are module-level typed blocks, not function nodes

Use explicit block kinds:
- register block
- constant block
- LUT block
- external block
- hub block

Keep reg/lut/hub as separate allocation sections. Hub remains the last emitted data block.

Each block stores **unordered** definitions internally using set/map semantics for uniqueness; do not preserve declaration order. Use hash-based storage internally and sort only at final layout time.

Introduce typed data-definition objects instead of label-plus-directive pairs. The minimum split should be:
- allocated storage definition for actual emitted data
- shared constant definition
- external/fixed binding definition

`StoragePlace` stops owning emitted data state:
- remove `StaticInitializer` from `StoragePlace`
- remove storage-emission metadata from function/code paths
- keep `StoragePlace` as symbol identity for addressable storage
- pair it with module-level data definitions for actual emitted storage

Shared register spill slots and legalized shared constants also become module-level data definitions, not appended code nodes.

### 4. Unify symbol references and remove place special-casing
A place is a symbol. Stop treating it as a separate operand category.

Refactor ASM operands so storage references use the normal symbol-reference path:
- remove `AsmPlaceOperand`
- use symbol operands for `StoragePlace`, function refs, spill slots, shared constants, labels, special registers, etc.

Refactor final formatting so it is symbol-type driven:
- remove `functionNames.Contains(name)` logic
- remove the `FormatPlaceOperand` / `FormatSymbolOperand` split
- replace them with one symbol-reference formatter that pattern matches on symbol type plus addressing mode

This also means the writer no longer infers semantic identity from strings; it uses the actual symbol object.

## Pipeline Changes
### 1. MIR lowering and storage collection
Change global/static initializer collection to produce typed runtime values:
- `TryEvaluateStaticValue` returns `RuntimeBladeValue?`, not `object?`
- preserve current accepted initializer language semantics; do not widen them in this refactor
- any static initializer that is accepted must already be normalized to the target runtime type before entering ASM IR

Create module-level storage definitions during lowering or module-construction time rather than delaying them until the writer.

### 2. ASM lowerer / allocator / legalizer responsibilities
Keep code generation responsibilities separate from data-definition responsibilities:
- `AsmLowerer` builds code-only functions plus initial module data blocks
- `RegisterAllocator` adds spill-slot/shared-register definitions into the register block
- `AsmLegalizer` adds shared-constant definitions into the constant block
- no phase appends data definitions into any function node list

### 3. Layout planning and emission
Add a dedicated data-layout planning step before final assembly emission.

For each emitted data block:
- collect entries
- sort by **descending alignment**
- use deterministic secondary ordering by emitted symbol name to keep output stable across runs
- emit `ALIGNL` once at the start of a 4-byte alignment group
- emit `ALIGNW` once at the start of a 2-byte alignment group
- emit no alignment directive for 1-byte groups
- only emit `ALIGNL` / `ALIGNW` when transitioning between alignment groups, never between entries with the same alignment

Final assembly order becomes:
1. entrypoint function
2. all remaining functions in module order
3. register block
4. constant block
5. LUT block
6. external block metadata/bindings
7. hub block

External/fixed symbols belong to the external block in the module model, but they are **not** emitted as allocated storage:
- fixed-address aliases still emit aliases/constants as needed
- externals emit no DAT storage
- special-register aliases remain symbol bindings only

`AsmTextWriter` must also show module-level data blocks separately from functions so the IR view reflects the real model.

## Test Plan
Add or update tests for all of the following:
- functions never contain section/data nodes after lowering, allocation, or legalization
- module data blocks contain register/constant/LUT/external/hub definitions in the right block kinds
- final assembly emits code first and hub last
- `ALIGNL` / `ALIGNW` appear only at alignment-group transitions
- mixed hub/LUT/register storage is sorted by descending alignment, independent of source declaration order
- fixed aliases and externals remain symbolic and do not produce allocated DAT storage
- reserved special-register aliases still bypass CON alias emission correctly
- shared constants and spill slots appear in module data blocks, not function code
- writer formatting does not depend on string name membership tests
- `RuntimeTypeSymbol.IsLegalRuntimeObject` rejects/accepts the exact CLR payloads listed above
- `RuntimeBladeValue` normalization preserves existing integer truncation/sign-extension semantics
- comptime evaluation returns typed Blade values and uses explicit `VoidValue` / `UndefinedValue` singletons instead of `null`

Acceptance remains:
- targeted writer/type/value tests
- full regression suite
- `just accept-changes`

## Assumptions and Defaults
- This is an atomic refactor: no compatibility layer for `AsmSectionNode`/`AsmDataNode`, `TypeFacts`, or `object?` storage initializers.
- `StoragePlace` remains a symbol/identity object, but emitted storage is owned by module-level data definitions.
- The internal collection type for data definitions is unordered (`HashSet`/dictionary semantics); final output order is introduced only by the explicit layout planner.
- Deterministic emission order within each alignment band is by emitted symbol name.
- `string`, `range`, `module`, `function`, `void`, `unknown`, `integer literal`, and `undefined literal` are comptime-only types.
- The refactor preserves current language behavior; it changes representation and ownership, not what programs are accepted.
