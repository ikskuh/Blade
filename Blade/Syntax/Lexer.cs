using System;
using System.Globalization;
using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Syntax;

/// <summary>
/// Single-pass lexer for the Blade language.
/// </summary>
public sealed class Lexer
{
    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private int _position;
    private int _start;

    public Lexer(SourceText source, DiagnosticBag diagnostics)
    {
        _source = source;
        _diagnostics = diagnostics;
    }

    public DiagnosticBag Diagnostics => _diagnostics;

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

    private Token MakeToken(TokenKind kind, object? value)
    {
        int length = _position - _start;
        string text = _source.ToString(new TextSpan(_start, length));
        return new Token(kind, new TextSpan(_start, length), text, value);
    }

    public Token NextToken()
    {
        SkipWhitespace();

        _start = _position;

        char c = Current;

        if (c == '\0' && _position >= _source.Length)
            return MakeToken(TokenKind.EndOfFile);

        // Numbers
        if (char.IsAsciiDigit(c))
            return ReadNumber();

        // Identifiers and keywords
        if (char.IsAsciiLetter(c) || c == '_')
            return ReadIdentifierOrKeyword();

        // Strings
        if (c == '"')
            return ReadString();

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
            _diagnostics.ReportUnterminatedBlockComment(span);
        }
    }

    private Token ReadNumber()
    {
        if (Current == '0' && (Lookahead == 'x' || Lookahead == 'X'))
            return ReadHexNumber();

        if (Current == '0' && (Lookahead == 'b' || Lookahead == 'B'))
            return ReadBinaryNumber();

        return ReadDecimalNumber();
    }

    private Token ReadDecimalNumber()
    {
        while (_position < _source.Length && (char.IsAsciiDigit(Current) || Current == '_'))
            Advance();

        TextSpan span = new(_start, _position - _start);
        string text = _source.ToString(span);
        string digits = text.Replace("_", "");

        if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            return MakeToken(TokenKind.IntegerLiteral, value);

        _diagnostics.ReportInvalidNumberLiteral(span, text);
        return MakeToken(TokenKind.IntegerLiteral, 0L);
    }

    private Token ReadHexNumber()
    {
        // Skip 0x
        Advance(2);

        while (_position < _source.Length && (IsHexDigit(Current) || Current == '_'))
            Advance();

        TextSpan span = new(_start, _position - _start);
        string text = _source.ToString(span);
        string digits = text.Substring(2).Replace("_", "");

        if (digits.Length > 0 && long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            return MakeToken(TokenKind.IntegerLiteral, value);

        _diagnostics.ReportInvalidNumberLiteral(span, text);
        return MakeToken(TokenKind.IntegerLiteral, 0L);
    }

    private Token ReadBinaryNumber()
    {
        // Skip 0b
        Advance(2);

        while (_position < _source.Length && (Current == '0' || Current == '1' || Current == '_'))
            Advance();

        TextSpan span = new(_start, _position - _start);
        string text = _source.ToString(span);
        string digits = text.Substring(2).Replace("_", "");

        if (digits.Length > 0)
        {
            try
            {
                long value = Convert.ToInt64(digits, 2);
                return MakeToken(TokenKind.IntegerLiteral, value);
            }
            catch (OverflowException)
            {
                // fall through to error
            }
        }

        _diagnostics.ReportInvalidNumberLiteral(span, text);
        return MakeToken(TokenKind.IntegerLiteral, 0L);
    }

    private Token ReadIdentifierOrKeyword()
    {
        while (_position < _source.Length && (char.IsAsciiLetterOrDigit(Current) || Current == '_'))
            Advance();

        TextSpan span = new(_start, _position - _start);
        string text = _source.ToString(span);

        TokenKind? keywordKind = SyntaxFacts.GetKeywordKind(text);
        TokenKind kind = keywordKind ?? TokenKind.Identifier;

        return MakeToken(kind);
    }

    private Token ReadString()
    {
        // Skip opening quote
        Advance();

        while (_position < _source.Length && Current != '"')
        {
            if (Current == '\n' || Current == '\r')
                break;
            Advance();
        }

        if (_position >= _source.Length || Current != '"')
        {
            TextSpan span = new(_start, _position - _start);
            _diagnostics.ReportUnterminatedString(span);
            return MakeToken(TokenKind.StringLiteral, "");
        }

        // Extract string value (without quotes)
        string value = _source.ToString(new TextSpan(_start + 1, _position - _start - 1));

        // Skip closing quote
        Advance();

        return MakeToken(TokenKind.StringLiteral, value);
    }

    private Token ReadOperatorOrPunctuation()
    {
        char c = Current;

        switch (c)
        {
            case '+':
                Advance();
                if (Current == '+') { Advance(); return MakeToken(TokenKind.PlusPlus); }
                if (Current == '=') { Advance(); return MakeToken(TokenKind.PlusEqual); }
                return MakeToken(TokenKind.Plus);

            case '-':
                Advance();
                if (Current == '-') { Advance(); return MakeToken(TokenKind.MinusMinus); }
                if (Current == '>') { Advance(); return MakeToken(TokenKind.Arrow); }
                if (Current == '=') { Advance(); return MakeToken(TokenKind.MinusEqual); }
                return MakeToken(TokenKind.Minus);

            case '<':
                Advance();
                if (Current == '<')
                {
                    Advance();
                    if (Current == '=') { Advance(); return MakeToken(TokenKind.LessLessEqual); }
                    return MakeToken(TokenKind.LessLess);
                }
                if (Current == '=') { Advance(); return MakeToken(TokenKind.LessEqual); }
                return MakeToken(TokenKind.Less);

            case '>':
                Advance();
                if (Current == '>')
                {
                    Advance();
                    if (Current == '=') { Advance(); return MakeToken(TokenKind.GreaterGreaterEqual); }
                    return MakeToken(TokenKind.GreaterGreater);
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
                if (Current == '.') { Advance(); return MakeToken(TokenKind.DotDot); }
                return MakeToken(TokenKind.Dot);

            case '@': Advance(); return MakeToken(TokenKind.At);
            case '#': Advance(); return MakeToken(TokenKind.Hash);
            case '*': Advance(); return MakeToken(TokenKind.Star);
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
                _diagnostics.ReportUnexpectedCharacter(span, c);
                return MakeToken(TokenKind.Bad);
        }
    }

    private static bool IsHexDigit(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
