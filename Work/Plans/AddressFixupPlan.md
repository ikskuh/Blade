# Typed Address Refactor: Replace Raw Address Integers with `VirtualAddress`/`MemoryAddress`

## Summary
Complete the address-type refactor by making address space part of the type system end to end.

The implementation should eliminate the current “two unrelated `int` maps” model and move the backend to one typed address pipeline:

- single-space addresses use `CogAddress`, `LutAddress`, or `HubAddress`
- cross-space virtual references use `VirtualAddress`
- placed image-local symbols use `MemoryAddress`

The main invariant is: once a value represents an address, it must stop being a plain `int` unless it is being formatted for final ASM or used as an arithmetic offset/count.

## Public API and Type Changes
- Fully implement [Addresses.cs](/home/felix/projects/nerdgruppe/blade/Blade/Addresses.cs):
  - `CogAddress`, `LutAddress`, `HubAddress` each implement `IVirtualAddress`, `IComparable<T>`, arithmetic with `int` offsets, and `IFormattable`.
  - fix the generic math interfaces to the correct 3-parameter forms.
  - `VirtualAddress` implements:
    - `AddressSpace`
    - `GetJumpTarget()`
    - `GetDataAddress()`
    - `+ int` / `- int`
    - typed conversions from the concrete address primitives
    - comparison/equality based on `(AddressSpace, address)`
  - `MemoryAddress` implements `IVirtualAddress` by delegating virtual behavior to `Virtual`, and exposes:
    - `Physical : HubAddress`
    - `Virtual : VirtualAddress`
- Replace symbol-level raw address carriers:
  - `AbsoluteAddressSymbol` stores a `VirtualAddress`, not `(int address, AddressSpace storageClass)`.
  - `GlobalVariableSymbol.FixedAddress` becomes `VirtualAddress?`.
  - `GlobalVariableSymbol.SetLayoutMetadata` accepts `VirtualAddress?`.
- Replace solved-layout raw address carriers:
  - `LayoutSlot.Address` becomes `VirtualAddress`.
  - `LayoutSlot.EndAddressExclusive` becomes `VirtualAddress`, computed via typed offset arithmetic.
  - all layout diagnostics still format numeric addresses, but only at the diagnostic boundary.
- Replace placed-image raw address carriers:
  - `ImagePlacementEntry.HubStartAddressBytes` and `HubEndAddressExclusive` become `HubAddress`.
  - `ImagePlacement.ImageArenaEndAddressExclusive` becomes `HubAddress`.

## Implementation Changes
### 1. Layout solving and semantic address identity
- Refactor layout solving to operate on typed addresses internally:
  - COG candidate placement uses `CogAddress`
  - LUT candidate placement uses `LutAddress`
  - HUB candidate placement uses `HubAddress`
- Keep allocator/search math in `int` offsets where needed, but convert back to typed addresses immediately at the result boundary.
- Update fixed-address validation, overlap detection, and reserved-range checks to compare typed addresses within the same address space.
- Update pointer absolute-identity code in [BladeValue.Operators.cs](/home/felix/projects/nerdgruppe/blade/Blade/Semantics/BladeValue.Operators.cs) and related runtime helpers to return `VirtualAddress` instead of `(AddressSpace, int)` pairs.

### 2. Image planning, placement, and resource layout
- Replace the dual dictionaries in [CogResourceLayout.cs](/home/felix/projects/nerdgruppe/blade/Blade/IR/CogResourceLayout.cs):
  - from `_virtualAddressesBySymbol` + `_physicalHubAddressesBySymbol`
  - to `_addressBySymbol : IReadOnlyDictionary<IAsmSymbol, MemoryAddress>`
- Delete `TryGetVirtualAddress` and `TryGetPhysicalHubAddressBytes`.
- Replace them with:
  - `TryGetAddress(IAsmSymbol, out MemoryAddress)`
  - `TryGetVirtualAddress(IAsmSymbol, out VirtualAddress)` only if a virtual-only convenience is still genuinely needed
  - `TryGetImageStartAddress(ImageDescriptor, out HubAddress)`
