using Blade.Semantics.Bound;
using Blade.Syntax;

namespace Blade.Tests;

[TestFixture]
public class BoundOperatorTests
{
    [Test]
    public void BoundUnaryOperator_Bind_CoversSupportedOperators()
    {
        Dictionary<TokenKind, BoundUnaryOperatorKind> expected = new()
        {
            [TokenKind.Bang] = BoundUnaryOperatorKind.LogicalNot,
            [TokenKind.Minus] = BoundUnaryOperatorKind.Negation,
            [TokenKind.Tilde] = BoundUnaryOperatorKind.BitwiseNot,
            [TokenKind.Plus] = BoundUnaryOperatorKind.UnaryPlus,
            [TokenKind.Ampersand] = BoundUnaryOperatorKind.AddressOf,
        };

        foreach (TokenKind kind in Enum.GetValues<TokenKind>())
        {
            BoundUnaryOperator? op = BoundUnaryOperator.Bind(kind);
            if (expected.TryGetValue(kind, out BoundUnaryOperatorKind expectedKind))
            {
                Assert.That(op, Is.Not.Null, $"Expected unary operator for {kind}");
                Assert.That(op!.SyntaxKind, Is.EqualTo(kind));
                Assert.That(op.Kind, Is.EqualTo(expectedKind));
            }
            else
            {
                Assert.That(op, Is.Null, $"Did not expect unary operator for {kind}");
            }
        }
    }

    [Test]
    public void BoundBinaryOperator_Bind_CoversSupportedOperators()
    {
        Dictionary<TokenKind, (BoundBinaryOperatorKind Kind, bool IsComparison)> expected = new()
        {
            [TokenKind.Plus] = (BoundBinaryOperatorKind.Add, false),
            [TokenKind.Minus] = (BoundBinaryOperatorKind.Subtract, false),
            [TokenKind.Star] = (BoundBinaryOperatorKind.Multiply, false),
            [TokenKind.Slash] = (BoundBinaryOperatorKind.Divide, false),
            [TokenKind.Percent] = (BoundBinaryOperatorKind.Modulo, false),
            [TokenKind.Ampersand] = (BoundBinaryOperatorKind.BitwiseAnd, false),
            [TokenKind.Pipe] = (BoundBinaryOperatorKind.BitwiseOr, false),
            [TokenKind.Caret] = (BoundBinaryOperatorKind.BitwiseXor, false),
            [TokenKind.LessLess] = (BoundBinaryOperatorKind.ShiftLeft, false),
            [TokenKind.GreaterGreater] = (BoundBinaryOperatorKind.ShiftRight, false),
            [TokenKind.LessLessLess] = (BoundBinaryOperatorKind.ArithmeticShiftLeft, false),
            [TokenKind.GreaterGreaterGreater] = (BoundBinaryOperatorKind.ArithmeticShiftRight, false),
            [TokenKind.RotateLeft] = (BoundBinaryOperatorKind.RotateLeft, false),
            [TokenKind.RotateRight] = (BoundBinaryOperatorKind.RotateRight, false),
            [TokenKind.AndKeyword] = (BoundBinaryOperatorKind.LogicalAnd, false),
            [TokenKind.OrKeyword] = (BoundBinaryOperatorKind.LogicalOr, false),
            [TokenKind.EqualEqual] = (BoundBinaryOperatorKind.Equals, true),
            [TokenKind.BangEqual] = (BoundBinaryOperatorKind.NotEquals, true),
            [TokenKind.Less] = (BoundBinaryOperatorKind.Less, true),
            [TokenKind.LessEqual] = (BoundBinaryOperatorKind.LessOrEqual, true),
            [TokenKind.Greater] = (BoundBinaryOperatorKind.Greater, true),
            [TokenKind.GreaterEqual] = (BoundBinaryOperatorKind.GreaterOrEqual, true),
        };

        foreach (TokenKind kind in Enum.GetValues<TokenKind>())
        {
            BoundBinaryOperator? op = BoundBinaryOperator.Bind(kind);
            if (expected.TryGetValue(kind, out (BoundBinaryOperatorKind Kind, bool IsComparison) expectedValue))
            {
                Assert.That(op, Is.Not.Null, $"Expected binary operator for {kind}");
                Assert.That(op!.SyntaxKind, Is.EqualTo(kind));
                Assert.That(op.Kind, Is.EqualTo(expectedValue.Kind));
                Assert.That(op.IsComparison, Is.EqualTo(expectedValue.IsComparison));
            }
            else
            {
                Assert.That(op, Is.Null, $"Did not expect binary operator for {kind}");
            }
        }
    }
}
