set unstable

# TODO: Use {{dotnet}} variable
dotnet := require("dotnet")

reportgenerator := which('reportgenerator') || which('dotnet-reportgenerator')

all: build test regressions compile-all-samples

accept-changes:
    dotnet build -c debug
    dotnet build -c release

    dotnet test -c debug
    dotnet test -c release

build:
    dotnet build

test:
    dotnet test

coverage: \
    (_base_coverage_collect "")

coverage-regressions: \
    (_base_coverage_collect "--filter 'FullyQualifiedName=Blade.Tests.RegressionHarnessTests.FullRegressionSuite_Passes")

# Base rule for coverage collection, can pass additional parameters to "dotnet test"
_base_coverage_collect params:
    # delete the previous coverage so we get a clean slate:
    rm -rf coverage/current

    # execute "dotnet test" with code coverage collection and only include the
    # compiler sources, exclude all others:
    dotnet test \
        --collect:"XPlat Code Coverage;Format=cobertura;Include=[blade]*;Exclude=[Blade.Regressions]*" \
        --results-directory coverage/current \
        {{params}}
    
    # move into "latest stage":
    mv coverage/current/*/coverage.cobertura.xml coverage/coverage.cobertura.xml

    # print a short summary of the code coverage data:
    @python3 Scripts/codecov-report.py coverage/coverage.cobertura.xml

coverage-report: coverage
    {{reportgenerator}} \
        -reports:"coverage/coverage.cobertura.xml" \
        -targetdir:"coverage/results" \
        -historydir:"coverage/history" \
        "-reporttypes:Html;TextSummary;TextDeltaSummary;CsvSummary"

regressions:
    dotnet run --project Blade.Regressions --

# Runs the fuzzer suite
fuzz:
    rm -rf fuzzing/{findings,build}
    mkdir -p fuzzing/{corpus,findings,build}

    # Sync corpus:
    find Demonstrators -type f -name "*.blade" -exec cp '{}' fuzzing/corpus ';'
    find Examples      -type f -name "*.blade" -exec cp '{}' fuzzing/corpus ';'

    # Compute dictionary based off the demonstrator files
    python Scripts/export_fuzz_dict.py --from Demonstrators/ --dict fuzzing/blade.dict


    DOTNET_ROLL_FORWARD=Major python3 \
        Scripts/fuzz.py \
        --project   Blade.FuzzTest/Blade.FuzzTest.csproj \
        --corpus    fuzzing/corpus  \
        --dict      fuzzing/blade.dict \
        --findings  fuzzing/findings  \
        --build     fuzzing/build  \
        --command   ~/.dotnet/tools/sharpfuzz

export-fuzz-findings:
    python Scripts/export_fuzz_crash.py \
        --from fuzzing/findings/default/crashes/ \
        --to RegressionTests/Fuzzing/ \
        --count 5

compile-all-samples: build \
    (compile-sample "Examples/blinky.blade") \
    (compile-sample "Examples/clamp.blade") \
    (compile-sample "Examples/fibonacci.blade") \
    (compile-sample "Examples/inline_asm_bit_test.blade") \
    (compile-sample "Examples/inline_asm_cordic.blade") \
    (compile-sample "Examples/inline_asm_streamer.blade") \
    (compile-sample "Examples/register_aliases.blade") \
    (compile-sample "Examples/sum_loop.blade") \
    (compile-sample "Demonstrators/Asm/volatile_routines.blade") \
    (compile-sample "Demonstrators/Asm/optimizer_exercises.blade") \
    (compile-sample "Demonstrators/Asm/io_regular_asm.blade") \
    (compile-sample "Demonstrators/Asm/math_routines.blade") \
    (compile-sample "Demonstrators/Bugs/missing-copy.blade")


compile-sample foo:
    Blade/bin/Debug/net10.0/blade --dump-all {{foo}} > {{ without_extension(foo) }}.dump.txt 
 
