using System;
using System.Globalization;
using Blade.Diagnostics;
using Blade.Source;
using Blade.Semantics;

namespace Blade.Syntax;

/// <summary>
/// Single-pass lexer for the Blade language.
/// </summary>
public sealed class Lexer(SourceText source, DiagnosticBag diagnostics)
{
    private readonly SourceText _source = Requires.NotNull(source);
    private int _position;
    private int _start;

    public DiagnosticBag Diagnostics { get; } = Requires.NotNull(diagnostics);

    private char Current => Peek(0);

    private char Lookahead => Peek(1);

    private char Peek(int offset)
    {
        int index = _position + offset;
        return index < _source.Length ? _source[index] : '\0';
    }

    private void Advance() => _position++;

    private void Advance(int count) => _position += count;

    private Token MakeToken(TokenKind kind)
    {
        int length = _position - _start;
        string text = _source.ToString(new TextSpan(_start, length));
        return new Token(kind, new TextSpan(_start, length), text);
    }

    private Token MakeToken(TokenKind kind, BladeValue? value)
    {
        int length = _position - _start;
        string text = _source.ToString(new TextSpan(_start, length));
        return new Token(kind, new TextSpan(_start, length), text, value);
    }

    public Token NextToken()
    {
        using IDisposable _ = Diagnostics.UseSource(_source);
        SkipWhitespace();

        _start = _position;

        char c = Current;

        if (c == '\0' && _position >= _source.Length)
            return MakeToken(TokenKind.EndOfFile);

        // Numbers
        if (char.IsAsciiDigit(c))
            return ReadNumber();

        // Zero-terminated strings: z"..."
        if (c == 'z' && Lookahead == '"')
        {
            Advance(); // skip 'z'
            return ReadString(zeroTerminated: true);
        }

        // Identifiers and keywords
        if (char.IsAsciiLetter(c) || c == '_')
            return ReadIdentifierOrKeyword();

        // Strings
        if (c == '"')
            return ReadString(zeroTerminated: false);

        // Character literals
        if (c == '\'')
            return ReadCharLiteral();

        // Comments or slash
        if (c == '/')
        {
            if (Lookahead == '/')
            {
                SkipLineComment();
                return NextToken();
            }

            if (Lookahead == '*')
            {
                SkipBlockComment();
                return NextToken();
            }

            Advance();
            if (Current == '=')
            {
                Advance();
                return MakeToken(TokenKind.SlashEqual);
            }
            return MakeToken(TokenKind.Slash);
        }

        // Operators and punctuation
        return ReadOperatorOrPunctuation();
    }

    private void SkipWhitespace()
    {
        while (_position < _source.Length)
        {
            char c = Current;
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                Advance();
            else
                break;
        }
    }

    private void SkipLineComment()
    {
        // Skip past //
        Advance(2);
        while (_position < _source.Length && Current != '\n' && Current != '\r')
            Advance();
    }

    private void SkipBlockComment()
    {
        int commentStart = _position;
        // Skip past /*
        Advance(2);

        int depth = 1;
        while (_position < _source.Length && depth > 0)
        {
            if (Current == '/' && Lookahead == '*')
            {
                Advance(2);
                depth++;
            }
            else if (Current == '*' && Lookahead == '/')
            {
                Advance(2);
                depth--;
            }
            else
            {
                Advance();
            }
        }

        if (depth > 0)
        {
            TextSpan span = TextSpan.FromBounds(commentStart, _position);
            Diagnostics.Report(new UnterminatedBlockCommentError(Diagnostics.CurrentSource, span));
        }
    }

    private Token ReadNumber()
    {
        if (Current == '0' && (Lookahead == 'x' || Lookahead == 'X'))
            return ReadHexNumber();

        if (Current == '0' && (Lookahead == 'b' || Lookahead == 'B'))
            return ReadBinaryNumber();

        if (Current == '0' && (Lookahead == 'q' || Lookahead == 'Q'))
            return ReadQuaternaryNumber();

        if (Current == '0' && (Lookahead == 'o' || Lookahead == 'O'))
            return ReadOctalNumber();

        return ReadDecimalNumber();
    }

    private Token ReadDecimalNumber()
    {
        while (_position < _source.Length && (char.IsAsciiDigit(Current) || Current == '_'))
            Advance();

        return FinishIntegerLiteralToken(prefixLength: 0, radix: 10);
    }

    private Token ReadHexNumber()
    {
        // Skip 0x
        Advance(2);

        while (_position < _source.Length && IsPrefixedIntegerContinuation(Current))
            Advance();

        return FinishIntegerLiteralToken(prefixLength: 2, radix: 16);
    }

