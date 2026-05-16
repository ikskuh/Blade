# Blade ABI

## Primitive Layout

### `bool`, `u1` and `i1`

Single-bit types are special in that the Propeller 2 can trivially transport them through the CPU flags instead of passing them as registers.

CPU flags are more powerful than in other systems, and the Propeller 2 can even perform certain operations on them through `MODCZ`.

When stored in memory or embedded in a compound, the following strategies are used:

- hub memory: A `bool` occupies 8 bits of memory, and stores either 0 or 1. Alignment is 1.
  - A pointer to a `*lut bool` is a value that encodes the HUB address.
- lut memory: A `bool` occupies 32 bits of memory, and stores either 0 or 1. Alignment is 1.
  - A pointer to a `*lut bool` is a value that encodes the LUT address.
- cog memory: A `bool` occupies 1 bit of memory, and stores either 0 or 1. As registers are 32 bit wide, the compiler can stash up to 32 bools into a single register.
  - This can be efficiently lowered through the use of `BITx`, `TESTB` and `TESTBN` instructions.
  - A pointer to a `*cog bool` must encode both the storage register, as well as the bit offset, and is encoded as a `bitfield(u32) { bit: u5, reg: u9, zero: u18 }`

NOTE: `bool` is used as a placeholder to describe the behavior of all other single-bit types as wel.

### Integer Types

All integers except for `u1` and `i1` are implemented as the CPUs native integer types.

- For `cog` and `lut` memory, all integers are 32 bit wide and occupy a single memory cell (size=1, alignment=1).
- For `hub` memory, the integers are divided into three groups:
  - `u2`…`u8` and `i2`…`i8` have size=1, align=1 and use `RDBYTE` and `WRBYTE`.
  - `u9`…`u16` and `i9`…`i16` have size=2, align=2 and use `RDWORD` and `WRWORD`.
  - `u17`…`u32` and `i17`…`i32` have size=4, align=4 and use `RDLONG` and `WRLONG`.

When loading and storing integers, a compiler must not perform zero or sign extension, but the stored values are trusted to have their unused bits correctly zero- or sign extended.

## Enumeration Types

Enumeration types behave like their underlying integer type, and cannot be distinguished in the backend.

## Struct Types

Throught the requirement of storing structs in registers and LUT (32 bit oriented), but also in hub memory (8 bit oriented), the Blade ABI for structures is slightly more complex than on other systems:

First and foremost, it is checked if the structure would have fields adding up to more than 8, 16 or 32 bits of memory. Depending on the case, a strategy is chosen:

### Identity structure type

A structure with exactly a single field shall be treated as the underlying type. Thus,

```blade
type Ident = struct { value: u16 };

var id: Ident = Ident { .value = 10 };
```

is equivalent to:

```blade
var id: u16 = 10;
```

when considering the ABI. Semantically, it's distinct, but it's lowered identically.

This rule is recursively applied, so a struct with a nested struct with a single field is still equivalent to just declaring this field.

### Small Struct ABI

The "Small Struct ABI" packs the struct into 16 bits of memory, allowing hub access through a single RDWORD or WRWORD.

These structs can either encode one or two fields, which are always stored at a byte boundary.

This means, a `struct { x: i8, y: i8 }` can be allocated with `x` at offset 0, and `y` at offset 8.

This ABI is applied iff:

- A struct has exactly two fields.
- Each field requires 8 bits or less storage.

### Standard Struct ABI

The "Standard Struct ABI" applies to all structures not covered by the above special rules.

Regular structs can be loaded with either a single `RDLONG` or `WRLONG` instruction, or can use `SETQ` to introduce a block copy operation for the fast transfer of more than a single 32 bit value. Partial loads and stores are allowed and should be utilized when possible.

Structs are always allocated in multiples of 32 bit, which means we get a struct with one or more so called "lanes".

Each line encodes 32 bits of data, internally aligned to 8 bits.

Struct fields are not required to be declaration ordered and shall be sorted by the compiler by decreasing alignment. This allows deterministic dense packing.

## Union Types

A union type has an alignment that is equivalent to the highest alignment of the fields. All fields are stored at offset 0, thus overlap.
