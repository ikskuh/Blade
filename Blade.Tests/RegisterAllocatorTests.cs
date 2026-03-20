using System.Collections.Generic;
using System.Reflection;
using Blade.IR;
using Blade.IR.Asm;
using Blade.Semantics;

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
}
