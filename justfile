
all: build test compile-all-samples

build:
    dotnet build

test:
    dotnet test

compile-all-samples: build \
    (compile-sample "Examples/blinky.blade") \
    (compile-sample "Examples/inline_asm_bit_test.blade") \
    (compile-sample "Examples/inline_asm_cordic.blade") \
    (compile-sample "Examples/inline_asm_streamer.blade")


compile-sample foo:
    Blade/bin/Debug/net10.0/blade --dump-all {{foo}} > {{ without_extension(foo) }}.dump.txt 
 
