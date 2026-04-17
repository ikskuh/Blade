# Blade MCP Server

The Blade MCP server exists to give agents stable, structured access to common compiler
and regression operations.

## Preference Rule

If the Blade MCP server is available, prefer it over manual shell commands whenever it
already exposes the needed operation.

Prefer MCP for:

- compiler builds and diagnostics
- single-file compiler execution and dump retrieval
- regression-fixture execution
- regression-suite execution and failure inspection
- instruction metadata lookup
- hardware-availability checks

Manual shell invocation is the fallback only when the MCP server does not expose the
required capability.

## Tool Overview

- `build_compiler`
  Runs `dotnet build --no-restore` for the compiler project and returns structured
  warning/error diagnostics.

- `compile_file`
  Compiles one Blade source file and returns structured diagnostics, optional dumps,
  optional metrics, and optional final assembly.

- `is_hw_testrunner_available`
  Checks whether `.blade_mcp.json` configures `hardware.serial_port` and whether that
  device path currently exists.

- `execute_regression_fixture`
  Runs one regression fixture, optionally with hardware execution enabled.

- `execute_regression_tests`
  Runs the regression harness and returns a structured summary, including failed paths.

- `get_regression_result`
  Returns the cached result for one path from the latest MCP-triggered regression run.

- `get_regression_artifact`
  Returns named artifact contents from the latest cached regression run.

- `get_instruction_forms`
  Returns the known canonical forms for a mnemonic.

- `get_instruction_form`
  Returns structured metadata for one canonical instruction form.

- `get_fuzzy_instruction`
  Searches mnemonics, forms, and descriptions for relevant instruction matches.

## Hardware Config

The server reads repo-root `.blade_mcp.json`:

```json
{
  "hardware": {
    "serial_port": "/dev/serial/by-id/..."
  }
}
```

Hardware execution is considered available only when the configured device path exists.

Hardware execution uses the regression runner CLI under the hood, so the runner's loader
environment variables are honored. Set `BLADE_TEST_LOADER=loadp2|turboprop|auto` or
`BLADE_TEST_TURBOPROP_NO_VERSION_CHECK=true` in the MCP server environment to control
the hardware loader without changing `.blade_mcp.json`.
