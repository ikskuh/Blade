# Code unreachable through the frontend

All previously listed items in this file were either removed from the codebase or turned into assertions/diagnostics.

New findings should be logged here as:

- location (`path:line` + symbol name)
- why it is believed unreachable (frontend path argument)
- what to do: delete, replace with `Assert.Unreachable*`, or add a frontend fixture to prove reachability

- `Blade/IR/Asm/AsmLowerer.cs:954` `TryLowerInlineAsmOperand` / `InlineAsmSymbolOperand`
  binder never constructs `InlineAsmSymbolOperand`; direct inline-asm symbol references always bind to labels, special registers, or binding refs
  what to do: delete the dead operand type and lowering branch

- `Blade/P2InstructionMetadata.g.cs:558` `P2InstructionFormInfo.GetOperandInfo`
  public callers guard `operandIndex` before reaching the per-form accessor, so the `_ => default` arm is not reachable from the frontend
  what to do: replace with `Assert.UnreachableValue`

- `Blade/IR/Mir/MirModel.cs:544` `MirUpdatePlaceInstruction.IsPointerArithmetic`
  helper property is not read anywhere; frontend only consumes `PointerArithmeticStride`
  what to do: delete

- `Blade/IR/Lir/LirModel.cs:452` `LirUpdatePlaceOperation.IsPointerArithmetic`
  helper property is not read anywhere; frontend only consumes `PointerArithmeticStride`
  what to do: delete
