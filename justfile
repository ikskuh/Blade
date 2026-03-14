
all: build test regressions compile-all-samples

build:
    dotnet build

test:
    dotnet test

coverage:
    dotnet test --collect:"XPlat Code Coverage"

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
 
