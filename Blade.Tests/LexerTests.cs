using Blade.Diagnostics;
using Blade.Source;
using Blade.Syntax;

namespace Blade.Tests;

[TestFixture]
public class LexerTests
{
    private static List<Token> Lex(string text)
    {
        SourceText source = new(text);
        DiagnosticBag diagnostics = new();
        Lexer lexer = new(source, diagnostics);
        List<Token> tokens = new();

        Token token;
        do
        {
            token = lexer.NextToken();
            tokens.Add(token);
        } while (token.Kind != TokenKind.EndOfFile);

        return tokens;
    }

    private static List<Token> LexWithDiagnostics(string text, out DiagnosticBag diagnostics)
    {
        SourceText source = new(text);
        diagnostics = new DiagnosticBag();
        Lexer lexer = new(source, diagnostics);
        List<Token> tokens = new();

        Token token;
        do
        {
            token = lexer.NextToken();
            tokens.Add(token);
        } while (token.Kind != TokenKind.EndOfFile);

        return tokens;
    }

    // --- Empty / EOF ---

    [Test]
    public void EmptyInput_ReturnsEndOfFile()
    {
        List<Token> tokens = Lex("");
        Assert.That(tokens, Has.Count.EqualTo(1));
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.EndOfFile));
    }

    [Test]
    public void WhitespaceOnly_ReturnsEndOfFile()
    {
        List<Token> tokens = Lex("   \t\r\n  ");
        Assert.That(tokens, Has.Count.EqualTo(1));
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.EndOfFile));
    }

    // --- Identifiers ---

    [Test]
    public void SimpleIdentifier()
    {
        List<Token> tokens = Lex("counter");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Text, Is.EqualTo("counter"));
    }

    [Test]
    public void IdentifierWithUnderscore()
    {
        List<Token> tokens = Lex("_my_var");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Text, Is.EqualTo("_my_var"));
    }

    [Test]
    public void IdentifierWithDigits()
    {
        List<Token> tokens = Lex("buf32");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Text, Is.EqualTo("buf32"));
    }

    [Test]
    public void IdentifierNotKeyword()
    {
        // "register" is not a keyword, just an identifier
        List<Token> tokens = Lex("register");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
    }

    // --- Keywords ---

    [TestCase("reg", TokenKind.RegKeyword)]
    [TestCase("lut", TokenKind.LutKeyword)]
    [TestCase("hub", TokenKind.HubKeyword)]
    [TestCase("extern", TokenKind.ExternKeyword)]
    [TestCase("const", TokenKind.ConstKeyword)]
    [TestCase("var", TokenKind.VarKeyword)]
    [TestCase("fn", TokenKind.FnKeyword)]
    [TestCase("leaf", TokenKind.LeafKeyword)]
    [TestCase("inline", TokenKind.InlineKeyword)]
    [TestCase("rec", TokenKind.RecKeyword)]
    [TestCase("coro", TokenKind.CoroKeyword)]
    [TestCase("comptime", TokenKind.ComptimeKeyword)]
    [TestCase("int1", TokenKind.Int1Keyword)]
    [TestCase("int2", TokenKind.Int2Keyword)]
    [TestCase("int3", TokenKind.Int3Keyword)]
    [TestCase("if", TokenKind.IfKeyword)]
    [TestCase("else", TokenKind.ElseKeyword)]
    [TestCase("while", TokenKind.WhileKeyword)]
    [TestCase("for", TokenKind.ForKeyword)]
    [TestCase("loop", TokenKind.LoopKeyword)]
    [TestCase("break", TokenKind.BreakKeyword)]
    [TestCase("continue", TokenKind.ContinueKeyword)]
    [TestCase("return", TokenKind.ReturnKeyword)]
    [TestCase("yield", TokenKind.YieldKeyword)]
    [TestCase("yieldto", TokenKind.YieldtoKeyword)]
    [TestCase("rep", TokenKind.RepKeyword)]
    [TestCase("noirq", TokenKind.NoirqKeyword)]
    [TestCase("in", TokenKind.InKeyword)]
    [TestCase("import", TokenKind.ImportKeyword)]
    [TestCase("as", TokenKind.AsKeyword)]
    [TestCase("asm", TokenKind.AsmKeyword)]
    [TestCase("volatile", TokenKind.VolatileKeyword)]
    [TestCase("packed", TokenKind.PackedKeyword)]
    [TestCase("struct", TokenKind.StructKeyword)]
    [TestCase("undefined", TokenKind.UndefinedKeyword)]
    [TestCase("align", TokenKind.AlignKeyword)]
    [TestCase("void", TokenKind.VoidKeyword)]
    [TestCase("true", TokenKind.TrueKeyword)]
    [TestCase("false", TokenKind.FalseKeyword)]
    [TestCase("bool", TokenKind.BoolKeyword)]
    [TestCase("bit", TokenKind.BitKeyword)]
    [TestCase("nit", TokenKind.NitKeyword)]
    [TestCase("nib", TokenKind.NibKeyword)]
    [TestCase("u8", TokenKind.U8Keyword)]
    [TestCase("i8", TokenKind.I8Keyword)]
    [TestCase("u16", TokenKind.U16Keyword)]
    [TestCase("i16", TokenKind.I16Keyword)]
    [TestCase("u32", TokenKind.U32Keyword)]
    [TestCase("i32", TokenKind.I32Keyword)]
    [TestCase("uint", TokenKind.UintKeyword)]
    [TestCase("int", TokenKind.IntKeyword)]
    [TestCase("type", TokenKind.TypeKeyword)]
    [TestCase("union", TokenKind.UnionKeyword)]
    [TestCase("enum", TokenKind.EnumKeyword)]
    [TestCase("bitfield", TokenKind.BitfieldKeyword)]
    [TestCase("bitcast", TokenKind.BitcastKeyword)]
    [TestCase("and", TokenKind.AndKeyword)]
    [TestCase("or", TokenKind.OrKeyword)]
    [TestCase("u8x4", TokenKind.U8x4Keyword)]
    public void Keywords(string text, TokenKind expectedKind)
    {
        List<Token> tokens = Lex(text);
        Assert.That(tokens[0].Kind, Is.EqualTo(expectedKind));
        Assert.That(tokens[0].Text, Is.EqualTo(text));
    }

    // --- Integer Literals ---

    [Test]
    public void DecimalInteger()
    {
        List<Token> tokens = Lex("42");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(42L));
    }

    [Test]
    public void DecimalIntegerWithUnderscores()
    {
        List<Token> tokens = Lex("180_000_000");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(180_000_000L));
    }

    [Test]
    public void HexInteger()
    {
        List<Token> tokens = Lex("0xFF00FF00");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0xFF00FF00L));
    }

    [Test]
    public void HexIntegerLowercase()
    {
        List<Token> tokens = Lex("0xff");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0xFFL));
    }

    [Test]
    public void BinaryInteger()
    {
        List<Token> tokens = Lex("0b1010");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(10L));
    }

    [Test]
    public void BinaryIntegerWithUnderscores()
    {
        List<Token> tokens = Lex("0b1111_0000");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0xF0L));
    }

    [Test]
    public void ZeroLiteral()
    {
        List<Token> tokens = Lex("0");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0L));
    }

    [Test]
    public void DecimalIntegerOverflow_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("9223372036854775808", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0L));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [Test]
    public void HexIntegerWithoutDigits_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0x", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0L));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [Test]
    public void HexIntegerOverflow_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0x1_0000_0000_0000_0000", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0L));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [Test]
    public void BinaryIntegerWithoutDigits_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0b", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0L));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [Test]
    public void BinaryIntegerOverflow_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics($"0b{new string('1', 65)}", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(0L));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    // --- String Literals ---

    [Test]
    public void SimpleString()
    {
        List<Token> tokens = Lex("\"hello\"");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo("hello"));
    }

    [Test]
    public void EmptyString()
    {
        List<Token> tokens = Lex("\"\"");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(""));
    }

    [Test]
    public void UnterminatedString_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("\"hello", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [Test]
    public void UnterminatedString_AtNewline_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("\"hello\nworld", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [Test]
    public void UnterminatedString_AtCarriageReturn_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("\"hello\rworld", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    // --- Operators ---

    [TestCase("+", TokenKind.Plus)]
    [TestCase("-", TokenKind.Minus)]
    [TestCase("*", TokenKind.Star)]
    [TestCase("/", TokenKind.Slash)]
    [TestCase("&", TokenKind.Ampersand)]
    [TestCase("|", TokenKind.Pipe)]
    [TestCase("^", TokenKind.Caret)]
    [TestCase("!", TokenKind.Bang)]
    [TestCase(".", TokenKind.Dot)]
    [TestCase("@", TokenKind.At)]
    [TestCase("->", TokenKind.Arrow)]
    [TestCase("..", TokenKind.DotDot)]
    [TestCase("++", TokenKind.PlusPlus)]
    [TestCase("--", TokenKind.MinusMinus)]
    [TestCase("<<", TokenKind.LessLess)]
    [TestCase(">>", TokenKind.GreaterGreater)]
    [TestCase("==", TokenKind.EqualEqual)]
    [TestCase("!=", TokenKind.BangEqual)]
    [TestCase("<=", TokenKind.LessEqual)]
    [TestCase(">=", TokenKind.GreaterEqual)]
    [TestCase("<", TokenKind.Less)]
    [TestCase(">", TokenKind.Greater)]
    [TestCase("=", TokenKind.Equal)]
    [TestCase("+=", TokenKind.PlusEqual)]
    [TestCase("-=", TokenKind.MinusEqual)]
    [TestCase("&=", TokenKind.AmpersandEqual)]
    [TestCase("|=", TokenKind.PipeEqual)]
    [TestCase("^=", TokenKind.CaretEqual)]
    [TestCase("<<=", TokenKind.LessLessEqual)]
    [TestCase(">>=", TokenKind.GreaterGreaterEqual)]
    [TestCase("~", TokenKind.Tilde)]
    [TestCase("%", TokenKind.Percent)]
    [TestCase("%=", TokenKind.PercentEqual)]
    [TestCase("...", TokenKind.DotDotDot)]
    [TestCase("<<<", TokenKind.LessLessLess)]
    [TestCase(">>>", TokenKind.GreaterGreaterGreater)]
    [TestCase("<%<", TokenKind.RotateLeft)]
    [TestCase(">%>", TokenKind.RotateRight)]
    public void Operators(string text, TokenKind expectedKind)
    {
        List<Token> tokens = Lex(text);
        Assert.That(tokens[0].Kind, Is.EqualTo(expectedKind));
        Assert.That(tokens[0].Text, Is.EqualTo(text));
    }

    // --- Delimiters ---

    [TestCase("(", TokenKind.OpenParen)]
    [TestCase(")", TokenKind.CloseParen)]
    [TestCase("{", TokenKind.OpenBrace)]
    [TestCase("}", TokenKind.CloseBrace)]
    [TestCase("[", TokenKind.OpenBracket)]
    [TestCase("]", TokenKind.CloseBracket)]
    [TestCase(";", TokenKind.Semicolon)]
    [TestCase(",", TokenKind.Comma)]
    [TestCase(":", TokenKind.Colon)]
    public void Delimiters(string text, TokenKind expectedKind)
    {
        List<Token> tokens = Lex(text);
        Assert.That(tokens[0].Kind, Is.EqualTo(expectedKind));
    }

    // --- Comments ---

    [Test]
    public void LineComment_IsSkipped()
    {
        List<Token> tokens = Lex("// this is a comment\n42");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(42L));
    }

    [Test]
    public void BlockComment_IsSkipped()
    {
        List<Token> tokens = Lex("/* comment */ 42");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo(42L));
    }

    [Test]
    public void NestedBlockComment_IsSkipped()
    {
        List<Token> tokens = Lex("/* outer /* inner */ still comment */ 42");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
    }

    [Test]
    public void UnterminatedBlockComment_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("/* unterminated", out DiagnosticBag diagnostics);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    // --- @ token ---

    [Test]
    public void AtIntrinsic_TokenizedAsSeparateTokens()
    {
        List<Token> tokens = Lex("@fle");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.At));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Text, Is.EqualTo("fle"));
    }

    [Test]
    public void AtFlagAnnotation_TokenizedAsSeparateTokens()
    {
        List<Token> tokens = Lex("@C");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.At));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Text, Is.EqualTo("C"));
    }

    // --- Error recovery ---

    [Test]
    public void UnexpectedCharacter_EmitsBadToken_AndContinues()
    {
        List<Token> tokens = LexWithDiagnostics("42 ` 7", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Bad));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    // --- Spans ---

    [Test]
    public void TokenSpans_AreCorrect()
    {
        List<Token> tokens = Lex("fn foo");
        Assert.That(tokens[0].Span.Start, Is.EqualTo(0));
        Assert.That(tokens[0].Span.Length, Is.EqualTo(2));
        Assert.That(tokens[1].Span.Start, Is.EqualTo(3));
        Assert.That(tokens[1].Span.Length, Is.EqualTo(3));
    }

    // --- Full snippet ---

    [Test]
    public void LexVariableDeclaration()
    {
        List<Token> tokens = Lex("reg var counter: u32 = 0;");
        TokenKind[] expected = new[]
        {
            TokenKind.RegKeyword,
            TokenKind.VarKeyword,
            TokenKind.Identifier,   // counter
            TokenKind.Colon,
            TokenKind.U32Keyword,
            TokenKind.Equal,
            TokenKind.IntegerLiteral,
            TokenKind.Semicolon,
            TokenKind.EndOfFile,
        };

        Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void LexFunctionSignature()
    {
        List<Token> tokens = Lex("fn square(x: u32) -> u32 {");
        TokenKind[] expected = new[]
        {
            TokenKind.FnKeyword,
            TokenKind.Identifier,   // square
            TokenKind.OpenParen,
            TokenKind.Identifier,   // x
            TokenKind.Colon,
            TokenKind.U32Keyword,
            TokenKind.CloseParen,
            TokenKind.Arrow,
            TokenKind.U32Keyword,
            TokenKind.OpenBrace,
            TokenKind.EndOfFile,
        };

        Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void LexRepLoop()
    {
        List<Token> tokens = Lex("rep loop (32) { }");
        TokenKind[] expected = new[]
        {
            TokenKind.RepKeyword,
            TokenKind.LoopKeyword,
            TokenKind.OpenParen,
            TokenKind.IntegerLiteral,
            TokenKind.CloseParen,
            TokenKind.OpenBrace,
            TokenKind.CloseBrace,
            TokenKind.EndOfFile,
        };

        Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void LexIntrinsicCall()
    {
        List<Token> tokens = Lex("@encod(value)");
        TokenKind[] expected = new[]
        {
            TokenKind.At,
            TokenKind.Identifier,   // encod
            TokenKind.OpenParen,
            TokenKind.Identifier,   // value
            TokenKind.CloseParen,
            TokenKind.EndOfFile,
        };

        Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void LexCompoundAssignment()
    {
        List<Token> tokens = Lex("counter += 1;");
        TokenKind[] expected = new[]
        {
            TokenKind.Identifier,
            TokenKind.PlusEqual,
            TokenKind.IntegerLiteral,
            TokenKind.Semicolon,
            TokenKind.EndOfFile,
        };

        Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(expected));
    }
}
