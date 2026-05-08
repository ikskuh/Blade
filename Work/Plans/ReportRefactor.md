# Report-Driven Compiler Output Using `CompilationOutput`

## Summary
Replace the current dump/output stack with a single richer compiler result type named `CompilationOutput`, and make report writers consume that type directly.

`CompilationOutput` becomes the sole source of truth for:

- compilation status
- diagnostics
- metrics
- partial pipeline products
- final assembly when available
- crash information for unexpected compiler failures

This removes the duplicate `CompilationReport` idea entirely. Reporting becomes a projection over real compiler state, not a second model.

The design should explicitly support a richer debugging workflow:

- inspect whichever stages were reached before a failure
- preserve compiler state for future richer tooling
- avoid stringly snapshots as the primary model
- keep the door open for later backtracking/re-exploration over actual stage objects rather than textual dumps

## Core Type Changes
Replace `CompilationResult` with `CompilationOutput` in [Blade/CompilerDriver.cs](/home/felix/projects/nerdgruppe/blade/Blade/CompilerDriver.cs:29).

`CompilationOutput` should contain:

- source
- syntax
- diagnostics
- metrics
- overall status:
  - success
  - failed
  - crashed
- crash payload for unexpected exceptions:
  - exception type
  - message
  - stack trace or equivalent debug text
- partial stage products, each independently optional:
  - bound program
  - image plan
  - image placement
  - layout solution
  - cog resource layouts
  - preopt MIR modules
  - optimized MIR modules
  - preopt LIR modules
  - optimized LIR modules
  - preopt ASMIR modules
  - optimized ASMIR modules
  - final assembly text

Do not keep the current “all later IR state is hidden behind `IrBuildResult?`” shape. That prevents partial reporting and blocks future debugging use cases.

Flatten `IrBuildResult` into either:

- fields owned directly by `CompilationOutput`, or
- a dedicated partial pipeline snapshot type owned by `CompilationOutput`

Recommended default:
keep a dedicated typed stage container inside `CompilationOutput`, but make it partial-capable and not all-or-nothing. That keeps the output object organized without reintroducing a second report model.

## Reporting Architecture
Keep `IReportWriter`, but make it consume `CompilationOutput` directly.

- `IReportWriter.Write(TextWriter writer, CompilationOutput output)`

Report writers inspect available stage products and render only what exists.

The event-based text emission layer remains important, but it should be an on-demand rendering API, not stored report data.

- rewrite the current dump writers into event emitters that target `ITextReportWriter`
- emit from actual compiler objects inside `CompilationOutput`
- do not pre-store tokenized MIR/LIR/ASM sections
- do not create a parallel “report section” object graph containing the same information

This keeps reporting lazy and non-duplicative while still enabling HTML identity highlighting and richer structured JSON.

## Stage Emission Model
Convert all current human-readable dump producers into structured emitters over real compiler objects:

- bound tree
- image plan
- layout solution
- MIR
- LIR
- ASMIR
- image memory maps
- final assembly

`ITextReportWriter` should become the shared low-level sink for text-like outputs.

It should support:

- basic text spans by kind
- semantic/context spans with opaque object identity
- explicit newlines
- indentation or begin/end block structure
- optional fold-region boundaries for HTML

Identity rules:

- the same underlying object reference must map to the same emitted identity within one render
- HTML highlighting must be based on generated stable render-local ids, never names
- JSON must serialize those generated ids, not CLR references

This gives the richer debugging UX you want while preserving typed state for future backtracking-oriented work.

## Compiler Flow Changes
Refactor `CompilerDriver` so it records progress incrementally into `CompilationOutput`.

Instead of constructing one final `IrBuildResult` only at the end, each major stage writes its product into the output as soon as it exists.

Expected flow:

1. create empty `CompilationOutput`
2. load source and syntax
3. bind and store bound program if successful
4. build image/layout/MIR/LIR/ASM stages, storing each completed product immediately
5. emit final assembly if reached
6. finalize diagnostics and metrics
7. if an unexpected exception occurs, mark status as crashed and preserve all already-captured stage products

This is the key change needed for partial reports and future debugging/backtracking support.

## CLI And Output Behavior
Replace the current output flags with repeatable `--report <format>,<path>`.

- supported formats: `text`, `html`, `json`
- `-` means stdout
- `--report` is repeatable

Remove and reject:

- `--dump-*`
- `--dump-all`
- `--dump-dir`
- `--output`
- `--json`
- `--metrics`

Default behavior with no `--report`:

- still emit bare final assembly only
- produce it through the report pipeline using `CompilationOutput`
- render only the final assembly body, no report chrome

When `--report` is present:

- build one `CompilationOutput`
- pass it to each requested writer
- detailed reports include diagnostics, metrics, and every available stage
- unavailable later stages are simply omitted because they were never reached

## Writer Responsibilities
`TextReportWriter`
- detailed mode renders diagnostics, metrics, and all available sections
- bare mode renders only final assembly
- preserve current plaintext style where practical

`HtmlReportWriter`
- use [Work/Report.html](/home/felix/projects/nerdgruppe/blade/Work/Report.html:1) as the structural baseline
- render available stages as tabs/panels
- include same-object highlighting via emitted identity ids
- keep assets self-contained in the output file

`JsonReportWriter`
- emit a new schema based on real compiler state availability
- include diagnostics, metrics, status, crash info, and available stages
- emit structured text/token content only where it is actually serialized for reporting
- do not preserve the old `--json` envelope shape

## Test Plan
Update tests to target the new `CompilationOutput`-driven behavior.

- `CompilationOutput` construction:
  - success case captures all reached stages
  - diagnostic failure preserves earlier stages when available
  - crash case preserves reached stages and crash metadata
- CLI parsing:
  - accepts repeatable `--report <format>,<path>`
  - rejects removed legacy flags with migration guidance
  - rejects malformed report targets
- default invocation:
  - no `--report` prints only final assembly
- text reports:
  - stdout and file output
  - success includes diagnostics/metrics/stages as appropriate
  - failure/crash include partial state actually reached
- HTML reports:
  - contain expected stage panels
  - same-object spans share the same identity id
- JSON reports:
  - include status, diagnostics, metrics, crash metadata, and available stage payloads
  - omit unreached stages
- stage emitters:
  - preserve current textual output shape for representative MIR/LIR/ASM/final-asm cases
- end-to-end:
  - multiple `--report` targets in one invocation
  - target-specific write failures return non-zero

## Assumptions And Defaults
- `CompilationOutput` replaces `CompilationResult` as the public compiler outcome type.
- The compiler should preserve real typed stage products, not textual snapshots, as the basis for reporting.
- Partial compilation state is a first-class requirement, not an error-path workaround.
- Report writers are projections over compiler state, not owners of duplicated data.
- Backtracking/debug-oriented future work should build on preserved typed stage outputs inside `CompilationOutput`, not on reconstructing state from reports.
