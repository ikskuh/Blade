# Optimization Demonstrators

This folder contains regression demonstrators for optimization options.

- `mir-*` files demonstrate MIR options.
- `lir-*` files demonstrate LIR options.
- `asmir-*` files demonstrate ASMIR options.
- Each optimization should have an `-enabled` and `-disabled` fixture pair.

Each fixture includes a `NOTE` section describing:

- the target optimization,
- a positive trigger pattern,
- and a negative comparison mode (disable the optimization flag).

Some optimizations are currently marked `EXPECT: xfail` in their enabled fixture.
Those cases are not source-testable yet through `.blade` fixtures even though the underlying optimizer has unit coverage.
