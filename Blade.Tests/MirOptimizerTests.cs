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
    [Test]
    public void ConstantPropagation_FoldsUnaryAndBinaryOperators()
    {
        TextSpan span = new(0, 0);

        MirValueId intOne = new(1);
        MirValueId intTwo = new(2);
        MirValueId intSeven = new(3);
        MirValueId intEight = new(4);
        MirValueId boolTrue = new(5);
        MirValueId boolFalse = new(6);
        MirValueId stringLeft = new(7);
        MirValueId stringRight = new(8);
        MirValueId zero = new(30);

        List<MirInstruction> instructions =
        [
            new MirConstantInstruction(intOne, BuiltinTypes.U32, 1L, span),
            new MirConstantInstruction(intTwo, BuiltinTypes.U32, 2L, span),
            new MirConstantInstruction(intSeven, BuiltinTypes.U32, 7L, span),
            new MirConstantInstruction(intEight, BuiltinTypes.U32, 8L, span),
            new MirConstantInstruction(zero, BuiltinTypes.U32, 0L, span),
            new MirConstantInstruction(boolTrue, BuiltinTypes.Bool, true, span),
            new MirConstantInstruction(boolFalse, BuiltinTypes.Bool, false, span),
            new MirConstantInstruction(stringLeft, BuiltinTypes.String, "left", span),
            new MirConstantInstruction(stringRight, BuiltinTypes.String, "right", span),
            new MirUnaryInstruction(new MirValueId(10), BuiltinTypes.Bool, BoundUnaryOperatorKind.LogicalNot, boolFalse, span),
            new MirUnaryInstruction(new MirValueId(11), BuiltinTypes.I32, BoundUnaryOperatorKind.Negation, intOne, span),
            new MirUnaryInstruction(new MirValueId(12), BuiltinTypes.U32, BoundUnaryOperatorKind.BitwiseNot, intOne, span),
            new MirUnaryInstruction(new MirValueId(13), BuiltinTypes.U32, BoundUnaryOperatorKind.UnaryPlus, intTwo, span),
            new MirUnaryInstruction(new MirValueId(14), BuiltinTypes.Bool, BoundUnaryOperatorKind.LogicalNot, stringLeft, span),
            new MirBinaryInstruction(new MirValueId(20), BuiltinTypes.U32, BoundBinaryOperatorKind.Add, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(21), BuiltinTypes.U32, BoundBinaryOperatorKind.Subtract, intTwo, intOne, span),
            new MirBinaryInstruction(new MirValueId(22), BuiltinTypes.U32, BoundBinaryOperatorKind.Multiply, intTwo, intTwo, span),
            new MirBinaryInstruction(new MirValueId(23), BuiltinTypes.U32, BoundBinaryOperatorKind.Divide, intEight, intTwo, span),
            new MirBinaryInstruction(new MirValueId(24), BuiltinTypes.U32, BoundBinaryOperatorKind.Divide, intEight, zero, span),
            new MirBinaryInstruction(new MirValueId(25), BuiltinTypes.U32, BoundBinaryOperatorKind.Modulo, intSeven, intTwo, span),
            new MirBinaryInstruction(new MirValueId(26), BuiltinTypes.U32, BoundBinaryOperatorKind.Modulo, intSeven, zero, span),
            new MirBinaryInstruction(new MirValueId(27), BuiltinTypes.U32, BoundBinaryOperatorKind.BitwiseAnd, intSeven, intTwo, span),
            new MirBinaryInstruction(new MirValueId(28), BuiltinTypes.U32, BoundBinaryOperatorKind.BitwiseOr, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(29), BuiltinTypes.U32, BoundBinaryOperatorKind.BitwiseXor, intSeven, intTwo, span),
            new MirBinaryInstruction(new MirValueId(31), BuiltinTypes.U32, BoundBinaryOperatorKind.ShiftLeft, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(32), BuiltinTypes.U32, BoundBinaryOperatorKind.ShiftRight, intEight, intOne, span),
            new MirBinaryInstruction(new MirValueId(33), BuiltinTypes.U32, BoundBinaryOperatorKind.ArithmeticShiftLeft, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(34), BuiltinTypes.U32, BoundBinaryOperatorKind.ArithmeticShiftRight, intEight, intOne, span),
            new MirBinaryInstruction(new MirValueId(35), BuiltinTypes.U32, BoundBinaryOperatorKind.RotateLeft, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(36), BuiltinTypes.U32, BoundBinaryOperatorKind.RotateRight, intEight, intOne, span),
            new MirBinaryInstruction(new MirValueId(37), BuiltinTypes.Bool, BoundBinaryOperatorKind.Equals, intOne, intOne, span),
            new MirBinaryInstruction(new MirValueId(38), BuiltinTypes.Bool, BoundBinaryOperatorKind.NotEquals, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(39), BuiltinTypes.Bool, BoundBinaryOperatorKind.Less, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(40), BuiltinTypes.Bool, BoundBinaryOperatorKind.LessOrEqual, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(41), BuiltinTypes.Bool, BoundBinaryOperatorKind.Greater, intTwo, intOne, span),
            new MirBinaryInstruction(new MirValueId(42), BuiltinTypes.Bool, BoundBinaryOperatorKind.GreaterOrEqual, intTwo, intOne, span),
            new MirBinaryInstruction(new MirValueId(43), BuiltinTypes.Bool, BoundBinaryOperatorKind.Equals, boolTrue, boolFalse, span),
            new MirBinaryInstruction(new MirValueId(44), BuiltinTypes.Bool, BoundBinaryOperatorKind.NotEquals, boolTrue, boolFalse, span),
            new MirBinaryInstruction(new MirValueId(45), BuiltinTypes.Bool, BoundBinaryOperatorKind.LogicalAnd, boolTrue, boolFalse, span),
            new MirBinaryInstruction(new MirValueId(46), BuiltinTypes.Bool, BoundBinaryOperatorKind.LogicalOr, boolTrue, boolFalse, span),
            new MirBinaryInstruction(new MirValueId(47), BuiltinTypes.Bool, BoundBinaryOperatorKind.LogicalAnd, stringLeft, stringRight, span),
            new MirBinaryInstruction(new MirValueId(48), BuiltinTypes.Bool, BoundBinaryOperatorKind.LogicalAnd, intOne, intTwo, span),
            new MirBinaryInstruction(new MirValueId(49), BuiltinTypes.Bool, BoundBinaryOperatorKind.Add, boolTrue, boolFalse, span),
        ];

        MirBlock block = new("bb0", [], instructions, new MirReturnTerminator([], span));
        MirFunction function = CreateMirFunction("test", isEntryPoint: true, FunctionKind.Default, [], [block]);
        MirModule optimized = MirOptimizer.Optimize(new MirModule([function]), maxIterations: 1, enabledOptimizations: [OptimizationRegistry.GetMirOptimization("const-prop")!]);

        IReadOnlyList<MirInstruction> rewritten = optimized.Functions[0].Blocks[0].Instructions;
        Dictionary<int, object?> constants = rewritten
            .OfType<MirConstantInstruction>()
            .ToDictionary(instruction => instruction.Result!.Value.Id, instruction => instruction.Value);

        Assert.That(rewritten[13], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(rewritten[^1], Is.Not.TypeOf<MirConstantInstruction>());
        Assert.That(constants[10], Is.EqualTo(true));
        Assert.That(constants[11], Is.EqualTo(-1L));
        Assert.That(constants[12], Is.EqualTo(~1L));
        Assert.That(constants[13], Is.EqualTo(2L));
        Assert.That(constants[20], Is.EqualTo(3L));
        Assert.That(constants[21], Is.EqualTo(1L));
        Assert.That(constants[22], Is.EqualTo(4L));
        Assert.That(constants[23], Is.EqualTo(4L));
        Assert.That(constants[24], Is.EqualTo(8L));
        Assert.That(constants[25], Is.EqualTo(1L));
        Assert.That(constants[26], Is.EqualTo(7L));
        Assert.That(constants[27], Is.EqualTo(2L));
        Assert.That(constants[28], Is.EqualTo(3L));
        Assert.That(constants[29], Is.EqualTo(5L));
        Assert.That(constants[31], Is.EqualTo(4L));
        Assert.That(constants[32], Is.EqualTo(4L));
        Assert.That(constants[33], Is.EqualTo(4L));
        Assert.That(constants[34], Is.EqualTo(4L));
        Assert.That(constants[35], Is.EqualTo(4L));
        Assert.That(constants[36], Is.EqualTo(4L));
        Assert.That(constants[37], Is.EqualTo(true));
        Assert.That(constants[38], Is.EqualTo(true));
        Assert.That(constants[39], Is.EqualTo(true));
        Assert.That(constants[40], Is.EqualTo(true));
        Assert.That(constants[41], Is.EqualTo(true));
        Assert.That(constants[42], Is.EqualTo(true));
        Assert.That(constants[43], Is.EqualTo(false));
        Assert.That(constants[44], Is.EqualTo(true));
        Assert.That(constants[45], Is.EqualTo(false));
        Assert.That(constants[46], Is.EqualTo(true));
        Assert.That(constants[48], Is.EqualTo(true));
    }
}
