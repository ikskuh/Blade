# Code unreachable through the frontend

All previously listed items in this file were either removed from the codebase or turned into assertions/diagnostics.

New findings should be logged here as:

- location (`path:line` + symbol name)
- why it is believed unreachable (frontend path argument)
- what to do: delete, replace with `Assert.Unreachable*`, or add a frontend fixture to prove reachability