    private Token ReadBinaryNumber()
    {
        // Skip 0b
        Advance(2);

        while (_position < _source.Length && IsPrefixedIntegerContinuation(Current))
            Advance();

        return FinishIntegerLiteralToken(prefixLength: 2, radix: 2);
    }

    private Token ReadQuaternaryNumber()
    {
        // Skip 0q
        Advance(2);

        while (_position < _source.Length && IsPrefixedIntegerContinuation(Current))
            Advance();

        return FinishIntegerLiteralToken(prefixLength: 2, radix: 4);
    }

    private Token ReadOctalNumber()
    {
        // Skip 0o
        Advance(2);

        while (_position < _source.Length && IsPrefixedIntegerContinuation(Current))
            Advance();

        return FinishIntegerLiteralToken(prefixLength: 2, radix: 8);
    }

    private Token FinishIntegerLiteralToken(int prefixLength, int radix)
    {
        TextSpan span = new(_start, _position - _start);
        string text = _source.ToString(span);

        if (TryParseIntegerLiteralText(text, prefixLength, radix, out long value))
            return MakeToken(TokenKind.IntegerLiteral, BladeValue.IntegerLiteral(value));

        Diagnostics.Report(new InvalidNumberLiteralError(Diagnostics.CurrentSource, span, text));
        return MakeToken(TokenKind.IntegerLiteral, BladeValue.IntegerLiteral(0L));
    }

    private Token ReadIdentifierOrKeyword()
    {
        while (_position < _source.Length && (char.IsAsciiLetterOrDigit(Current) || Current == '_'))
            Advance();

        TextSpan span = new(_start, _position - _start);
        string text = _source.ToString(span);

        TokenKind? keywordKind = SyntaxFacts.GetKeywordKind(text);
        TokenKind kind = keywordKind ?? TokenKind.Identifier;

        return kind switch
        {
            TokenKind.TrueKeyword => MakeToken(kind, BladeValue.Bool(true)),
            TokenKind.FalseKeyword => MakeToken(kind, BladeValue.Bool(false)),
            TokenKind.UndefinedKeyword => MakeToken(kind, BladeValue.Undefined),
            _ => MakeToken(kind),
        };
    }

