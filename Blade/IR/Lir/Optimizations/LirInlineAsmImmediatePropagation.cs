using System;
using System.Collections.Generic;
using Blade.Semantics;
using static Blade.IR.Lir.LirOptimizationHelpers;

namespace Blade.IR.Lir.Optimizations;

/// <summary>
/// Rewrites read-only inline-asm bindings for const declarations to immediates.
/// </summary>
[LirOptimization("asm-const-decl-imm-propagation", Priority = 850)]
public sealed class LirInlineAsmConstDeclarationImmediatePropagation : ILirOptimization
{
    public LirModule? Run(LirModule input)
        => LirInlineAsmImmediatePropagation.Run(
            input,
            static binding => binding.Symbol is VariableSymbol { IsConst: true });
}

/// <summary>
/// Rewrites read-only inline-asm bindings for unchanged mutable declarations to immediates.
/// </summary>
[LirOptimization("asm-var-decl-imm-propagation", Priority = 840)]
public sealed class LirInlineAsmVarDeclarationImmediatePropagation : ILirOptimization
{
    public LirModule? Run(LirModule input)
        => LirInlineAsmImmediatePropagation.Run(
            input,
            static binding => binding.Symbol is VariableSymbol
            {
                IsConst: false,
                ScopeKind: VariableScopeKind.Local or VariableScopeKind.GlobalStorage,
            });
}

internal static class LirInlineAsmImmediatePropagation
{
    internal static LirModule? Run(LirModule input, Func<LirInlineAsmBinding, bool> shouldRewrite)
    {
        Requires.NotNull(input);
        Requires.NotNull(shouldRewrite);

        bool anyChanged = false;
        List<LirFunction> functions = new(input.Functions.Count);
        foreach (LirFunction function in input.Functions)
        {
            bool functionChanged = false;
            List<LirBlock> blocks = new(function.Blocks.Count);
            foreach (LirBlock block in function.Blocks)
            {
                Dictionary<LirVirtualRegister, BladeValue> constantValues = [];
                bool blockChanged = false;
                List<LirInstruction> instructions = new(block.Instructions.Count);
                foreach (LirInstruction instruction in block.Instructions)
                {
                    LirInstruction rewritten = RewriteInlineAsmInstruction(instruction, constantValues, shouldRewrite);
                    instructions.Add(rewritten);
                    blockChanged |= !ReferenceEquals(rewritten, instruction);

                    if (rewritten.Destination is LirVirtualRegister destination)
                        constantValues.Remove(destination);

                    foreach (LirVirtualRegister written in EnumerateWrites(rewritten))
                        constantValues.Remove(written);

                    if (TryGetConstantValue(rewritten, out LirVirtualRegister constantDestination, out BladeValue? constantValue))
                        constantValues[constantDestination] = constantValue;
                }

                if (blockChanged)
                {
                    functionChanged = true;
                    blocks.Add(new LirBlock(block.Ref, block.Parameters, instructions, block.Terminator));
                }
                else
                {
                    blocks.Add(block);
                }
            }

            anyChanged |= functionChanged;
            functions.Add(functionChanged ? new LirFunction(function.SourceFunction, blocks) : function);
        }

        return anyChanged
            ? new LirModule(input.SourceModule, input.StoragePlaces, input.StorageDefinitions, functions)
            : null;
    }

    private static LirInstruction RewriteInlineAsmInstruction(
        LirInstruction instruction,
        IReadOnlyDictionary<LirVirtualRegister, BladeValue> constantValues,
        Func<LirInlineAsmBinding, bool> shouldRewrite)
    {
        if (instruction is not LirInlineAsmInstruction inlineAsm)
            return instruction;

        List<LirInlineAsmBinding>? rewrittenBindings = null;
        for (int i = 0; i < inlineAsm.Bindings.Count; i++)
        {
            LirInlineAsmBinding binding = inlineAsm.Bindings[i];
            if (binding.Access != InlineAsmBindingAccess.Read
                || !shouldRewrite(binding)
                || binding.Operand is not LirRegisterOperand register
                || !constantValues.TryGetValue(register.Register, out BladeValue? constantValue))
            {
                continue;
            }

            rewrittenBindings ??= new List<LirInlineAsmBinding>(inlineAsm.Bindings);
            rewrittenBindings[i] = new LirInlineAsmBinding(
                binding.Slot,
                binding.Symbol,
                new LirImmediateOperand(constantValue),
                binding.Access);
        }

        if (rewrittenBindings is null)
            return instruction;

        return new LirInlineAsmInstruction(
            inlineAsm.Volatility,
            inlineAsm.FlagOutput,
            inlineAsm.ParsedLines,
            rewrittenBindings,
            inlineAsm.Span);
    }
}