# Blade

VS Code support for the Blade programming language.

## Features

- Syntax highlighting for `.blade` files.
- `Blade: Open Preview To The Side` command for compiling the active Blade document and showing diagnostics, assembly, IR dumps, and metrics in a side preview.
- Live preview refresh as the source document changes.

## Requirements

- The Blade compiler must be installed on the host machine.
- When `blade.path` is not configured, the extension runs `blade` from `PATH`.

## Extension Settings

This extension contributes the following setting:

- `blade.path`: Path to the Blade compiler executable. Leave unset to use `blade` from `PATH`. Supports `${workspaceFolder}`, `${file}`, `${cwd}`, and `${env:NAME}` expansion.

## Preview

The preview command compiles the active document by invoking:

```text
<blade.path or blade> --json --dump-all -
```

The document source is piped to stdin. The preview renders the JSON report as ordered `<details>` sections for diagnostics, final assembly, compiler dumps, and metrics. Diagnostics include clickable links that jump to the reported source location in VS Code.