    private Token ReadString(bool zeroTerminated)
    {
        // Skip opening quote (the 'z' prefix was already consumed by the caller if applicable)
        Advance();

        System.Text.StringBuilder sb = new();

        while (_position < _source.Length && Current != '"')
        {
            if (Current == '\n' || Current == '\r')
                break;

            if (Current == '\\')
            {
                long codepoint = ReadEscapeSequence();
                if (codepoint >= 0)
                {
                    if (codepoint <= 0x7F)
                        sb.Append((char)codepoint);
                    else
                        sb.Append(char.ConvertFromUtf32((int)codepoint));
                }
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }

        if (_position >= _source.Length || Current != '"')
        {
            TextSpan span = new(_start, _position - _start);
            Diagnostics.Report(new UnterminatedStringError(Diagnostics.CurrentSource, span));
            TokenKind errorKind = zeroTerminated ? TokenKind.ZeroTerminatedStringLiteral : TokenKind.StringLiteral;
            return MakeToken(errorKind, BladeValue.U8Array([]));
        }

        // Skip closing quote
        Advance();


        TokenKind kind = zeroTerminated ? TokenKind.ZeroTerminatedStringLiteral : TokenKind.StringLiteral;
        string decodedText = sb.ToString();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(decodedText);
        if (zeroTerminated)
            bytes = [.. bytes, 0];
        return MakeToken(kind, BladeValue.U8Array(bytes));
    }

    private Token ReadCharLiteral()
    {
        // Skip opening quote
        Advance();

        long value;

        if (Current == '\\')
        {
            value = ReadEscapeSequence();
            if (value < 0)
                value = 0;
        }
        else if (Current == '\'' || Current == '\0')
        {
            TextSpan span = new(_start, _position - _start);
            Diagnostics.Report(new UnterminatedStringError(Diagnostics.CurrentSource, span));
            return MakeToken(TokenKind.CharLiteral, BladeValue.IntegerLiteral(0L));
        }
        else
        {
            value = Current;
            Advance();
        }

        if (Current != '\'')
        {
            // Consume remaining characters until closing quote
            while (_position < _source.Length && Current != '\'' && Current != '\n' && Current != '\r')
                Advance();

            if (Current == '\'')
                Advance();

            TextSpan span = new(_start, _position - _start);
            Diagnostics.Report(new InvalidCharacterLiteralError(Diagnostics.CurrentSource, span));
            return MakeToken(TokenKind.CharLiteral, BladeValue.IntegerLiteral(value));
        }

        // Skip closing quote
        Advance();

        return MakeToken(TokenKind.CharLiteral, BladeValue.IntegerLiteral(value));
    }

    /// <summary>
    /// Reads an escape sequence starting at the backslash.
    /// Returns the codepoint value, or -1 on error (diagnostic already reported).
    /// </summary>
    private long ReadEscapeSequence()
    {
        int escapeStart = _position;
        Advance(); // skip backslash

        char esc = Current;
        Advance();

        switch (esc)
        {
            case '0': return 0x00;
            case 't': return 0x09;
            case 'n': return 0x0A;
            case 'r': return 0x0D;
            case 'e': return 0x1B;
            case '\\': return '\\';
            case '\'': return '\'';
            case '"': return '"';

            case 'x':
                {
                    // \xHH — exactly 2 hex digits
                    if (IsHexDigit(Current) && IsHexDigit(Peek(1)))
                    {
                        string hex = new string(new[] { Current, Peek(1) });
                        Advance(2);
                        return Convert.ToInt64(hex, 16);
                    }

                    TextSpan span = TextSpan.FromBounds(escapeStart, _position);
                    Diagnostics.Report(new InvalidEscapeSequenceError(Diagnostics.CurrentSource, span));
                    return -1;
                }

            case 'u':
                {
                    // \u{XXXXXX} — 1-6 hex digits in braces
                    if (Current != '{')
                    {
                        TextSpan span = TextSpan.FromBounds(escapeStart, _position);
                        Diagnostics.Report(new InvalidEscapeSequenceError(Diagnostics.CurrentSource, span));
                        return -1;
                    }

                    Advance(); // skip {
                    int digitStart = _position;

                    while (_position < _source.Length && IsHexDigit(Current))
                        Advance();

                    int digitCount = _position - digitStart;

                    if (digitCount == 0 || digitCount > 6 || Current != '}')
                    {
                        TextSpan span = TextSpan.FromBounds(escapeStart, _position);
                        Diagnostics.Report(new InvalidEscapeSequenceError(Diagnostics.CurrentSource, span));
                        if (Current == '}')
                            Advance();
                        return -1;
                    }

                    string hex = _source.ToString(new TextSpan(digitStart, digitCount));
                    Advance(); // skip }
                    long codepoint = Convert.ToInt64(hex, 16);
                    if (!IsValidUnicodeScalar(codepoint))
                    {
                        TextSpan span = TextSpan.FromBounds(escapeStart, _position);
                        Diagnostics.Report(new InvalidEscapeSequenceError(Diagnostics.CurrentSource, span));
                        return -1;
                    }

                    return codepoint;
                }

            default:
                {
                    TextSpan span = TextSpan.FromBounds(escapeStart, _position);
                    Diagnostics.Report(new InvalidEscapeSequenceError(Diagnostics.CurrentSource, span));
                    return -1;
                }
        }
    }

    private static bool IsValidUnicodeScalar(long codepoint)
        => codepoint >= 0
            && codepoint <= 0x10FFFF
            && (codepoint < 0xD800 || codepoint > 0xDFFF);

    private Token ReadOperatorOrPunctuation()
    {
        char c = Current;

        switch (c)
        {
            case '+':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.PlusEqual); }
                return MakeToken(TokenKind.Plus);

            case '-':
                Advance();
                if (Current == '>') { Advance(); return MakeToken(TokenKind.Arrow); }
                if (Current == '=') { Advance(); return MakeToken(TokenKind.MinusEqual); }
                return MakeToken(TokenKind.Minus);

            case '<':
                Advance();
                if (Current == '<')
                {
                    Advance();
                    if (Current == '<')
                    {
                        Advance();
                        if (Current == '=') { Advance(); return MakeToken(TokenKind.LessLessLessEqual); }
                        return MakeToken(TokenKind.LessLessLess);
                    }
                    if (Current == '=') { Advance(); return MakeToken(TokenKind.LessLessEqual); }
                    return MakeToken(TokenKind.LessLess);
                }
                if (Current == '%' && Peek(1) == '<')
                {
                    Advance(2);
                    if (Current == '=') { Advance(); return MakeToken(TokenKind.RotateLeftEqual); }
                    return MakeToken(TokenKind.RotateLeft);
                }
                if (Current == '=') { Advance(); return MakeToken(TokenKind.LessEqual); }
                return MakeToken(TokenKind.Less);

            case '>':
                Advance();
                if (Current == '>')
                {
                    Advance();
                    if (Current == '>')
                    {
                        Advance();
                        if (Current == '=') { Advance(); return MakeToken(TokenKind.GreaterGreaterGreaterEqual); }
                        return MakeToken(TokenKind.GreaterGreaterGreater);
                    }
                    if (Current == '=') { Advance(); return MakeToken(TokenKind.GreaterGreaterEqual); }
                    return MakeToken(TokenKind.GreaterGreater);
                }
                if (Current == '%' && Peek(1) == '>')
                {
                    Advance(2);
                    if (Current == '=') { Advance(); return MakeToken(TokenKind.RotateRightEqual); }
                    return MakeToken(TokenKind.RotateRight);
                }
                if (Current == '=') { Advance(); return MakeToken(TokenKind.GreaterEqual); }
                return MakeToken(TokenKind.Greater);

            case '=':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.EqualEqual); }
                return MakeToken(TokenKind.Equal);

            case '!':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.BangEqual); }
                return MakeToken(TokenKind.Bang);

            case '&':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.AmpersandEqual); }
                return MakeToken(TokenKind.Ampersand);

            case '|':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.PipeEqual); }
                return MakeToken(TokenKind.Pipe);

            case '^':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.CaretEqual); }
                return MakeToken(TokenKind.Caret);

            case '.':
                Advance();
                if (Current == '.')
                {
                    Advance();
                    if (Current == '.') { Advance(); return MakeToken(TokenKind.DotDotDot); }
                    if (Current == '=') { Advance(); return MakeToken(TokenKind.DotDotEqual); }
                    if (Current == '<') { Advance(); return MakeToken(TokenKind.DotDotLess); }
                    return MakeToken(TokenKind.DotDot);
                }
                return MakeToken(TokenKind.Dot);

            case '~': Advance(); return MakeToken(TokenKind.Tilde);
            case '%':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.PercentEqual); }
                return MakeToken(TokenKind.Percent);
            case '@': Advance(); return MakeToken(TokenKind.At);
            case '#': Advance(); return MakeToken(TokenKind.Hash);
            case '$': Advance(); return MakeToken(TokenKind.Dollar);
            case '*':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.StarEqual); }
                return MakeToken(TokenKind.Star);
            case '(': Advance(); return MakeToken(TokenKind.OpenParen);
            case ')': Advance(); return MakeToken(TokenKind.CloseParen);
            case '{': Advance(); return MakeToken(TokenKind.OpenBrace);
            case '}': Advance(); return MakeToken(TokenKind.CloseBrace);
            case '[': Advance(); return MakeToken(TokenKind.OpenBracket);
            case ']': Advance(); return MakeToken(TokenKind.CloseBracket);
            case ';': Advance(); return MakeToken(TokenKind.Semicolon);
            case ',': Advance(); return MakeToken(TokenKind.Comma);
            case ':': Advance(); return MakeToken(TokenKind.Colon);

            default:
                Advance();
                TextSpan span = new(_start, 1);
                Diagnostics.Report(new UnexpectedCharacterError(Diagnostics.CurrentSource, span, c));
                return MakeToken(TokenKind.Bad);
        }
    }

    private static bool TryParseIntegerLiteralText(string text, int prefixLength, int radix, out long value)
    {
        value = 0;

        if (text.Length < prefixLength)
            return false;

        string digits = text.Substring(prefixLength);
        if (!HasValidDigitSeparators(digits, radix))
            return false;

        string normalizedDigits = digits.Replace("_", "", StringComparison.Ordinal);
        if (radix == 10)
            return long.TryParse(normalizedDigits, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        return TryParseIntegerLiteral(normalizedDigits, radix, out value);
    }

    private static bool HasValidDigitSeparators(string digits, int radix)
    {
        if (digits.Length == 0)
            return false;

        for (int i = 0; i < digits.Length; i++)
        {
            char current = digits[i];
            if (current == '_')
            {
                if (i == 0 || i == digits.Length - 1)
                    return false;

                if (!IsDigitForRadix(digits[i - 1], radix) || !IsDigitForRadix(digits[i + 1], radix))
                    return false;

                continue;
            }

            if (!IsDigitForRadix(current, radix))
                return false;
        }

        return true;
    }

    private static bool TryParseIntegerLiteral(string digits, int radix, out long value)
    {
        value = 0;
        if (digits.Length == 0)
            return false;

        checked
        {
            try
            {
                for (int i = 0; i < digits.Length; i++)
                {
                    int digit = GetDigitValue(digits[i]);

                    if (digit < 0 || digit >= radix)
                        return false;

                    value = (value * radix) + digit;
                }

                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }

    private static bool IsPrefixedIntegerContinuation(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsDigitForRadix(char c, int radix)
    {
        int digit = GetDigitValue(c);
        return digit >= 0 && digit < radix;
    }

    private static int GetDigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static bool IsHexDigit(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
