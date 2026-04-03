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
    private static BladeValue Value(TypeSymbol type, object value)
    {
        object canonicalValue = CanonicalizeValue(type, value);
        return type switch
        {
            RuntimeTypeSymbol runtimeType => new RuntimeBladeValue(runtimeType, canonicalValue),
            ComptimeTypeSymbol comptimeType => new ComptimeBladeValue(comptimeType, canonicalValue),
            _ => throw new System.InvalidOperationException($"Unsupported MIR test constant type '{type.Name}'."),
        };
    }

    private static object CanonicalizeValue(TypeSymbol type, object value)
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

    private static MirConstantInstruction Constant(MirValueId result, TypeSymbol type, object value, TextSpan span) => new(result, type, Value(type, value), span);

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

        List<MirInstruction> instructions =
        [
            Constant(intOne, BuiltinTypes.U32, 1u, span),
            Constant(intTwo, BuiltinTypes.U32, 2u, span),
            Constant(intSeven, BuiltinTypes.U32, 7u, span),
            Constant(intEight, BuiltinTypes.U32, 8u, span),
            Constant(zero, BuiltinTypes.U32, 0u, span),
            Constant(boolTrue, BuiltinTypes.Bool, true, span),
            Constant(boolFalse, BuiltinTypes.Bool, false, span),
            Constant(stringLeft, BuiltinTypes.String, "left", span),
            Constant(stringRight, BuiltinTypes.String, "right", span),
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
        MirModule optimized = MirOptimizer.Optimize(new MirModule([function]), maxIterations: 1, enabledOptimizations: [OptimizationRegistry.GetMirOptimization("const-prop")!]);

        IReadOnlyList<MirInstruction> rewritten = optimized.Functions[0].Blocks[0].Instructions;
        Dictionary<MirValueId, object?> constants = rewritten
            .OfType<MirConstantInstruction>()
            .ToDictionary(instruction => instruction.Result!, instruction => instruction.Value?.Value);

        Assert.That(rewritten[13], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(rewritten[40], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(rewritten[41], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(rewritten[^1], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(constants[MirValue(10)], Is.EqualTo(true));
        Assert.That(constants[MirValue(11)], Is.EqualTo(-1L));
        Assert.That(constants[MirValue(12)], Is.EqualTo(0xFFFF_FFFEL));
        Assert.That(constants[MirValue(13)], Is.EqualTo(2L));
        Assert.That(constants[MirValue(20)], Is.EqualTo(3L));
        Assert.That(constants[MirValue(21)], Is.EqualTo(1L));
        Assert.That(constants[MirValue(22)], Is.EqualTo(4L));
        Assert.That(constants[MirValue(23)], Is.EqualTo(4L));
        Assert.That(constants.ContainsKey(MirValue(24)), Is.False);
        Assert.That(constants[MirValue(25)], Is.EqualTo(1L));
        Assert.That(constants.ContainsKey(MirValue(26)), Is.False);
        Assert.That(constants[MirValue(27)], Is.EqualTo(2L));
        Assert.That(constants[MirValue(28)], Is.EqualTo(3L));
        Assert.That(constants[MirValue(29)], Is.EqualTo(5L));
        Assert.That(constants[MirValue(31)], Is.EqualTo(4L));
        Assert.That(constants[MirValue(32)], Is.EqualTo(4L));
        Assert.That(constants[MirValue(33)], Is.EqualTo(4L));
        Assert.That(constants[MirValue(34)], Is.EqualTo(4L));
        Assert.That(constants[MirValue(35)], Is.EqualTo(4L));
        Assert.That(constants[MirValue(36)], Is.EqualTo(4L));
        Assert.That(constants[MirValue(37)], Is.EqualTo(true));
        Assert.That(constants[MirValue(38)], Is.EqualTo(true));
        Assert.That(constants[MirValue(39)], Is.EqualTo(true));
        Assert.That(constants[MirValue(40)], Is.EqualTo(true));
        Assert.That(constants[MirValue(41)], Is.EqualTo(true));
        Assert.That(constants[MirValue(42)], Is.EqualTo(true));
        Assert.That(constants[MirValue(43)], Is.EqualTo(false));
        Assert.That(constants[MirValue(44)], Is.EqualTo(true));
        Assert.That(constants[MirValue(45)], Is.EqualTo(false));
        Assert.That(constants[MirValue(46)], Is.EqualTo(true));
    }
}
