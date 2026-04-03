using System.Reflection;
using Blade.Diagnostics;
using Blade.Source;
using Blade.Semantics;
using Blade.Syntax;

namespace Blade.Tests;

[TestFixture]
public class LexerTests
{


    [Test]
    public void Lexer_DiagnosticsProperty_ReturnsProvidedBag()
    {
        SourceText source = new("x");
        DiagnosticBag diagnostics = new();
        Lexer lexer = new(source, diagnostics);

        Assert.That(lexer.Diagnostics, Is.SameAs(diagnostics));
    }
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

    private static void AssertBooleanValue(Token token, bool expected)
    {
        Assert.That(token.Value, Is.Not.Null);
        Assert.That(token.Value!.TryGetBool(out bool actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static void AssertIntegerValue(Token token, long expected)
    {
        Assert.That(token.Value, Is.Not.Null);
        Assert.That(token.Value!.TryGetInteger(out long actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static void AssertStringValue(Token token, string expected)
    {
        Assert.That(token.Value, Is.Not.Null);
        Assert.That(token.Value!.TryGetString(out string actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
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
    [TestCase("assert", TokenKind.AssertKeyword)]
    [TestCase("in", TokenKind.InKeyword)]
    [TestCase("import", TokenKind.ImportKeyword)]
    [TestCase("as", TokenKind.AsKeyword)]
    [TestCase("asm", TokenKind.AsmKeyword)]
    [TestCase("volatile", TokenKind.VolatileKeyword)]
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

    [TestCase("true", true)]
    [TestCase("false", false)]
    public void BooleanKeywords_PreserveLiteralValue(string text, bool expectedValue)
    {
        List<Token> tokens = Lex(text);

        AssertBooleanValue(tokens[0], expectedValue);
    }

    [Test]
    public void Packed_LexesAsIdentifier()
    {
        List<Token> tokens = Lex("packed");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Text, Is.EqualTo("packed"));
    }

    // --- Integer Literals ---

    [Test]
    public void DecimalInteger()
    {
        List<Token> tokens = Lex("42");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 42L);
    }

    [Test]
    public void DecimalIntegerWithUnderscores()
    {
        List<Token> tokens = Lex("180_000_000");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 180_000_000L);
    }

    [Test]
    public void HexInteger()
    {
        List<Token> tokens = Lex("0xFF00FF00");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0xFF00FF00L);
    }

    [Test]
    public void HexIntegerLowercase()
    {
        List<Token> tokens = Lex("0xff");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0xFFL);
    }

    [Test]
    public void BinaryInteger()
    {
        List<Token> tokens = Lex("0b1010");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 10L);
    }

    [Test]
    public void BinaryIntegerWithUnderscores()
    {
        List<Token> tokens = Lex("0b1111_0000");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0xF0L);
    }

    [Test]
    public void ZeroLiteral()
    {
        List<Token> tokens = Lex("0");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
    }

    [Test]
    public void DecimalIntegerOverflow_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("9223372036854775808", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0003_InvalidNumberLiteral));
    }

