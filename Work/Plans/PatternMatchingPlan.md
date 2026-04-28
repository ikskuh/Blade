# Refactor Regression Assertion Pattern Matching

**Summary**
- Replace flat `CONTAINS`/`SEQUENCE` assertion lists with independent assertion blocks.
- Redefine `EXACT:` as a prefixed block matcher, not whole-text equality.
- Replace whitespace-stripping regex matching with compiled token-safe pattern matching.
- Enable `?` wildcards in all assertion stages; update MIR/LIR branch dump text so literal `?` is no longer emitted there.

**Key Changes**
- Update the regression expectation model in `RegressionRunner.cs`:
  - Store `CONTAINS`, `SEQUENCE`, and `EXACT` as `IReadOnlyList<SnippetBlock>`.
  - Each directive occurrence creates a new block with its own wildcard binding namespace.
  - `EXACT:` uses the same `SnippetItem` entries as `SEQUENCE`; old free-text exact matching is removed.
  - Reject malformed `SEQUENCE`/`EXACT` blocks that contain only `!` items.
  - Reject `0x snippet`; counts must be `>= 1`, and negative assertions must use `! snippet`.

- Implement a compiled matcher:
  - Strip comments before normalization: ASMIR/final assembly strip only `'`; bound/MIR/LIR strip `;`.
  - Normalize text by inserting spaces at word boundaries, collapsing whitespace to one space, and trimming only outer source text.
  - Compile each snippet into a `Pattern` containing `N + 1` literal sequences and `N` wildcard slots.
  - Match without regex backtracking: scan literal sequence, consume `\w+` for wildcards, bind numbered wildcards on first use, and require later uses to match exactly.
  - Bare `?` consumes one `\w+` token without binding.

- Assertion semantics:
  - `CONTAINS` block:
    - `-` requires at least one match.
    - `!` requires no match.
    - `Nx` requires exactly `N` non-overlapping matches.
  - `SEQUENCE` block:
    - `-` advances to the next matching item.
    - `Nx` advances through `N` matching repetitions.
    - `!` is checked only in the gap before the next non-negative item, or in the prefix/suffix gap if first/last.
    - Interleaved unrelated text remains allowed.
  - `EXACT` block:
    - Same as `SEQUENCE`, but unmatched text is forbidden between positive/count matches.
    - Unmatched text before the first match and after the last match is allowed, per selected behavior.
    - `!` checks still apply to the relevant prefix/inter-match/suffix gap.

- Update MIR/LIR branch dump formatting:
  - Change branch text from `branch %r0 ? bb2() : bb3()` style to named fields, e.g. `branch cond=%r0, true=bb2(), false=bb3()`.
  - Apply the same shape to MIR and LIR writers.
  - Update affected regression fixtures that currently assert literal MIR/LIR `?`.

- Update documentation:
  - Revise `Docs/TestHarness.md` for independent blocks, all-stage wildcards, `EXACT:` block syntax, count validation, and new comment stripping behavior.
  - Remove the documented `0x` alias.

**Test Plan**
- Add focused `Blade.Tests` coverage for:
  - Multiple `CONTAINS` blocks with independent numbered wildcard bindings.
  - Multiple `SEQUENCE` blocks with independent numbered wildcard bindings.
  - `EXACT` rejecting interleaved unmatched text but allowing prefix/suffix text.
  - Leading, trailing, and only-negative `SEQUENCE`/`EXACT` cases.
  - `0x` rejection and `Nx` acceptance for `N >= 1`.
  - Identifier-fusion regression: `ANDN ?1, #10` must not bind in a way that lets `AND ?1, #20` match `AND NFOO`.
  - Wildcards in MIR/LIR after branch dump formatting no longer uses literal `?`.

- Update existing regression fixtures affected by MIR/LIR branch dump text.
- Run `just accept-changes`.
- Run coverage and use `python Scripts/coverage_range.py ...` for any newly uncovered matcher/parser lines until new code reaches 100% line coverage.

**Assumptions**
- `EXACT:` no longer supports old free-text body syntax; entries must use `-`, `!`, or `Nx`.
- `?` is always a wildcard in code assertion snippets after this change.
- No escape syntax for literal `?` is added; current MIR/LIR literal use is removed by changing dump formatting.
- No Zig command is needed for this work.
