using System.Collections.Generic;
using System.Linq;
using Blade.IR;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class MirOptimizerTests
{
    private static BladeValue Value(BladeType type, object value)
    {
        if (type is ArrayTypeSymbol arrayType
            && arrayType.ElementType == BuiltinTypes.U8
            && value is byte[] bytes)
        {
            return BladeValue.U8Array(bytes);
        }

        object canonicalValue = CanonicalizeValue(type, value);
        return type switch
        {
            RuntimeTypeSymbol runtimeType => new RuntimeBladeValue(runtimeType, canonicalValue),
            ComptimeTypeSymbol comptimeType => new ComptimeBladeValue(comptimeType, canonicalValue),
            _ => throw new System.InvalidOperationException($"Unsupported MIR test constant type '{type.Name}'."),
        };
    }

    private static object CanonicalizeValue(BladeType type, object value)
    {
        if (type is IntegerLiteralTypeSymbol or IntegerTypeSymbol or EnumTypeSymbol or BitfieldTypeSymbol)
        {
            return value switch
            {
                sbyte sbyteValue => (long)sbyteValue,
                byte byteValue => (long)byteValue,
                short shortValue => (long)shortValue,
                ushort ushortValue => (long)ushortValue,
                int intValue => (long)intValue,
                uint uintValue => (long)uintValue,
                long longValue => longValue,
                _ => value,
            };
        }

        return value;
    }

    private static MirConstantInstruction Constant(MirValueId result, BladeType type, object value, TextSpan span) => new(result, type, Value(type, value), span);

    private static StructTypeSymbol CreateStructType(
        string name,
        int sizeBytes,
        int alignmentBytes,
        params AggregateMemberSymbol[] members)
    {
        Dictionary<string, BladeType> fields = new(StringComparer.Ordinal);
        Dictionary<string, AggregateMemberSymbol> memberMap = new(StringComparer.Ordinal);
        foreach (AggregateMemberSymbol member in members)
        {
            fields[member.Name] = member.Type;
            memberMap[member.Name] = member;
        }

        return new StructTypeSymbol(name, fields, memberMap, sizeBytes, alignmentBytes);
    }

    [Test]
    public void ConstantPropagation_FoldsUnaryAndBinaryOperators()
    {
        TextSpan span = new(0, 0);

        MirValueId intOne = MirValue(1);
        MirValueId intTwo = MirValue(2);
        MirValueId intSeven = MirValue(3);
        MirValueId intEight = MirValue(4);
        MirValueId boolTrue = MirValue(5);
        MirValueId boolFalse = MirValue(6);
        MirValueId stringLeft = MirValue(7);
        MirValueId stringRight = MirValue(8);
        MirValueId zero = MirValue(30);
        ArrayTypeSymbol leftLiteralType = new(BuiltinTypes.U8, 4);
        ArrayTypeSymbol rightLiteralType = new(BuiltinTypes.U8, 5);

        List<MirInstruction> instructions =
        [
            Constant(intOne, BuiltinTypes.U32, 1u, span),
            Constant(intTwo, BuiltinTypes.U32, 2u, span),
            Constant(intSeven, BuiltinTypes.U32, 7u, span),
            Constant(intEight, BuiltinTypes.U32, 8u, span),
            Constant(zero, BuiltinTypes.U32, 0u, span),
            Constant(boolTrue, BuiltinTypes.Bool, true, span),
            Constant(boolFalse, BuiltinTypes.Bool, false, span),
            Constant(stringLeft, leftLiteralType, new byte[] { 108, 101, 102, 116 }, span),
            Constant(stringRight, rightLiteralType, new byte[] { 114, 105, 103, 104, 116 }, span),
            new MirUnaryInstruction(MirValue(10), BuiltinTypes.Bool, BoundUnaryOperatorKind.LogicalNot, boolFalse, span),
            new MirUnaryInstruction(MirValue(11), BuiltinTypes.I32, BoundUnaryOperatorKind.Negation, intOne, span),
            new MirUnaryInstruction(MirValue(12), BuiltinTypes.U32, BoundUnaryOperatorKind.BitwiseNot, intOne, span),
            new MirUnaryInstruction(MirValue(13), BuiltinTypes.U32, BoundUnaryOperatorKind.UnaryPlus, intTwo, span),
            new MirUnaryInstruction(MirValue(14), BuiltinTypes.Bool, BoundUnaryOperatorKind.LogicalNot, stringLeft, span),
            new MirBinaryInstruction(MirValue(20), BuiltinTypes.U32, BoundBinaryOperatorKind.Add, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(21), BuiltinTypes.U32, BoundBinaryOperatorKind.Subtract, intTwo, intOne, span),
            new MirBinaryInstruction(MirValue(22), BuiltinTypes.U32, BoundBinaryOperatorKind.Multiply, intTwo, intTwo, span),
            new MirBinaryInstruction(MirValue(23), BuiltinTypes.U32, BoundBinaryOperatorKind.Divide, intEight, intTwo, span),
            new MirBinaryInstruction(MirValue(24), BuiltinTypes.U32, BoundBinaryOperatorKind.Divide, intEight, zero, span),
            new MirBinaryInstruction(MirValue(25), BuiltinTypes.U32, BoundBinaryOperatorKind.Modulo, intSeven, intTwo, span),
            new MirBinaryInstruction(MirValue(26), BuiltinTypes.U32, BoundBinaryOperatorKind.Modulo, intSeven, zero, span),
            new MirBinaryInstruction(MirValue(27), BuiltinTypes.U32, BoundBinaryOperatorKind.BitwiseAnd, intSeven, intTwo, span),
            new MirBinaryInstruction(MirValue(28), BuiltinTypes.U32, BoundBinaryOperatorKind.BitwiseOr, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(29), BuiltinTypes.U32, BoundBinaryOperatorKind.BitwiseXor, intSeven, intTwo, span),
            new MirBinaryInstruction(MirValue(31), BuiltinTypes.U32, BoundBinaryOperatorKind.ShiftLeft, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(32), BuiltinTypes.U32, BoundBinaryOperatorKind.ShiftRight, intEight, intOne, span),
            new MirBinaryInstruction(MirValue(33), BuiltinTypes.U32, BoundBinaryOperatorKind.ArithmeticShiftLeft, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(34), BuiltinTypes.U32, BoundBinaryOperatorKind.ArithmeticShiftRight, intEight, intOne, span),
            new MirBinaryInstruction(MirValue(35), BuiltinTypes.U32, BoundBinaryOperatorKind.RotateLeft, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(36), BuiltinTypes.U32, BoundBinaryOperatorKind.RotateRight, intEight, intOne, span),
            new MirBinaryInstruction(MirValue(37), BuiltinTypes.Bool, BoundBinaryOperatorKind.Equals, intOne, intOne, span),
            new MirBinaryInstruction(MirValue(38), BuiltinTypes.Bool, BoundBinaryOperatorKind.NotEquals, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(39), BuiltinTypes.Bool, BoundBinaryOperatorKind.Less, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(40), BuiltinTypes.Bool, BoundBinaryOperatorKind.LessOrEqual, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(41), BuiltinTypes.Bool, BoundBinaryOperatorKind.Greater, intTwo, intOne, span),
            new MirBinaryInstruction(MirValue(42), BuiltinTypes.Bool, BoundBinaryOperatorKind.GreaterOrEqual, intTwo, intOne, span),
            new MirBinaryInstruction(MirValue(43), BuiltinTypes.Bool, BoundBinaryOperatorKind.Equals, boolTrue, boolFalse, span),
            new MirBinaryInstruction(MirValue(44), BuiltinTypes.Bool, BoundBinaryOperatorKind.NotEquals, boolTrue, boolFalse, span),
            new MirBinaryInstruction(MirValue(45), BuiltinTypes.Bool, BoundBinaryOperatorKind.LogicalAnd, boolTrue, boolFalse, span),
            new MirBinaryInstruction(MirValue(46), BuiltinTypes.Bool, BoundBinaryOperatorKind.LogicalOr, boolTrue, boolFalse, span),
            new MirBinaryInstruction(MirValue(47), BuiltinTypes.Bool, BoundBinaryOperatorKind.LogicalAnd, stringLeft, stringRight, span),
            new MirBinaryInstruction(MirValue(48), BuiltinTypes.Bool, BoundBinaryOperatorKind.LogicalAnd, intOne, intTwo, span),
            new MirBinaryInstruction(MirValue(49), BuiltinTypes.Bool, BoundBinaryOperatorKind.Add, boolTrue, boolFalse, span),
        ];

        MirBlock block = new(MirBlockRef("bb0"), [], instructions, new MirReturnTerminator([], span));
        MirFunction function = CreateMirFunction("test", isEntryPoint: true, FunctionKind.Default, [], [block]);
        MirModule optimized = MirOptimizer.Optimize(CreateMirModule(functions: [function]), maxIterations: 1, enabledOptimizations: [OptimizationRegistry.GetMirOptimization("const-prop")!]);

        IReadOnlyList<MirInstruction> rewritten = optimized.Functions[0].Blocks[0].Instructions;
        Dictionary<MirValueId, BladeValue> constants = rewritten
            .OfType<MirConstantInstruction>()
            .Where(static instruction => instruction.Value is not null)
            .ToDictionary(instruction => instruction.Result!, instruction => instruction.Value!);

        Assert.That(rewritten[13], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(rewritten[40], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(rewritten[41], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(rewritten[^1], Is.Not.TypeOf<MirConstantInstruction>());
        AssertBoolConstant(constants, MirValue(10), true);
        AssertIntegerConstant(constants, MirValue(11), -1L);
        AssertIntegerConstant(constants, MirValue(12), 0xFFFF_FFFEL);
        AssertIntegerConstant(constants, MirValue(13), 2L);
        AssertIntegerConstant(constants, MirValue(20), 3L);
        AssertIntegerConstant(constants, MirValue(21), 1L);
        AssertIntegerConstant(constants, MirValue(22), 4L);
        AssertIntegerConstant(constants, MirValue(23), 4L);
        Assert.That(constants.ContainsKey(MirValue(24)), Is.False);
        AssertIntegerConstant(constants, MirValue(25), 1L);
        Assert.That(constants.ContainsKey(MirValue(26)), Is.False);
        AssertIntegerConstant(constants, MirValue(27), 2L);
        AssertIntegerConstant(constants, MirValue(28), 3L);
        AssertIntegerConstant(constants, MirValue(29), 5L);
        AssertIntegerConstant(constants, MirValue(31), 4L);
        AssertIntegerConstant(constants, MirValue(32), 4L);
        AssertIntegerConstant(constants, MirValue(33), 4L);
        AssertIntegerConstant(constants, MirValue(34), 4L);
        AssertIntegerConstant(constants, MirValue(35), 4L);
        AssertIntegerConstant(constants, MirValue(36), 4L);
        AssertBoolConstant(constants, MirValue(37), true);
        AssertBoolConstant(constants, MirValue(38), true);
        AssertBoolConstant(constants, MirValue(39), true);
        AssertBoolConstant(constants, MirValue(40), true);
        AssertBoolConstant(constants, MirValue(41), true);
        AssertBoolConstant(constants, MirValue(42), true);
        AssertBoolConstant(constants, MirValue(43), false);
        AssertBoolConstant(constants, MirValue(44), true);
        AssertBoolConstant(constants, MirValue(45), false);
        AssertBoolConstant(constants, MirValue(46), true);
    }

    [Test]
    public void Inliner_PreservesIndexedPointerAndAggregateMetadataOnClonedInstructions()
    {
        TextSpan span = new(0, 0);
        MultiPointerTypeSymbol manyType = new(BuiltinTypes.U32, isConst: false, storageClass: AddressSpace.Cog);
        PointerTypeSymbol pointerType = new(BuiltinTypes.U16, isConst: false, storageClass: AddressSpace.Hub);
        AggregateMemberSymbol bitfieldMember = new("high", BuiltinTypes.Nib, byteOffset: 0, bitOffset: 4, bitWidth: 4, isBitfield: true);
        BitfieldTypeSymbol flagsType = new(
            "Flags",
            BuiltinTypes.U32,
            new Dictionary<string, BladeType>(StringComparer.Ordinal)
            {
                ["high"] = BuiltinTypes.Nib,
            },
            new Dictionary<string, AggregateMemberSymbol>(StringComparer.Ordinal)
            {
                ["high"] = bitfieldMember,
            });
        AggregateMemberSymbol structMember = new("value", BuiltinTypes.U16, byteOffset: 1, bitOffset: 0, bitWidth: 0, isBitfield: false);
        StructTypeSymbol packedType = CreateStructType(
            "Packed",
            sizeBytes: 4,
            alignmentBytes: 1,
            new AggregateMemberSymbol("tag", BuiltinTypes.U8, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
            structMember);

        FunctionSymbol calleeSymbol = new("callee", IrTestFactory.CreateFunctionDeclarationSyntax("callee"), FunctionKind.Default, isTopLevel: false, storageClass: null, FunctionInliningPolicy.Default, SourceSpan.Synthetic())
        {
            Parameters =
            [
                new ParameterVariableSymbol("many", manyType, SourceSpan.Synthetic()),
                new ParameterVariableSymbol("index", BuiltinTypes.U32, SourceSpan.Synthetic()),
                new ParameterVariableSymbol("ptr", pointerType, SourceSpan.Synthetic()),
                new ParameterVariableSymbol("flags", flagsType, SourceSpan.Synthetic()),
                new ParameterVariableSymbol("packed", packedType, SourceSpan.Synthetic()),
                new ParameterVariableSymbol("storeValue", BuiltinTypes.U32, SourceSpan.Synthetic()),
            ],
            ReturnSlots = [new ReturnSlot(packedType, ReturnPlacement.Register)],
        };

        MirValueId manyArg = MirValue(100);
        MirValueId indexArg = MirValue(101);
        MirValueId pointerArg = MirValue(102);
        MirValueId flagsArg = MirValue(103);
        MirValueId packedArg = MirValue(104);
        MirValueId storeValueArg = MirValue(105);

        MirBlock calleeBlock = new(
            MirBlockRef("callee_bb0"),
            [
                new MirBlockParameter(manyArg, "many", manyType),
                new MirBlockParameter(indexArg, "index", BuiltinTypes.U32),
                new MirBlockParameter(pointerArg, "ptr", pointerType),
                new MirBlockParameter(flagsArg, "flags", flagsType),
                new MirBlockParameter(packedArg, "packed", packedType),
                new MirBlockParameter(storeValueArg, "storeValue", BuiltinTypes.U32),
            ],
            [
                new MirLoadIndexInstruction(MirValue(106), BuiltinTypes.U32, manyType, manyArg, indexArg, AddressSpace.Cog, hasSideEffects: false, span),
                new MirLoadDerefInstruction(MirValue(107), BuiltinTypes.U16, pointerType, pointerArg, AddressSpace.Hub, hasSideEffects: false, span),
                new MirBitfieldExtractInstruction(MirValue(108), BuiltinTypes.Nib, flagsArg, bitfieldMember, span),
                new MirBitfieldInsertInstruction(MirValue(109), flagsType, flagsArg, MirValue(108), bitfieldMember, span),
                new MirInsertMemberInstruction(MirValue(110), packedType, packedArg, MirValue(107), structMember, span),
                new MirStoreIndexInstruction(BuiltinTypes.U32, manyType, manyArg, indexArg, storeValueArg, AddressSpace.Cog, span),
                new MirStoreDerefInstruction(BuiltinTypes.U16, pointerType, pointerArg, MirValue(107), AddressSpace.Hub, span),
            ],
            new MirReturnTerminator([MirValue(110)], span));
        MirFunction callee = new(calleeSymbol, isEntryPoint: false, [packedType], [calleeBlock], calleeSymbol.ReturnSlots);

        FunctionSymbol callerSymbol = new("caller", IrTestFactory.CreateFunctionDeclarationSyntax("caller"), FunctionKind.Default, isTopLevel: false, storageClass: null, FunctionInliningPolicy.Default, SourceSpan.Synthetic())
        {
            Parameters = calleeSymbol.Parameters,
            ReturnSlots = calleeSymbol.ReturnSlots,
        };
        MirBlock callerBlock = new(
            MirBlockRef("caller_bb0"),
            [
                new MirBlockParameter(MirValue(200), "many", manyType),
                new MirBlockParameter(MirValue(201), "index", BuiltinTypes.U32),
                new MirBlockParameter(MirValue(202), "ptr", pointerType),
                new MirBlockParameter(MirValue(203), "flags", flagsType),
                new MirBlockParameter(MirValue(204), "packed", packedType),
                new MirBlockParameter(MirValue(205), "storeValue", BuiltinTypes.U32),
            ],
            [
                new MirCallInstruction(
                    MirValue(206),
                    packedType,
                    calleeSymbol,
                    [MirValue(200), MirValue(201), MirValue(202), MirValue(203), MirValue(204), MirValue(205)],
                    span),
            ],
            new MirReturnTerminator([MirValue(206)], span));
        MirFunction caller = new(callerSymbol, isEntryPoint: true, [packedType], [callerBlock], callerSymbol.ReturnSlots);

        MirModule inlined = MirInliner.InlineMandatoryAndSingleCallsite(CreateMirModule(functions: [caller, callee]), enableSingleCallsiteInlining: true);
        MirFunction rewrittenCaller = inlined.Functions.Single(function => function.Symbol == callerSymbol);
        List<MirInstruction> instructions = rewrittenCaller.Blocks.SelectMany(static block => block.Instructions).ToList();

        Assert.That(instructions.OfType<MirCallInstruction>(), Is.Empty);
        Assert.That(instructions.OfType<MirLoadIndexInstruction>().Single().IndexedType, Is.SameAs(manyType));
        Assert.That(instructions.OfType<MirLoadDerefInstruction>().Single().PointerType, Is.SameAs(pointerType));
        Assert.That(instructions.OfType<MirBitfieldExtractInstruction>().Single().Member, Is.SameAs(bitfieldMember));
        Assert.That(instructions.OfType<MirBitfieldInsertInstruction>().Single().ResultType, Is.SameAs(flagsType));
        Assert.That(instructions.OfType<MirInsertMemberInstruction>().Single().ResultType, Is.SameAs(packedType));
        Assert.That(instructions.OfType<MirStoreIndexInstruction>().Single().IndexedType, Is.SameAs(manyType));
        Assert.That(instructions.OfType<MirStoreDerefInstruction>().Single().PointerType, Is.SameAs(pointerType));
    }

    private static void AssertIntegerConstant(IReadOnlyDictionary<MirValueId, BladeValue> constants, MirValueId id, long expected)
    {
        Assert.That(constants.TryGetValue(id, out BladeValue? value), Is.True);
        Assert.That(value!.TryGetInteger(out long actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static void AssertBoolConstant(IReadOnlyDictionary<MirValueId, BladeValue> constants, MirValueId id, bool expected)
    {
        Assert.That(constants.TryGetValue(id, out BladeValue? value), Is.True);
        Assert.That(value!.TryGetBool(out bool actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }
}
