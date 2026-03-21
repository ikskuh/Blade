# Code style and conventions

## General style
- Prefer clarity over brevity
- Do not use `var` when the type is not obvious from the right-hand side
- Expected failures should use result-style handling / explicit error returns; exceptions are for bugs
- Runtime error paths must be handled explicitly and not swallowed
- Use `Debug.Assert` for invariants guaranteed by earlier stages instead of unreachable fallback code
- Public argument validation should use helpers from `Blade/Requires.cs` such as `Requires.NotNull`, `Requires.NonNegative`, `Requires.Positive`, and `Requires.InRange`
- Build output should remain warning-free

## Analyzer policy
- `.editorconfig` is the source of truth for analyzer policy
- Narrow, well-explained in-source suppressions are allowed only for isolated cases
- Globalization and string comparison intent are enforced (`CA1305`, `CA1307`)
- Public APIs are expected to validate inputs (`CA1062`)

## Compiler-specific conventions
- Every diagnostic needs a numeric code with `E`, `W`, or `I` prefix, a human-readable name, and a format string
- Code paths not reachable through the compiler frontend are considered dead and should be replaced with assertions
- Syntax or grammar work must stay aligned with `Docs/reference.blade`
- Parser/lexer changes require matching test updates
- Prefer adding a demonstrator in `Demonstrators/` over adding a dedicated unit test when validating compiler behavior or coverage

## Testing conventions
- `Examples/` should remain pristine and header-free
- `Demonstrators/` may include expectations and are part of the regression corpus
- `RegressionTests/` contains focused bug, diagnostic, and assembly fixtures
- Unit tests that need filesystem access should use `Blade.Tests/TempDirectory`