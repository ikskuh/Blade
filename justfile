
all: build test compile-all-samples compile-known-breakage-samples

build:
    dotnet build

test:
    dotnet test

coverage:
    dotnet test --collect:"XPlat Code Coverage"

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
    (compile-sample "Demonstrators/Asm/math_routines.blade")

# Samples in this target are intentionally expected to fail until the marked
# front-end / lowering gaps are implemented.
compile-known-breakage-samples: build \
    (compile-sample-expected-breakage "Examples/hub_string_walk.blade")


compile-sample foo:
    Blade/bin/Debug/net10.0/blade --dump-all {{foo}} > {{ without_extension(foo) }}.dump.txt 

compile-sample-expected-breakage foo:
    @echo "EXPECTED BREAKAGE: {{foo}}"
    -Blade/bin/Debug/net10.0/blade --dump-all {{foo}} > {{ without_extension(foo) }}.dump.txt 2>&1
 
