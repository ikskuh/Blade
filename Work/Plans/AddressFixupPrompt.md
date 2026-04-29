

What i really don't like and needs to change is this:
```cs
    private readonly IReadOnlyDictionary<IAsmSymbol, int> _stableAddressesBySymbol;
    private readonly IReadOnlyDictionary<IAsmSymbol, int> _virtualAddressesBySymbol;
```

This is the wrong abstraction, as it makes the sets potentially disjunct.

Each symbol requires a potential memory address per image. Thus, we should introduce new typesy that bring us the required type safety and clarity:

- `HubAddress`, `CogAddress`, `LutAddress` primitives that encode the address space in the type.
  - Use these whereever only a single address space is legal anyways
- `VirtualAddress` is a primitive that fuses an `AddressSpace` with an integer address
  - Use this whereever a virtual hub/cog/lut address can be placed
- `MemoryAddress` is a primitive that fuses a hub placement with a virtual address.
  - This is what we'll use instead of the two dictionaries.
- `IVirtualAddress` is an interface shared by all types above that can be used to abstract over the concrete requirements, if only a virtual memory address is used.

This change now gives us the following improvement over `CogResourceLayoutSet`:

```diff
-    private readonly IReadOnlyDictionary<IAsmSymbol, int> _stableAddressesBySymbol;
-    private readonly IReadOnlyDictionary<IAsmSymbol, int> _virtualAddressesBySymbol;
+    private readonly IReadOnlyDictionary<IAsmSymbol, MemoryAddress> _addressBySymbol;
```

This change means:

- A layout solution then stores a mapping from `GlobalVariableSymbol` to `VirtualAddress`, as the layout solution cannot know the later placement inside hub ram.
- `CogResourceLayoutSet` can use `MemoryAddress` for placement data.
  - Delete and refactor `TryGetVirtualAddress`, `TryGetPhysicalHubAddressBytes`.
  

To prepare for this task, i've renamed `VariableStorageClass` into `AddressSpace` and moved it from `namespace Blade.Semantics` into `namespace Blade`. This reflects the broader reality of this type.

The implementation for these types is already partially available in Blade:

- `Blade/Addresses.cs` contains the new address types in a partial implementation

Your task is now is:

- Fully implement the interfaces in `Addresses.cs` and add unit tests for the types.
- Thoroughly thread the new datatypes through codebase and replace all integer + convention based place through the correct data types.
- Identify all places where a virtual and physical address are transported separately and use `MemoryAddress` for this.
- Do not leave any traces of `int` behind except for the use in the FinalAsmWriter, which requires correct int formatting and in places where you have to compute offsets.
  - Switch as early as possible back to the address types.

Do not leave any traces of unnecessary `int address` and alike in the codebase.