    [Test]
    public void HexIntegerWithoutDigits_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0x", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [Test]
    public void HexIntegerOverflow_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0x1_0000_0000_0000_0000", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0003_InvalidNumberLiteral));
    }

    [Test]
    public void BinaryIntegerWithoutDigits_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0b", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [Test]
    public void BinaryIntegerOverflow_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics($"0b{new string('1', 65)}", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
    }

    [TestCase("0b1000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0q20000000000000000000000000000000")]
    [TestCase("0o1000000000000000000000")]
    public void NonDecimalIntegerOverflow_ReportsInvalidNumberLiteral(string text)
    {
        List<Token> tokens = LexWithDiagnostics(text, out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0003_InvalidNumberLiteral));
    }

    [Test]
    public void QuaternaryInteger()
    {
        List<Token> tokens = Lex("0q123");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 27L);
    }

    [Test]
    public void OctalInteger()
    {
        List<Token> tokens = Lex("0o17");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 15L);
    }

    [Test]
    public void QuaternaryIntegerWithoutDigits_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0q", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0003_InvalidNumberLiteral));
    }

    [Test]
    public void QuaternaryIntegerWithOutOfRadixDigits_StaysSingleTokenAndReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0q123_456", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Text, Is.EqualTo("0q123_456"));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(tokens, Has.Count.EqualTo(2));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0003_InvalidNumberLiteral));
    }

    [Test]
    public void OctalIntegerWithoutDigits_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("0o", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0003_InvalidNumberLiteral));
    }

    [TestCase("123_")]
    [TestCase("1__23")]
    [TestCase("0x_112")]
    [TestCase("0o8")]
    public void InvalidIntegerSeparatorsOrDigits_ReportInvalidNumberLiteral(string text)
    {
        List<Token> tokens = LexWithDiagnostics(text, out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(tokens[0].Text, Is.EqualTo(text));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0003_InvalidNumberLiteral));
    }

    [Test]
    public void CharLiteral_SimpleCharacter()
    {
        List<Token> tokens = Lex("'A'");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.CharLiteral));
        AssertIntegerValue(tokens[0], (long)'A');
    }

    [Test]
    public void CharLiteral_WithEscapeSequence()
    {
        List<Token> tokens = Lex("'\n'");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.CharLiteral));
        AssertIntegerValue(tokens[0], 10L);
    }

    [Test]
    public void CharLiteral_InvalidTooManyCharacters_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("'ab'", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.CharLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0005_InvalidCharacterLiteral));
    }

    [Test]
    public void CharLiteral_Unterminated_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("'", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.CharLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0002_UnterminatedString));
    }

    [TestCase("'a\n")]
    [TestCase("'a\r")]
    [TestCase("'a")]
    public void CharLiteral_MissingClosingQuoteAcrossNewlineOrEof_ReportsInvalidCharacterLiteral(string text)
    {
        List<Token> tokens = LexWithDiagnostics(text, out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.CharLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0005_InvalidCharacterLiteral));
    }

    [TestCase("'\\x\n")]
    [TestCase("'\\x")]
    public void CharLiteral_MalformedEscapeAcrossNewlineOrEof_ReportsEscapeAndCharacterLiteralDiagnostics(string text)
    {
        List<Token> tokens = LexWithDiagnostics(text, out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.CharLiteral));
        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[]
        {
            DiagnosticCode.E0006_InvalidEscapeSequence,
            DiagnosticCode.E0005_InvalidCharacterLiteral,
        }));
    }

    [Test]
    public void StringLiteral_WithUnicodeEscape()
    {
        List<Token> tokens = Lex("\"" + "\\u{41}\\x42" + "\"");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        AssertStringValue(tokens[0], "AB");
    }

    [Test]
    public void StringLiteral_InvalidEscape_ReportsDiagnostic()
    {
        List<Token> tokens = LexWithDiagnostics("\"" + "\\q" + "\"", out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0006_InvalidEscapeSequence));
    }

    [TestCase("\"\\u{}\"")]
    [TestCase("\"\\u{1234567}\"")]
    [TestCase("\"\\u{41\"")]
    [TestCase("\"\\u{4G}\"")]
    public void StringLiteral_MalformedUnicodeBraceEscape_ReportsInvalidEscape(string text)
    {
        List<Token> tokens = LexWithDiagnostics(text, out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0006_InvalidEscapeSequence));
    }

    [TestCase("\"\\xF\"", "F")]
    [TestCase("\"\\x4z\"", "4z")]
    [TestCase("\"\\xG1\"", "G1")]
    public void StringLiteral_MalformedHexEscape_ReportsInvalidEscapeAndRetainsTrailingText(string text, string expectedValue)
    {
        List<Token> tokens = LexWithDiagnostics(text, out DiagnosticBag diagnostics);
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        AssertStringValue(tokens[0], expectedValue);
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0006_InvalidEscapeSequence));
    }

    [Test]
    public void StringLiteral_SimpleEscapes_AreDecoded()
    {
        List<Token> tokens = Lex("\"" + "\\0\\t\\n\\r\\e\\\\\\'\\\"" + "\"");
        string expectedValue = "\0\t\n\r" + ((char)0x1B) + "\\'\"";

        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        AssertStringValue(tokens[0], expectedValue);
    }

    [Test]
    public void StringLiteral_UnicodeEscapeAboveAscii_UsesUtf32Path()
    {
        List<Token> tokens = Lex("\"" + "\\u{80}" + "\"");

        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        AssertStringValue(tokens[0], "\u0080");
    }

    [TestCase("\"\\u{110000}\"")]
    [TestCase("\"\\u{D800}\"")]
    public void StringLiteral_InvalidUnicodeScalarEscape_ReportsInvalidEscape(string text)
    {
        List<Token> tokens = LexWithDiagnostics(text, out DiagnosticBag diagnostics);

        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        AssertStringValue(tokens[0], string.Empty);
        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo([DiagnosticCode.E0006_InvalidEscapeSequence]));
    }

    [TestCase("'\\u{110000}'")]
    [TestCase("'\\u{D800}'")]
    public void CharLiteral_InvalidUnicodeScalarEscape_ReportsInvalidEscapeAndInvalidCharacterLiteral(string text)
    {
        List<Token> tokens = LexWithDiagnostics(text, out DiagnosticBag diagnostics);

        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.CharLiteral));
        AssertIntegerValue(tokens[0], 0L);
        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo([DiagnosticCode.E0006_InvalidEscapeSequence]));
    }

    [Test]
    public void StringLiteral_UnicodeEscapeWithoutOpeningBrace_ReportsInvalidEscape()
    {
        List<Token> tokens = LexWithDiagnostics("\"" + "\\u41" + "\"", out DiagnosticBag diagnostics);

        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        AssertStringValue(tokens[0], "41");
        Assert.That(diagnostics.Count, Is.EqualTo(1));
        Assert.That(diagnostics.Single().Code, Is.EqualTo(DiagnosticCode.E0006_InvalidEscapeSequence));
    }

    [Test]
    public void ZeroTerminatedString_IsLexedAsZeroTerminatedStringLiteral()
    {
        List<Token> tokens = Lex("z\"ok\"");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.ZeroTerminatedStringLiteral));
        AssertStringValue(tokens[0], "ok\0");
    }

    // --- String Literals ---

    [Test]
    public void SimpleString()
    {
        List<Token> tokens = Lex("\"hello\"");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        AssertStringValue(tokens[0], "hello");
    }

    [Test]
    public void EmptyString()
    {
        List<Token> tokens = Lex("\"\"");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        AssertStringValue(tokens[0], "");
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
    [TestCase("#", TokenKind.Hash)]
    [TestCase("->", TokenKind.Arrow)]
    [TestCase("..", TokenKind.DotDot)]
    [TestCase("..=", TokenKind.DotDotEqual)]
    [TestCase("..<", TokenKind.DotDotLess)]
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
        AssertIntegerValue(tokens[0], 42L);
    }

    [Test]
    public void BlockComment_IsSkipped()
    {
        List<Token> tokens = Lex("/* comment */ 42");
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
        AssertIntegerValue(tokens[0], 42L);
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
        List<Token> tokens = Lex("rep loop { }");
        TokenKind[] expected = new[]
        {
            TokenKind.RepKeyword,
            TokenKind.LoopKeyword,
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


    [Test]
    public void TryParseIntegerLiteral_HexDigitsAndOutOfRadixDigits_AreHandled()
    {
        MethodInfo method = typeof(Lexer).GetMethod("TryParseIntegerLiteral", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] argsLower = new object?[] { "a", 16, 0L };
        object?[] argsUpper = new object?[] { "A", 16, 0L };
        object?[] argsOutOfRadix = new object?[] { "2", 2, 0L };
        object?[] argsInvalidDigit = new object?[] { "?", 16, 0L };

        bool lowerResult = (bool)method.Invoke(null, argsLower)!;
        bool upperResult = (bool)method.Invoke(null, argsUpper)!;
        bool outOfRadixResult = (bool)method.Invoke(null, argsOutOfRadix)!;
        bool invalidDigitResult = (bool)method.Invoke(null, argsInvalidDigit)!;

        Assert.That(lowerResult, Is.True);
        Assert.That((long)argsLower[2]!, Is.EqualTo(10L));
        Assert.That(upperResult, Is.True);
        Assert.That((long)argsUpper[2]!, Is.EqualTo(10L));
        Assert.That(outOfRadixResult, Is.False);
        Assert.That((long)argsOutOfRadix[2]!, Is.EqualTo(0L));
        Assert.That(invalidDigitResult, Is.False);
        Assert.That((long)argsInvalidDigit[2]!, Is.EqualTo(0L));
    }
}
