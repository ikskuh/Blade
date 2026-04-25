Plan how to implement the new concept of an "Image".

- Each compiled program consists of one or more images.
- `task main` is compiled into the *entry* Image.
- Each task reachable through `spawn` or `spawnpair` from the `task main` and all transitive calls is also compiled into an Image.
- An Image is composed of:
  - The execution mode (cog, lut, hub)
  - All referenced variables and their storage locations
  - All referenced functions and their storage locations
  - An Image is the thing that is passed into the `COGINIT` instruction
    - An Image for a `cog task` is always 496 longs large (= 1984 byte)
    - An Image for a `lut task` is up to 512 longs large (≤ 2048 byte)
    - An Image for a `hub task` is also always 496 longs large (= 1984 byte)
  - Each Image has a start address in memory
  - The *entry* Image is located in the Hub at address 0x0000 (start of the file)
- An Image contains:
  - always: the `cog var` declarations
  - always: the temporary registers
  - always: function parameters and return slots
  - always: all referenced `cog fn` functions
  - optional: the entry point function
  - optional: all automatic `fn` the compiler decided are worth putting into registers

This is the point where the `layout` constructs get semantic:
Each `layout` declaration gives the following guarantees:

- All variables inside a layout fulfil their requirements (size, alignment, optional position)
- No matter in what Image a `layout` is used, the variables keep their address stable
  - This is what gives us lut sharing capabilities.
  - This allows us compiling code once in a `hub fn` and still use it with `cog var`.

These guarantees require some kind of constraint solver, but as we're only caring about a limit of at max 512 variables that have complex constraints, this should still be doable. The compiler must reject programs which cannot have a fulfillable `layout` definition.

This means that we have to introduce a split in the code generation:
Parser and Binder run exactly once, while at least the ASMIR stage will run once per `task`.

For this planning session, we can focus on `cog task` and `hub task` first, and ignore `lut task` (because it's a huge can of worms that needs figuring out).

Plan out in great detail how to refactor the compiler such that we can implement the Image and `layout` concepts. These are crucial to the Blade language.