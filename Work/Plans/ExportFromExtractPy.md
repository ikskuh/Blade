# Refactor `extract.py` to Export a Human-Editable JSON Metadata Model

## Summary
Refactor `Scripts/extract.py` so it exports Propeller 2 instruction metadata as a human-authored JSON file instead of emitting C# directly.

The JSON model should be:
- easy to read and edit by hand
- shaped around the instruction-set domain rather than downstream code layout
- easy to extend later with additive fields
- independent of any C# generator design, since code generation is out of scope for this task

## Data Model
Define one top-level JSON object with these sections:

- `conditionCodes`: object keyed by condition-code token
- `modczOperands`: object keyed by MODCZ operand token
- `specialRegisters`: object keyed by register name
- `flagEffects`: object keyed by flag-effect name
- `mnemonics`: object keyed by mnemonic name

Using objects at the top level makes identity explicit and prevents duplicate entries from being representable in the exported format.

### `conditionCodes`
Each property name is the condition-code token, including `"<INST>"` where applicable. Each value is:

- `isAlias`: bool
- `canonicalName`: nullable string; null for canonical entries

### `modczOperands`
Each property name is the MODCZ operand token. Each value is:

- `isAlias`
- `canonicalName`

### `specialRegisters`
Each property name is the register name. Each value is:

- `address`
- `description`

### `flagEffects`
Each property name is the flag-effect name. Each value is:

- `targetFlag`: `"c" | "z" | "both"`
- `operator`: `"set" | "and" | "or" | "xor"`

Bitmask mapping is derived later from the ordered list or consumer logic outside this task.

### `mnemonics`
Each property name is the mnemonic text. Each value is:

- `isAlias`
- `instructionForms`: array of instruction-form objects

Alias mnemonics remain first-class entries because the meaningful data is carried by their forms.

### `instructionForms` item
Store forms under each mnemonic:

- `summary`: optional human-readable description for this specific form
- `allowedFlagEffects`: array of flag-effect names
- `operands`: array of operand objects
- `writtenRegisters`: array of register names plus `"D"` where applicable
- `hwStackEffect`: `"None" | "Push" | "Pop"`
- `classification`: object with
  - `isCall`
  - `isJump`
  - `isBranch`
  - `isReturn`
  - `hasNoRegisterEffect`
  - `isPureRegisterLocal`

`operandCount` is implicit from `operands.length`. The mnemonic name is inherited from the parent object key.

### `operands` item
- `role`: `"D" | "S" | "N" | "MODCZ" | "ADDR" | "None"`
- `type`:
  - `"regular"`
  - `"branch target"`
  - `"modcz"`
  - `"bit"`
  - `"pin"`
  - `"bitrange"`
  - `"pinrange"`
  - `"hub ptrexpr"`
  - `"lut ptrexpr"`
- `bitWidth`
- `access`: `"None" | "Read" | "Write" | "ReadWrite"`
- `supportsImmediate`: `"no" | "optional" | "required"`
- `augPrefix`: `"None" | "AUGD" | "AUGS"`

Operand position is implicit from array order.

## Implementation Changes
Update `Scripts/extract.py` to:

- replace C# rendering with JSON serialization
- introduce explicit Python-side model types for the exported JSON contract
- aggregate all top-level identity-based collections as objects keyed by their domain name/token
- omit redundant `name` fields from keyed top-level objects
- reject duplicate keys during model construction rather than silently merging incompatible entries
- group instruction forms under mnemonic entries
- move descriptions from mnemonic-level metadata to per-form `summary`
- infer `isJump` separately from `isBranch`
  - `isJump`: unconditional control transfer
  - `isBranch`: conditional control transfer
- extend operand-role inference to support `MODCZ` and `ADDR`
- add operand-type inference for:
  - arithmetic/value operands
  - branch targets
  - MODCZ operands
  - bit and pin immediates
  - bit and pin ranges
  - hub and LUT pointer-expression operands
- replace boolean immediate support with `supportsImmediate`:
  - `no`: operand never uses immediate syntax
  - `optional`: operand may use immediate syntax
  - `required`: operand must use immediate syntax
- emit deterministic, pretty-printed UTF-8 JSON suitable for review and hand-editing
- write the JSON to a stable repo path used as the exported metadata artifact

Keep the extraction logic focused on domain facts from the workbook. Do not embed downstream representation details such as enum ordinals, fixed C# field slotting, or lookup-table layout.

## Verification
Let the human operator verify the data.

## Assumptions
- The JSON file is the exported metadata artifact and should be optimized for human maintenance.
- Top-level collections should be keyed objects wherever entries have a stable natural identity.
- Root-level metadata should contain only stable instruction-set data.
- Alias mnemonics are retained as first-class keyed entries because their form data may differ materially from canonical names.
- `MODCZ` and `ADDR` are real operand-role concepts worth modeling explicitly.
- Operand `type` and `supportsImmediate` are instruction-set properties, while consumer-specific interpretation is outside the scope of this task.
