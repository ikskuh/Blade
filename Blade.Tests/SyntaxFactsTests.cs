using System;
using System.Collections.Generic;
using System.Linq;
using Blade.Syntax;

namespace Blade.Tests;

[TestFixture]
public class SyntaxFactsTests
{
    private static readonly IReadOnlyDictionary<TokenKind, string> FixedTokenText = new Dictionary<TokenKind, string>
    {
        [TokenKind.RegKeyword] = "reg",
        [TokenKind.LutKeyword] = "lut",
        [TokenKind.HubKeyword] = "hub",
        [TokenKind.ExternKeyword] = "extern",
        [TokenKind.ConstKeyword] = "const",
        [TokenKind.VarKeyword] = "var",
        [TokenKind.FnKeyword] = "fn",
        [TokenKind.LeafKeyword] = "leaf",
        [TokenKind.InlineKeyword] = "inline",
        [TokenKind.NoinlineKeyword] = "noinline",
        [TokenKind.RecKeyword] = "rec",
        [TokenKind.CoroKeyword] = "coro",
        [TokenKind.ComptimeKeyword] = "comptime",
        [TokenKind.Int1Keyword] = "int1",
        [TokenKind.Int2Keyword] = "int2",
        [TokenKind.Int3Keyword] = "int3",
        [TokenKind.IfKeyword] = "if",
        [TokenKind.ElseKeyword] = "else",
        [TokenKind.WhileKeyword] = "while",
        [TokenKind.ForKeyword] = "for",
        [TokenKind.LoopKeyword] = "loop",
        [TokenKind.BreakKeyword] = "break",
        [TokenKind.ContinueKeyword] = "continue",
        [TokenKind.ReturnKeyword] = "return",
        [TokenKind.YieldKeyword] = "yield",
        [TokenKind.YieldtoKeyword] = "yieldto",
        [TokenKind.RepKeyword] = "rep",
        [TokenKind.NoirqKeyword] = "noirq",
        [TokenKind.InKeyword] = "in",
        [TokenKind.ImportKeyword] = "import",
        [TokenKind.AsKeyword] = "as",
        [TokenKind.AsmKeyword] = "asm",
        [TokenKind.VolatileKeyword] = "volatile",
        [TokenKind.StructKeyword] = "struct",
        [TokenKind.UndefinedKeyword] = "undefined",
        [TokenKind.AlignKeyword] = "align",
        [TokenKind.VoidKeyword] = "void",
        [TokenKind.TrueKeyword] = "true",
        [TokenKind.FalseKeyword] = "false",
        [TokenKind.BoolKeyword] = "bool",
        [TokenKind.BitKeyword] = "bit",
        [TokenKind.NitKeyword] = "nit",
        [TokenKind.NibKeyword] = "nib",
        [TokenKind.U8Keyword] = "u8",
        [TokenKind.I8Keyword] = "i8",
        [TokenKind.U16Keyword] = "u16",
        [TokenKind.I16Keyword] = "i16",
        [TokenKind.U32Keyword] = "u32",
        [TokenKind.I32Keyword] = "i32",
        [TokenKind.UintKeyword] = "uint",
        [TokenKind.IntKeyword] = "int",
        [TokenKind.TypeKeyword] = "type",
        [TokenKind.UnionKeyword] = "union",
        [TokenKind.EnumKeyword] = "enum",
        [TokenKind.BitfieldKeyword] = "bitfield",
        [TokenKind.BitcastKeyword] = "bitcast",
        [TokenKind.AndKeyword] = "and",
        [TokenKind.OrKeyword] = "or",
        [TokenKind.U8x4Keyword] = "u8x4",
        [TokenKind.Plus] = "+",
        [TokenKind.Minus] = "-",
        [TokenKind.Star] = "*",
        [TokenKind.Slash] = "/",
        [TokenKind.Percent] = "%",
        [TokenKind.Tilde] = "~",
        [TokenKind.Ampersand] = "&",
        [TokenKind.Pipe] = "|",
        [TokenKind.Caret] = "^",
        [TokenKind.Bang] = "!",
        [TokenKind.Dot] = ".",
        [TokenKind.At] = "@",
        [TokenKind.Arrow] = "->",
        [TokenKind.DotDot] = "..",
        [TokenKind.DotDotDot] = "...",
        [TokenKind.PlusPlus] = "++",
        [TokenKind.MinusMinus] = "--",
        [TokenKind.LessLess] = "<<",
        [TokenKind.GreaterGreater] = ">>",
        [TokenKind.LessLessLess] = "<<<",
        [TokenKind.GreaterGreaterGreater] = ">>>",
        [TokenKind.RotateLeft] = "<%<",
        [TokenKind.RotateRight] = ">%>",
        [TokenKind.EqualEqual] = "==",
        [TokenKind.BangEqual] = "!=",
        [TokenKind.LessEqual] = "<=",
        [TokenKind.GreaterEqual] = ">=",
        [TokenKind.Less] = "<",
        [TokenKind.Greater] = ">",
        [TokenKind.Equal] = "=",
        [TokenKind.PlusEqual] = "+=",
        [TokenKind.MinusEqual] = "-=",
        [TokenKind.PercentEqual] = "%=",
        [TokenKind.AmpersandEqual] = "&=",
        [TokenKind.PipeEqual] = "|=",
        [TokenKind.CaretEqual] = "^=",
        [TokenKind.LessLessEqual] = "<<=",
        [TokenKind.GreaterGreaterEqual] = ">>=",
        [TokenKind.OpenParen] = "(",
        [TokenKind.CloseParen] = ")",
        [TokenKind.OpenBrace] = "{",
        [TokenKind.CloseBrace] = "}",
        [TokenKind.OpenBracket] = "[",
        [TokenKind.CloseBracket] = "]",
        [TokenKind.Semicolon] = ";",
        [TokenKind.Comma] = ",",
        [TokenKind.Colon] = ":",
    };

