
Stuff to validate:

## `CALLPA` bindings are important

Leaf functions through CALL/PA should prefer the "PA" parameter for their first parameter value.

This plays together nicely with the register allocator running backwards.


## Implementation of array loads/stores

Right now only the high-level part is implemented, but lowering the array access is not.

## constant file rendering is broken

uses the old rendering method which is weirdly formatted.


## Until asm writer, use symbolic label names

Instead of using string based symbolic operands, use a C# reference type
for referring to actual symbol names. This prevents accidential confusion
in the future.

```cs
// before:
string name = ctx.Function.Name;
new AsmSymbolOperand(name, AsmSymbolAddressingMode.Immediate)

// after:
ISymbol name = ctx.Function.Name;
new AsmSymbolOperand(name, AsmSymbolAddressingMode.Immediate)
```

Proposal:

```cs

enum SymbolType
{
    RegisterVariable, // "g_foobar LONG 0"
    Function, // "f_main RET"
}

interface ISymbol
{
    string Name { get; } // potentially allow set with a "write once" operation in a single "symbol name allocator"

    SymbolType SymbolType { get; }
}
```

## Clean up string based mnemonics

In `P2InstructionMetadata.g.cs`, remove all generation of `string mnemonic`, as we now have `P2Mnemonic mnemonic` available, which is type safe and cannot fail.

`FormsByMnemonic` is still string based, but can be transformed into `FrozenDictionary<P2Mnemonic, FrozenDictionary<int, P2InstructionFormInfo>>`

`FrozenSet<string> ValidMnemonics` can be replaced by a simple `Enum.Parse<P2Mnemonic>()`

```
P2InstructionOperandInfo GetOperandInfo(string mnemonic
bool UsesImmediateSyntax(string mnemonic,
bool UsesImmediateSymbolSyntax(string mnemonic
P2OperandAccess GetOperandAccess(string mnemonic
bool TryGetInstructionForm(string mnemonic,
```


## Inline Asm allows referencing unknown labels

```blade
fn unsupported_operand_falls_back(x: u32) -> u32 {
    var out: u32 = 0;
    asm {
        MOV {out}, #target_label
    };
    return out ^ x;
}
```

works right now, even if `target_label` is not defined. inline asm should only ever have access to its own "private" labels,
and never to labels defined outside the asm block. These labels should also be rewritten into a local namespace:

```
fn foo() {
    asm {
        JMP #label ' points to label/0
    label: ' label/0
    }
    asm {
        JMP #label ' this points to label/1
    label: ' label/1
    }
}
```
should use something like `f_foo_asm0_label` and `f_foo_asm1_label`.