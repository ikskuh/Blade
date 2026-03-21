# Task completion checklist

When finishing work in this repository, prefer this checklist:

1. Keep the build warning-free.
2. Run the smallest relevant verification first, then the broader one if the change warrants it.
3. For compiler behavior changes, prefer adding or updating a demonstrator/regression fixture instead of relying only on a unit test.
4. If parser or grammar syntax changed, update `Docs/reference.blade`, the VS Code grammar, and related tests.
5. If diagnostics changed, ensure diagnostic code / name / format string requirements are still satisfied.
6. If coverage-sensitive logic changed, run `just coverage` and inspect gaps; the project expects 100% branch coverage.
7. If a compiler bug or unexpected behavior was found during the task, log it in `Work/Bugs.md`.
8. If truly unreachable code was discovered, log it in `Work/Unreachable.md`.
9. Use `just test` as the normal validation baseline; use `just regressions` or `just all` for broader compiler-impacting work.

Recommended command choices by scope:
- Small local code change: `just test`
- Compiler pipeline or semantic/codegen change: `just test` and `just regressions`
- Broad or risky change: `just all`
- Coverage-focused work: `just coverage`