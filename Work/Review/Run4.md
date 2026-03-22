# Blade/ code review findings (2026-03-22)

This review focuses on **correctness risks** and **unnecessary complexity** in `Blade/`.

## Scope reviewed

- `Blade/Syntax/Lexer.cs`
- `Blade/Syntax/Parser.cs`
- `Blade/Syntax/SyntaxFacts.cs`
- `Blade/Source/SourceText.cs`
- `Blade/CompilerDriver.cs`
- `Blade/CompilationOptionsCommandLine.cs`
- `Blade/Program.cs`
- `Blade/IR/IrPipeline.cs`
- `Blade/IR/Asm/AsmLowerer.cs`

---

## Findings

## 1) [High] Lexer can throw on validly-shaped `\u{...}` escapes with out-of-range scalar values

**Category:** Correctness / crash risk

### Evidence

- `ReadEscapeSequence` accepts 1–6 hex digits for `\u{...}` and returns the numeric value with no Unicode scalar-range validation.
- `ReadString` later passes values `> 0x7F` directly into `char.ConvertFromUtf32((int)codepoint)`.
- `char.ConvertFromUtf32` throws for values outside `[0x0000..0x10FFFF]` and surrogate range `0xD800..0xDFFF`.

### Why this matters

A malformed escape such as `"\u{110000}"` can produce an unhandled exception in lexing instead of a diagnostic, terminating compilation abruptly.

### Recommendation

Validate Unicode scalar ranges in `ReadEscapeSequence` (or before `ConvertFromUtf32`) and emit `E0006_InvalidEscapeSequence` when invalid.

---

## 2) [High] Variable declaration parser silently accepts duplicate optional clauses and overwrites earlier values

**Category:** Correctness / silent misparse

### Evidence

In `ParseVariableDeclaration`, optional clauses `@(addr)`, `align(n)`, and `= initializer` are parsed in a loop and assigned into single variables (`atClause`, `alignClause`, `equalsToken`, `initializer`) with no duplicate detection.

### Why this matters

Inputs like these are silently accepted, with “last one wins” behavior:

- `var x: u32 @(1) @(2);`
- `var x: u32 align(4) align(8);`
- `var x: u32 = 1 = 2;`

That makes parser behavior surprising and can mask user mistakes that should be diagnostics.

### Recommendation

Track whether each clause already occurred and report a dedicated parser diagnostic for duplicates.

---

## 3) [Medium] Flag annotation parsing is inconsistent between return specs and asm output bindings

**Category:** Correctness / grammar inconsistency

### Evidence

- Return-item flags use `SyntaxFacts.IsIdentifierLike(...)`.
- ASM output binding flags use strict `MatchToken(TokenKind.Identifier)`.

### Why this matters

A keyword-like flag token (for example `reg`) is accepted in return specs but rejected in asm output bindings, causing inconsistent syntax acceptance in two similar grammar constructs.

### Recommendation

Use a shared helper for flag-name parsing so both paths accept/reject the same token set.

---

## 4) [Medium] `IsIdentifierLike` is broader than its documented intent, potentially allowing unintended keywords in contextual-name positions

**Category:** Correctness / overly permissive parse surface

### Evidence

`IsIdentifierLike` currently returns true for **identifier + any keyword in the entire keyword table**.

### Why this matters

Contexts that should admit a restricted contextual-name subset (for example enum-member-like names) may inadvertently accept unrelated keywords (e.g., control-flow/type keywords), increasing ambiguity and reducing diagnostic quality.

### Recommendation

Narrow `IsIdentifierLike` to an explicit allowlist per context, or split into context-specific predicates (e.g., `IsEnumMemberNameToken`, `IsFlagNameToken`).

---

## 5) [Low] `hasEscapeErrors` in `ReadString` is dead state and adds noise

**Category:** Unnecessary complexity

### Evidence

`hasEscapeErrors` is set but never read for behavior (`_ = hasEscapeErrors;` only suppresses warning).

### Why this matters

It suggests missing behavior that no longer exists and increases cognitive load in a performance-critical lexer path.

### Recommendation

Remove `hasEscapeErrors` entirely, or wire it into actual behavior if intended.

---

## 6) [Low] Raw-asm body capture logic is duplicated in two parser methods

**Category:** Unnecessary complexity / maintainability risk

### Evidence

Both `ParseAsmFunctionDeclaration` and `ParseAsmBlockStatement` implement near-identical brace-depth raw-body capture loops.

### Why this matters

Any bug fix or syntax adjustment to one path can drift from the other.

### Recommendation

Extract a shared helper returning `(openBrace, bodyText, closeBrace)` or equivalent.

---

## Summary by severity

- **High:** 2
- **Medium:** 2
- **Low:** 2

The top priority is preventing lexer crashes (Finding #1) and rejecting duplicate declaration clauses (Finding #2), because both can directly affect compile correctness and developer trust.
