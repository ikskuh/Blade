# Suggested commands

## Core workflow
- `just build` : build the compiler and related projects
- `just test` : run the NUnit test suite
- `just regressions` : run the full regression harness
- `just all` : full local verification (`build`, `test`, `regressions`, `compile-all-samples`)

## Coverage
- `just coverage` : collect coverage into `coverage/coverage.cobertura.xml`
- `just coverage-regressions` : collect coverage for the integrated regression harness path
- `python Scripts/coverage_range.py <class_pattern> <start_line> <end_line>` : inspect uncovered lines after coverage collection

## Sample compilation
- `just compile-sample <path>` : compile one sample with `--dump-all`
- `just compile-all-samples` : compile the curated example/demonstrator set
- `dotnet run --project Blade -- <file.blade>` : run the compiler CLI on one source file
- `dotnet run --project Blade -- --dump-all <file.blade>` : emit all compiler dumps for a sample
- `dotnet run --project Blade.Regressions --` : run the regression harness executable directly

## Metadata generation
- `source .venv/bin/activate && python Scripts/extract.py` : regenerate `Blade/P2InstructionMetadata.g.cs`

## External tool
- `flexspin -2 -c -q <wrapped-asm-file>` : validate emitted PASM2 assembly; emitted PASM must be wrapped in `DAT` / `org 0`

## Useful Linux shell commands
- `git status`, `git diff`
- `ls`, `find`, `cd`
- `rg <pattern>` and `rg --files`
- `sed -n 'start,endp' <file>`
- `cat <file>`