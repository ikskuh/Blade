dotnet          := require("dotnet")
reportgenerator := require('reportgenerator')
python          := require('python')
roslynator      := require('roslynator')

BLADE_TEST_PORT := "/dev/serial/by-id/usb-FTDI_FT231X_USB_UART_DUAB9RPU-if00-port0"

all: build test regressions compile-all-samples

install-tools:
    dotnet tool install --global dotnet-trace
    dotnet tool install --global roslynator.dotnet.cli
    dotnet tool install --global dotnet-reportgenerator-globaltool
    dotnet tool install --global sharpfuzz.commandline


# Quality Gate for changes
accept-changes:
    # Build code in debug and release mode:
    {{dotnet}} build --no-restore -verbosity minimal -c debug
    {{dotnet}} build --no-restore -verbosity minimal -c release

    # Run unit tests in debug and release mode:
    BLADE_TEST_PORT="{{BLADE_TEST_PORT}}" {{dotnet}} test --no-restore -verbosity minimal -c debug
    BLADE_TEST_PORT="{{BLADE_TEST_PORT}}" {{dotnet}} test --no-restore -verbosity minimal -c release

    # Run static analysis
    (cd Blade && {{roslynator}} analyze)

analyze:
    (cd Blade && {{roslynator}} analyze)

build:
    {{dotnet}} build

test:
    {{dotnet}} test

coverage: \
    (_base_coverage_collect "")

coverage-regressions: \
    (_base_coverage_collect "--filter 'FullyQualifiedName=Blade.Tests.RegressionHarnessTests.FullRegressionSuite_Passes'")

# Base rule for coverage collection, can pass additional parameters to "dotnet test"
_base_coverage_collect params:
    # delete the previous coverage so we get a clean slate:
    rm -rf coverage/current

    # execute "dotnet test" with code coverage collection and only include the
    # compiler sources, exclude all others:
    {{dotnet}} test \
        --collect:"XPlat Code Coverage;Format=cobertura;Include=[blade]*;Exclude=[Blade.Regressions]*" \
        --results-directory coverage/current \
        {{params}}
    
    # move into "latest stage":
    mv coverage/current/*/coverage.cobertura.xml coverage/coverage.cobertura.xml

    # apply pragma-based coverage adjustments without reformatting the XML:
    {{dotnet}} run --project Tools/CoverageFilter -- coverage/coverage.cobertura.xml

    # print a short summary of the code coverage data:
    {{python}} Scripts/codecov-report.py coverage/coverage.cobertura.xml

# Creates a code coverage report from the test suite and the regression runner.
coverage-report-full: coverage
    {{reportgenerator}} \
        -reports:"coverage/coverage.cobertura.xml" \
        -targetdir:"coverage/results" \
        -historydir:"coverage/history" \
        "-reporttypes:Html;TextSummary;TextDeltaSummary;CsvSummary"
    @echo "$PWD/coverage/results/index.html"

# Creates a coverage report that is only driven by the regression runner and not by the test suite.
coverage-report-regression: coverage-regressions
    {{reportgenerator}} \
        -reports:"coverage/coverage.cobertura.xml" \
        -targetdir:"coverage/regression-results" \
        -historydir:"coverage/regression-history" \
        "-reporttypes:Html;TextSummary;TextDeltaSummary;CsvSummary"
    @echo "$PWD/coverage/regression-results/index.html"

regressions:
    BLADE_TEST_PORT="{{BLADE_TEST_PORT}}" {{dotnet}} run --no-restore --project Blade.Regressions --

# Runs the fuzzer suite
fuzz:
    rm -rf fuzzing/{findings,build}
    mkdir -p fuzzing/{corpus,findings,build}

    # Sync corpus:
    find Demonstrators -type f -name "*.blade" -exec cp '{}' fuzzing/corpus ';'
    find Examples      -type f -name "*.blade" -exec cp '{}' fuzzing/corpus ';'

    # Compute dictionary based off the demonstrator files
    {{python}} Scripts/export_fuzz_dict.py --from Demonstrators/ --dict fuzzing/blade.dict

    DOTNET_ROLL_FORWARD=Major {{python}} Scripts/fuzz.py \
        --project   Blade.FuzzTest/Blade.FuzzTest.csproj \
        --corpus    fuzzing/corpus  \
        --dict      fuzzing/blade.dict \
        --findings  fuzzing/findings  \
        --build     fuzzing/build  \
        --command   ~/.dotnet/tools/sharpfuzz

export-fuzz-findings:
    {{python}} Scripts/export_fuzz_crash.py \
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
    (compile-sample "Examples/hub_string_walk.blade") \
    (compile-sample "Examples/coroutines.blade")


compile-sample foo:
    Blade/bin/Debug/net10.0/blade --dump-all {{foo}} > {{ without_extension(foo) }}.dump.txt 
 

hwtest path:
    mkdir -p .hwtest

    Blade/bin/Debug/net10.0/blade \
        "--runtime=Blade.HwTestRunner/Runtime.spin2" \
        "--output" ".hwtest/payload.spin2" \
        "{{path}}"
    
    flexspin \
        -2 -b \
        -o ".hwtest/payload.bin" \
        ".hwtest/payload.spin2"

    Blade.HwTestRunner/bin/Debug/net10.0/Blade.HwTestRunner \
        .hwtest/payload.bin \
        0xDEADBEEF
