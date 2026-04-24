# Test Harness

Blade has two test layers:

- `dotnet test` for unit and component tests in `Blade.Tests`
- `just regressions` for the corpus-driven regression harness in `Blade.Regressions`

The regression harness is the main compiler-quality gate. It runs over source fixtures, checks diagnostics, inspects generated intermediate/final code, and validates generated assembly with FlexSpin.

## Fixture discovery

The runner discovers fixture pools from `regressions.cfg.json` in the repository root, or from a file passed via `--config`.

Each pool declares a relative `path` plus an `expect` mode:

- `accept` means the files are compiled without parsing in-file regression headers and must succeed with zero diagnostics
- `reject` means the files are compiled without parsing in-file regression headers and must produce at least one error diagnostic
- `encoded` means the files use the normal `EXPECT` header format described below

Only `*.blade` and `*.blade.crash` files are discovered. `.blade.crash` fixtures are only valid in `encoded` pools.

Any `.blade` file in an `encoded` pool without a harness header is treated as:

```blade
// EXPECT: pass
```

with an implied requirement of zero diagnostics.

## Header format

The harness only looks at the leading contiguous comment block. Parsing stops at the first non-comment, non-blank line.
If a fixture uses `EXPECT`, that marker must be the first line of the file.

Comment prefixes:

- `.blade`: `//`

The supported directives are intentionally simple.
Once an `EXPECT` header starts, every non-blank comment line must be either a supported directive or content inside a directive block such as `NOTE`.

### `EXPECT`

```blade
// EXPECT: pass
// EXPECT: fail
// EXPECT: xfail
// EXPECT: pass-hw
```

- `pass` means the fixture must satisfy all assertions and, unless `DIAGNOSTICS` says otherwise, emit zero diagnostics.
- `pass-hw` means the fixture must satisfy the normal `pass` checks and, when hardware is configured, also pass real hardware execution using the hardware test runtime and `RUNS`.
- `fail` means the fixture must fail in the way the header describes.
- `xfail` means the current failure shape is intentional. If the fixture unexpectedly starts passing, the suite fails so the expectation can be revisited.

### `RUNS`

`RUNS` is only valid for `.blade` fixtures with `EXPECT: pass-hw` and is required there.

```blade
// EXPECT: pass-hw
// RUNS:
// - [] = 1234
// - [ 0 ] = 1234
// - [ 0, -10, 0x12345 ] = 1234
```

Each run entry supplies a parameter list followed by the expected output for that run.

- `[]` means no runtime parameters
- up to 8 parameters are allowed
- values support unsigned decimal `0..4294967295`
- values support signed decimal `-2147483648..-1`
- values support unsigned hex via `0x...`

### `NOTE`

`NOTE` is for human explanation only and is ignored by the runner.

```blade
// NOTE:
//   human written note
//   indentation is irrelevant
```

### `DIAGNOSTICS`

Loose form: these codes must appear, but extra diagnostics are allowed.

```blade
// EXPECT: fail
// DIAGNOSTICS: E0218, E0205
hub var counter: u32 = 0;
```

Strict block form: the diagnostics must match exactly. Each entry must include a diagnostic code and may also include a line number and/or exact message.

```blade
// EXPECT: fail
// DIAGNOSTICS:
// - L5, E0202: Name 'missing' does not exist in the current scope.
fn load() -> u32 {
    return missing;
}
```

The strict form supports these entry shapes:

```text
- L30, W1234: This thing is potentially bad
- L32, E2345: This is not possible
- L10, W2222
- E9999: Over nine thousand!
```

### `STAGE`

`STAGE` selects which generated text to inspect for a `.blade` fixture.

```blade
// STAGE: bound
// STAGE: mir-preopt
// STAGE: mir
// STAGE: lir-preopt
// STAGE: lir
// STAGE: asmir-preopt
// STAGE: asmir
// STAGE: final-asm
```

`STAGE` is only valid for `.blade` fixtures.

### `CONTAINS`

Every listed snippet must appear after comment and whitespace normalization.

Items use a prefix to indicate the assertion kind:

- `- snippet` (dash) — the snippet must appear at least once (positive)
- `! snippet` (bang) — the snippet must never appear (negative)
- `Nx snippet` (count) — the snippet must appear exactly N times; `0x` is an alias for `!`

```blade
// EXPECT: pass
// STAGE: final-asm
// CONTAINS:
// - OR DIRA, #16
// - ANDN OUTA, #16
// ! NOP
// 2x WAITX
```

### `SEQUENCE`

The listed snippets must appear in order after normalization. The same item prefixes as `CONTAINS` apply:

