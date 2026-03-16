
all: build test regressions compile-all-samples

build:
    dotnet build

test:
    dotnet test

coverage:
    rm -rf coverage
    dotnet test \
        --collect:"XPlat Code Coverage;Format=cobertura;Include=[blade]*;Exclude=[Blade.Regressions]*" \
        --results-directory coverage
    cp coverage/*/coverage.cobertura.xml coverage/coverage.cobertura.xml
    @python3 -c " \
        import xml.etree.ElementTree as ET; \
        r = ET.parse('coverage/coverage.cobertura.xml').getroot(); \
        lr=float(r.get('line-rate'))*100; br=float(r.get('branch-rate'))*100; \
        print(f'Line coverage:   {lr:.1f}% ({r.get(\"lines-covered\")}/{r.get(\"lines-valid\")})'); \
        print(f'Branch coverage: {br:.1f}% ({r.get(\"branches-covered\")}/{r.get(\"branches-valid\")})')"

regressions:
    dotnet run --project Blade.Regressions --

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
 