    private static readonly IReadOnlyDictionary<TokenKind, int> BinaryPrecedence = new Dictionary<TokenKind, int>
    {
        [TokenKind.Star] = 9,
        [TokenKind.Slash] = 9,
        [TokenKind.Percent] = 9,
        [TokenKind.Plus] = 8,
        [TokenKind.Minus] = 8,
        [TokenKind.LessLess] = 7,
        [TokenKind.GreaterGreater] = 7,
        [TokenKind.LessLessLess] = 7,
        [TokenKind.GreaterGreaterGreater] = 7,
        [TokenKind.RotateLeft] = 7,
        [TokenKind.RotateRight] = 7,
        [TokenKind.Ampersand] = 6,
        [TokenKind.Caret] = 5,
        [TokenKind.Pipe] = 4,
        [TokenKind.Less] = 3,
        [TokenKind.LessEqual] = 3,
        [TokenKind.Greater] = 3,
        [TokenKind.GreaterEqual] = 3,
        [TokenKind.EqualEqual] = 3,
        [TokenKind.BangEqual] = 3,
        [TokenKind.AndKeyword] = 2,
        [TokenKind.OrKeyword] = 1,
    };

    [Test]
    public void GetText_ReturnsExactFixedTextAndNullOtherwise()
    {
        foreach (TokenKind kind in Enum.GetValues<TokenKind>())
        {
            string? text = SyntaxFacts.GetText(kind);
            if (FixedTokenText.TryGetValue(kind, out string? expected))
                Assert.That(text, Is.EqualTo(expected), $"Unexpected text for {kind}");
            else
                Assert.That(text, Is.Null, $"Expected null text for {kind}");
        }
    }

    [Test]
    public void GetBinaryOperatorPrecedence_MatchesDefinitionForEveryTokenKind()
    {
        foreach (TokenKind kind in Enum.GetValues<TokenKind>())
        {
            int precedence = SyntaxFacts.GetBinaryOperatorPrecedence(kind);
            int expected = BinaryPrecedence.TryGetValue(kind, out int value) ? value : 0;
            Assert.That(precedence, Is.EqualTo(expected), $"Unexpected precedence for {kind}");
        }
    }

    [Test]
    public void GetUnaryOperatorPrecedence_MatchesDefinition()
    {
        Assert.That(SyntaxFacts.GetUnaryOperatorPrecedence(TokenKind.Bang), Is.EqualTo(10));
        Assert.That(SyntaxFacts.GetUnaryOperatorPrecedence(TokenKind.Minus), Is.EqualTo(10));
        Assert.That(SyntaxFacts.GetUnaryOperatorPrecedence(TokenKind.Star), Is.EqualTo(10));
        Assert.That(SyntaxFacts.GetUnaryOperatorPrecedence(TokenKind.Plus), Is.EqualTo(10));
        Assert.That(SyntaxFacts.GetUnaryOperatorPrecedence(TokenKind.Tilde), Is.EqualTo(10));
        Assert.That(SyntaxFacts.GetUnaryOperatorPrecedence(TokenKind.Ampersand), Is.EqualTo(10));
    }

    [Test]
    public void IsAssignmentOperator_MatchesDefinition()
    {
        TokenKind[] assignmentOperators =
        {
            TokenKind.Equal,
            TokenKind.PlusEqual,
            TokenKind.MinusEqual,
            TokenKind.PercentEqual,
            TokenKind.AmpersandEqual,
            TokenKind.PipeEqual,
            TokenKind.CaretEqual,
            TokenKind.LessLessEqual,
            TokenKind.GreaterGreaterEqual,
        };

        foreach (TokenKind kind in assignmentOperators)
            Assert.That(SyntaxFacts.IsAssignmentOperator(kind), Is.True, $"{kind} should be assignment");

        TokenKind[] nonAssignmentOperators =
        {
            TokenKind.EqualEqual,
            TokenKind.Plus,
            TokenKind.Identifier,
            TokenKind.EndOfFile,
        };

        foreach (TokenKind kind in nonAssignmentOperators)
            Assert.That(SyntaxFacts.IsAssignmentOperator(kind), Is.False, $"{kind} should not be assignment");
    }

    [Test]
    public void GetKeywordKind_RecognizesEveryKeyword()
    {
        foreach ((TokenKind kind, string text) in FixedTokenText.Where(pair => pair.Key.ToString().EndsWith("Keyword")))
            Assert.That(SyntaxFacts.GetKeywordKind(text), Is.EqualTo(kind));

        Assert.That(SyntaxFacts.GetKeywordKind("identifier"), Is.Null);
    }
}