- Make `CogResourceLayout.AvailableRegisterAddresses` typed as `IReadOnlyList<CogAddress>`.
- Make all internal register-space occupancy structures use `CogAddress`, including dedicated slot reservation and spill placement.
- Refactor `TryGetFixedAddress` and all planner code paths so fixed or solved COG addresses are typed, not `int`.

### 3. ASM model, lowering, allocation, and final emission
- Thread typed addresses through ASM-side symbol and allocation structures:
  - any symbol field that is “this is a COG slot” becomes `CogAddress`
  - any symbol field that is “this is a virtual address in some space” becomes `VirtualAddress`
  - any symbol field that is “this symbol is placed in an image” becomes `MemoryAddress`
- Specifically update:
  - `AsmSpillSlotSymbol.Slot` to `CogAddress`
  - allocator location records and register-packing state to use `CogAddress`
  - stable place resolution in `RegisterAllocator` to consume typed addresses
- Refactor [FinalAssemblyWriter.cs](/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/FinalAssemblyWriter.cs) to accept typed addresses everywhere except formatting:
  - COG/LUT/HUB origin emission takes typed addresses
  - LUT constant emission reads `VirtualAddress`
  - image start references use `HubAddress`
  - physical/virtual resolution helpers return typed values, not `int`
- Preserve the explicit exception you requested:
  - raw `int` formatting remains inside final ASM formatting helpers
  - address-to-string conversion for diagnostics and dumps may extract the integer only at the last moment

### 4. Diagnostics, dumps, and memory-map projection
- Fix all remaining namespace fallout from the `AddressSpace` move, starting with [DiagnosticBag.cs](/home/felix/projects/nerdgruppe/blade/Blade/Diagnostics/DiagnosticBag.cs).
- Update memory-map model and dump writers so:
  - COG rows use `CogAddress`
  - LUT rows use `LutAddress`
  - HUB rows use `HubAddress`
  - row indices may still be loop counters, but stored/transported addresses are typed
- Keep file-format output unchanged unless a formatting bug is discovered.

## Test Plan
- Add focused address-type unit tests covering:
  - construction bounds for `CogAddress`, `LutAddress`, `HubAddress`, `VirtualAddress`, `MemoryAddress`
  - typed arithmetic overflow/underflow rejection
  - `GetJumpTarget()` semantics:
    - COG direct
    - LUT `+ $200`
    - HUB valid only at `>= $400`
  - `MemoryAddress` hub/virtual consistency invariant for hub-space virtual addresses
  - equality/comparison semantics for same-space and cross-space `VirtualAddress`
  - formatting output for each address type
- Update existing layout/resource tests to assert typed addresses rather than raw integers.
- Add/adjust regression coverage for:
  - COG/LUT/HUB layout placement still emitting the same final ASM
  - multi-image physical placement still using correct image base addresses
  - LUT virtual constants still emitted correctly
  - hardware/storage regressions touched by address resolution still passing
- Verification target after implementation:
  - `dotnet build blade.slnx --no-restore`
  - targeted `Blade.Tests` for address/layout/writer/resource allocation
  - full `just regressions`
  - `just accept-changes` is still the end gate even though the workspace is known to have unrelated pressure; the implementation should still be driven to that gate

## Assumptions and Defaults
- `MemoryAddress` always means “physical hub placement + virtual address”. There is no separate physical COG/LUT address type.
- `VirtualAddress` remains the common abstraction for cross-space references; `IVirtualAddress` is used only where APIs truly need polymorphism.
- Raw `int` addresses remain legal only for:
  - loop indices
  - sizes/counts/offsets
  - final formatting and serializer boundaries
- This is a full refactor, not a compatibility pass:
  - remove obsolete `int`-based address APIs
  - do not keep parallel typed and untyped paths alive
  - update tests to the new typed model rather than preserving old signatures
