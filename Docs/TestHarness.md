# Test Harness

Blade has two test layers:

- `dotnet test` for unit and component tests in `Blade.Tests`
- `just regressions` for the corpus-driven regression harness in `Blade.Regressions`

The regression harness is the main compiler-quality gate. It runs over source fixtures, checks diagnostics, inspects generated intermediate/final code, and validates hand-written assembly with FlexSpin.

## Fixture discovery

The runner discovers fixtures from:

- `Examples/**/*.blade`
- `Demonstrators/**/*.blade`
- `RegressionTests/**/*.blade`
- `RegressionTests/**/*.spin2`
- `RegressionTests/**/*.pasm2`

`Examples/` is a pristine sample corpus. Do not add test expectation comments there. Every example must compile cleanly with zero diagnostics.

`Demonstrators/` and `RegressionTests/` may contain explicit regression expectations.

Any `.blade` file without a harness header is treated as:

```blade
// EXPECT: pass
```

with an implied requirement of zero diagnostics.

## Header format

The harness only looks at the leading contiguous comment block. Parsing stops at the first non-comment, non-blank line.

Comment prefixes:

- `.blade`: `//`
- `.spin2` and `.pasm2`: `'` or `;`

The supported directives are intentionally simple.

### `EXPECT`

```blade
// EXPECT: pass
// EXPECT: fail
// EXPECT: xfail
```

- `pass` means the fixture must satisfy all assertions and, unless `DIAGNOSTICS` says otherwise, emit zero diagnostics.
- `fail` means the fixture must fail in the way the header describes.
- `xfail` means the current failure shape is intentional. If the fixture unexpectedly starts passing, the suite fails so the expectation can be revisited.

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

`STAGE` is only valid for `.blade` fixtures. `.spin2` and `.pasm2` fixtures always match against their raw code body.

### `CONTAINS`

Every listed snippet must appear after comment and whitespace normalization.

```blade
// EXPECT: pass
// STAGE: final-asm
// CONTAINS:
// - OR DIRA, #16
// - ANDN OUTA, #16
```


### `NOT_CONTAINS`

Like `CONTAINS`, but asserts a snippet is absent from normalized output.

```blade
// EXPECT: pass
// STAGE: asmir
// NOT_CONTAINS:
// - NOP
```

### `SEQUENCE`

The listed snippets must appear in order after normalization.

```blade
// EXPECT: pass
// STAGE: final-asm
// SEQUENCE:
// - SETQ _r3
// - XINIT _r4, #0
// - WAITX _r3
// - NOP
// - NOP
```

### `EXACT`

The fully normalized code must match exactly.

```spin2
' EXPECT: pass
' FLEXSPIN: required
' EXACT:
'   OR OUTA, #16
'   RET
OR OUTA, #16
RET
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

Supported options are compiler optimization toggles and `--no-single-callsite-inline`.

### `FLEXSPIN`

```blade
// FLEXSPIN: required
// FLEXSPIN: forbidden
```

- `.spin2` and `.pasm2` default to `required`
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

## Hand-written assembly

Use `.pasm2` for raw PASM2 fragments and `.spin2` for already-wrapped sources.

- `.pasm2` fixtures are wrapped in a minimal `DAT / org 0` container before FlexSpin runs
- `.spin2` fixtures are passed through unchanged

Example `.spin2` fixture:

```spin2
' EXPECT: pass
' FLEXSPIN: required
' CONTAINS:
' - OR OUTA, #16
' - RET
DAT
    org 0
entry
    OR OUTA, #16
    RET
```

## Commands

- `just regressions` runs the full regression corpus
- `just coverage` runs `dotnet test --collect:"XPlat Code Coverage"`

The NUnit suite contains a thin wrapper that invokes the regression runner in-process, so the regression harness contributes to the existing coverage data without a second coverage pipeline.


## FlexSpin availability

The runner probes FlexSpin once at startup using `flexspin --version`.
If FlexSpin is unavailable, fixtures that require FlexSpin validation are reported as `SKIPPED`.