- `- snippet` — find this snippet at or after the current position
- `! snippet` — this snippet must not appear between the previous and next positive match
- `Nx snippet` — this snippet must appear exactly N consecutive times at the current position

```blade
// EXPECT: pass
// STAGE: final-asm
// SEQUENCE:
// - SETQ _r3
// - XINIT _r4, #0
// ! MOV
// - WAITX _r3
// 2x NOP
```

### Wildcards

Wildcards are only active for assembly stages (`asmir-preopt`, `asmir`, `final-asm`). In IR stages (`bound`, `mir`, `lir` variants), `?` is treated as a literal because it is part of the ternary operator syntax.

Use `?` in any snippet to match a single token (identifier, register name, etc.):

```blade
// CONTAINS:
// - MOV ?, #0
```

Use `?N` (e.g. `?1`, `?2`) for numbered wildcards that bind on first use and must match the same value everywhere within the same block:

```blade
// SEQUENCE:
// - MOV ?1, #0
// - ADD ?, ?1
```

This matches `MOV PA, #0` followed by `ADD <anything>, PA` — `?1` captured `PA` on the first occurrence and requires the same value on the second.

### `EXACT`

The fully normalized code must match exactly.

```blade
// EXPECT: pass
// STAGE: final-asm
// EXACT:
//   OR OUTA, #16
//   RET
cog task main {
}
```


### `ARGS`

`ARGS` passes per-fixture compiler CLI options to the Blade compiler invocation (`.blade` fixtures only).

Single-line form:

```
// ARGS: -fno-asmir-opt=* -fasmir-opt=elide-nops
```

Block form:

```
// ARGS:
// - -fno-asmir-opt=*
// - -fasmir-opt=elide-nops
```

Supported options are compiler optimization toggles, `-fno-mir-opt=single-callsite-inline`, module mappings via `--module=<name>=<path>`, and runtime templates via `--runtime=<path>` (resolved relative to the fixture file).

For `EXPECT: pass-hw` and `EXPECT: xfail-hw`, the regression harness injects the configured `hardwareRuntimePath` from `regressions.cfg.json` unless the fixture already specifies an explicit `--runtime=...` in `ARGS`.

### `FLEXSPIN`

```blade
// FLEXSPIN: required
// FLEXSPIN: forbidden
```

- `.blade` defaults to automatic validation on passing fixtures that reach final assembly

## Normalization rules

Code comparisons ignore:

- line comments
- whitespace

They do not ignore or rewrite:

- identifiers
- instruction spellings
- numeric literals
- token order

This keeps the harness strict enough to catch real codegen changes while still ignoring formatting and explanatory comments.

## Commands

- `just regressions` runs the full regression corpus
- `just regressions -- --hw-port <port>` runs the full regression corpus and enables real hardware execution for `EXPECT: pass-hw` fixtures
- `just regressions -- --hw-loader turboprop --hw-port <port>` forces the hardware runner to use `turboprop`
- `just regressions -- --hw-turboprop-no-version-check --hw-port <port>` passes `--no-version-check` when `turboprop` is selected
- `just coverage` runs `dotnet test --collect:"XPlat Code Coverage"`

Hardware port resolution order for `EXPECT: pass-hw` is:

- `--hw-port <port>`
- `BLADE_TEST_PORT`
- no hardware execution; the fixture still runs as a normal compile/FlexSpin regression using the hardware runtime

Hardware loader resolution order is:

- `--hw-loader auto|loadp2|turboprop`
- `BLADE_TEST_LOADER=auto|loadp2|turboprop`
- `auto`

`auto` selects `turboprop` when it is available on `PATH`, otherwise it falls back to `loadp2`.
The `loadp2` path writes the patched binary to a temporary file and runs `loadp2 -p <port> -t -q <file>`.
The `turboprop` path pads the patched binary to a 4-byte boundary, streams it through stdin, and runs `turboprop --port=<port> --monitor --monitor-format=raw -`.

The `turboprop` version check is enabled by default. Disable it for faster runs with:

- `--hw-turboprop-no-version-check`
- `BLADE_TEST_TURBOPROP_NO_VERSION_CHECK=true`

Use `--hw-turboprop-version-check` to force the version check back on when the environment variable is set.

The NUnit suite contains a thin wrapper that invokes the regression runner in-process, so the regression harness contributes to the existing coverage data without a second coverage pipeline.


## FlexSpin availability

The runner probes FlexSpin once at startup using `flexspin --version`.
If FlexSpin is unavailable, fixtures that require FlexSpin validation are reported as `SKIPPED`.
