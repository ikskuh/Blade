using System.Collections.Generic;
using System.Reflection;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.Tests;

[TestFixture]
public class RegisterAllocatorTests
{
    [Test]
    public void RewriteInlineAsmText_LeavesUnknownBindingsAndFormatsPlacesAndImmediates()
    {
        VariableSymbol symbol = new(
            "slot",
            BuiltinTypes.U32,
            isConst: false,
            VariableStorageClass.Reg,
            VariableScopeKind.GlobalStorage,
            isExtern: false,
            fixedAddress: null,
            alignment: null);
        StoragePlace place = new(symbol, StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: null);

        string rewritten = RewriteInlineAsmText(
            "MOV {place}, {imm}, {missing} ' comment",
            new Dictionary<string, AsmOperand>(StringComparer.Ordinal)
            {
                ["place"] = new AsmPlaceOperand(place),
                ["imm"] = new AsmImmediateOperand(7),
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<int, int>());

        Assert.That(rewritten, Is.EqualTo("MOV g_slot, #7, missing ' comment"));
    }

    [Test]
    public void CodegenPipeline_RemovesSelfMovesIntroducedByRegisterAllocation()
    {
        AsmRegisterOperand source = new(1);
        AsmRegisterOperand copy = new(2);

        AsmModule asmModule = new([
            new AsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmInstructionNode("MOV", [copy, source]),
                new AsmInstructionNode("MOV", [new AsmSymbolOperand("OUTA", AsmSymbolAddressingMode.Register), copy]),
            ]),
        ]);

        EmitResult emit = CodegenPipeline.Emit(CreateBuildResult(asmModule), new EmitOptions
        {
            EnabledAsmirOptimizations = [OptimizationRegistry.GetAsmOptimization("cleanup-self-mov")!],
        });

        Assert.That(emit.AssemblyText, Does.Not.Match(@"MOV\s+(_r\d+),\s+\1\b"), emit.AssemblyText);
        Assert.That(emit.AssemblyText, Does.Match(@"MOV\s+OUTA,\s+_r\d+\b"), emit.AssemblyText);
    }

    [Test]
    public void CodegenPipeline_LeavesAllocatorSelfMovesWhenCleanupSelfMovDisabled()
    {
        AsmRegisterOperand source = new(1);
        AsmRegisterOperand copy = new(2);

        AsmModule asmModule = new([
            new AsmFunction("f", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmInstructionNode("MOV", [copy, source]),
                new AsmInstructionNode("MOV", [new AsmSymbolOperand("OUTA", AsmSymbolAddressingMode.Register), copy]),
            ]),
        ]);

        EmitResult emit = CodegenPipeline.Emit(CreateBuildResult(asmModule), new EmitOptions
        {
            EnabledAsmirOptimizations = [],
        });

        Assert.That(emit.AssemblyText, Does.Match(@"MOV\s+(_r\d+),\s+\1\b"), emit.AssemblyText);
    }

    private static string RewriteInlineAsmText(
        string text,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<string, string> localLabels,
        IReadOnlyDictionary<int, int> regToSlot)
    {
        MethodInfo method = typeof(RegisterAllocator).GetMethod(
            "RewriteInlineAsmText",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return (string)method.Invoke(null, [text, bindings, localLabels, regToSlot])!;
    }

    private static IrBuildResult CreateBuildResult(AsmModule asmModule)
    {
        BoundProgram program = new([], [], [], new Dictionary<string, TypeSymbol>(), new Dictionary<string, FunctionSymbol>(), new Dictionary<string, ImportedModule>());
        MirModule mirModule = new([]);
        LirModule lirModule = new([]);
        return new IrBuildResult(program, mirModule, mirModule, lirModule, lirModule, asmModule, asmModule, string.Empty);
    }
}
