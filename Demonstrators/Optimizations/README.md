# Optimization Demonstrators

This folder contains one demonstrator per optimization option.

- `mir-*` files demonstrate MIR options.
- `lir-*` files demonstrate LIR options.
- `asmir-*` files demonstrate ASMIR options (via inline assembly snippets).

Each file includes a `NOTE` section describing:

- the target optimization,
- a positive trigger pattern,
- and a negative comparison mode (disable the optimization flag).
