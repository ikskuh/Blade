using System.Collections.Frozen;
using System.Collections.Generic;

namespace Blade.Syntax;

/// <summary>
/// Lookup tables for keywords and fixed-text tokens.
/// </summary>
public static class SyntaxFacts
{
    private static readonly FrozenDictionary<string, TokenKind> Keywords = new Dictionary<string, TokenKind>
    {
        // Storage classes
        ["reg"] = TokenKind.RegKeyword,
        ["lut"] = TokenKind.LutKeyword,
        ["hub"] = TokenKind.HubKeyword,

        // Modifiers
        ["extern"] = TokenKind.ExternKeyword,
        ["const"] = TokenKind.ConstKeyword,
        ["var"] = TokenKind.VarKeyword,

        // Function kinds
        ["fn"] = TokenKind.FnKeyword,
        ["leaf"] = TokenKind.LeafKeyword,
        ["inline"] = TokenKind.InlineKeyword,
        ["rec"] = TokenKind.RecKeyword,
        ["coro"] = TokenKind.CoroKeyword,
        ["comptime"] = TokenKind.ComptimeKeyword,
        ["int1"] = TokenKind.Int1Keyword,
        ["int2"] = TokenKind.Int2Keyword,
        ["int3"] = TokenKind.Int3Keyword,

        // Control flow
        ["if"] = TokenKind.IfKeyword,
        ["else"] = TokenKind.ElseKeyword,
        ["while"] = TokenKind.WhileKeyword,
        ["for"] = TokenKind.ForKeyword,
        ["loop"] = TokenKind.LoopKeyword,
        ["break"] = TokenKind.BreakKeyword,
        ["continue"] = TokenKind.ContinueKeyword,
        ["return"] = TokenKind.ReturnKeyword,
        ["yield"] = TokenKind.YieldKeyword,
        ["yieldto"] = TokenKind.YieldtoKeyword,
        ["rep"] = TokenKind.RepKeyword,
        ["noirq"] = TokenKind.NoirqKeyword,
        ["in"] = TokenKind.InKeyword,

        // Other keywords
        ["import"] = TokenKind.ImportKeyword,
        ["as"] = TokenKind.AsKeyword,
        ["asm"] = TokenKind.AsmKeyword,
        ["packed"] = TokenKind.PackedKeyword,
        ["struct"] = TokenKind.StructKeyword,
        ["undefined"] = TokenKind.UndefinedKeyword,
        ["align"] = TokenKind.AlignKeyword,
        ["void"] = TokenKind.VoidKeyword,
        ["true"] = TokenKind.TrueKeyword,
        ["false"] = TokenKind.FalseKeyword,

        // Type keywords
        ["bool"] = TokenKind.BoolKeyword,
        ["bit"] = TokenKind.BitKeyword,
        ["nit"] = TokenKind.NitKeyword,
        ["nib"] = TokenKind.NibKeyword,
        ["u8"] = TokenKind.U8Keyword,
        ["i8"] = TokenKind.I8Keyword,
        ["u16"] = TokenKind.U16Keyword,
        ["i16"] = TokenKind.I16Keyword,
        ["u32"] = TokenKind.U32Keyword,
        ["i32"] = TokenKind.I32Keyword,
        ["uint"] = TokenKind.UintKeyword,
        ["int"] = TokenKind.IntKeyword,
    }.ToFrozenDictionary();

    /// <summary>
    /// Returns the keyword TokenKind for the given text, or null if it's not a keyword.
    /// </summary>
    public static TokenKind? GetKeywordKind(string text)
    {
        return Keywords.TryGetValue(text, out TokenKind kind) ? kind : null;
    }

    /// <summary>
    /// Returns the fixed text for a token kind, or null for variable-text tokens.
    /// </summary>
    public static string? GetText(TokenKind kind) => kind switch
    {
        // Keywords
        TokenKind.RegKeyword => "reg",
        TokenKind.LutKeyword => "lut",
        TokenKind.HubKeyword => "hub",
        TokenKind.ExternKeyword => "extern",
        TokenKind.ConstKeyword => "const",
        TokenKind.VarKeyword => "var",
        TokenKind.FnKeyword => "fn",
        TokenKind.LeafKeyword => "leaf",
        TokenKind.InlineKeyword => "inline",
        TokenKind.RecKeyword => "rec",
        TokenKind.CoroKeyword => "coro",
        TokenKind.ComptimeKeyword => "comptime",
        TokenKind.Int1Keyword => "int1",
        TokenKind.Int2Keyword => "int2",
        TokenKind.Int3Keyword => "int3",
        TokenKind.IfKeyword => "if",
        TokenKind.ElseKeyword => "else",
        TokenKind.WhileKeyword => "while",
        TokenKind.ForKeyword => "for",
        TokenKind.LoopKeyword => "loop",
        TokenKind.BreakKeyword => "break",
        TokenKind.ContinueKeyword => "continue",
        TokenKind.ReturnKeyword => "return",
        TokenKind.YieldKeyword => "yield",
        TokenKind.YieldtoKeyword => "yieldto",
        TokenKind.RepKeyword => "rep",
        TokenKind.NoirqKeyword => "noirq",
        TokenKind.InKeyword => "in",
        TokenKind.ImportKeyword => "import",
        TokenKind.AsKeyword => "as",
        TokenKind.AsmKeyword => "asm",
        TokenKind.PackedKeyword => "packed",
        TokenKind.StructKeyword => "struct",
        TokenKind.UndefinedKeyword => "undefined",
        TokenKind.AlignKeyword => "align",
        TokenKind.VoidKeyword => "void",
        TokenKind.TrueKeyword => "true",
        TokenKind.FalseKeyword => "false",
        TokenKind.BoolKeyword => "bool",
        TokenKind.BitKeyword => "bit",
        TokenKind.NitKeyword => "nit",
        TokenKind.NibKeyword => "nib",
        TokenKind.U8Keyword => "u8",
        TokenKind.I8Keyword => "i8",
        TokenKind.U16Keyword => "u16",
        TokenKind.I16Keyword => "i16",
        TokenKind.U32Keyword => "u32",
        TokenKind.I32Keyword => "i32",
        TokenKind.UintKeyword => "uint",
        TokenKind.IntKeyword => "int",

        // Operators
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Ampersand => "&",
        TokenKind.Pipe => "|",
        TokenKind.Caret => "^",
        TokenKind.Bang => "!",
        TokenKind.Dot => ".",
        TokenKind.At => "@",
        TokenKind.Arrow => "->",
        TokenKind.DotDot => "..",
        TokenKind.PlusPlus => "++",
        TokenKind.MinusMinus => "--",
        TokenKind.LessLess => "<<",
        TokenKind.GreaterGreater => ">>",
        TokenKind.EqualEqual => "==",
        TokenKind.BangEqual => "!=",
        TokenKind.LessEqual => "<=",
        TokenKind.GreaterEqual => ">=",
        TokenKind.Less => "<",
        TokenKind.Greater => ">",
        TokenKind.Equal => "=",
        TokenKind.PlusEqual => "+=",
        TokenKind.MinusEqual => "-=",
        TokenKind.AmpersandEqual => "&=",
        TokenKind.PipeEqual => "|=",
        TokenKind.CaretEqual => "^=",
        TokenKind.LessLessEqual => "<<=",
        TokenKind.GreaterGreaterEqual => ">>=",

        // Delimiters
        TokenKind.OpenParen => "(",
        TokenKind.CloseParen => ")",
        TokenKind.OpenBrace => "{",
        TokenKind.CloseBrace => "}",
        TokenKind.OpenBracket => "[",
        TokenKind.CloseBracket => "]",
        TokenKind.Semicolon => ";",
        TokenKind.Comma => ",",
        TokenKind.Colon => ":",

        _ => null,
    };

    /// <summary>
    /// Returns the binary operator precedence for a token kind, or 0 if not a binary operator.
    /// Higher values bind tighter.
    /// </summary>
    public static int GetBinaryOperatorPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Star or TokenKind.Slash => 7,
        TokenKind.Plus or TokenKind.Minus => 6,
        TokenKind.LessLess or TokenKind.GreaterGreater => 5,
        TokenKind.Ampersand => 4,
        TokenKind.Caret => 3,
        TokenKind.Pipe => 2,
        TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual
            or TokenKind.EqualEqual or TokenKind.BangEqual => 1,
        _ => 0,
    };

    /// <summary>
    /// Returns the unary operator precedence for a token kind, or 0 if not a unary prefix operator.
    /// </summary>
    public static int GetUnaryOperatorPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Bang or TokenKind.Minus or TokenKind.Star => 8,
        _ => 0,
    };

    /// <summary>
    /// Returns true if the token kind is an assignment operator (=, +=, -=, etc.).
    /// </summary>
    public static bool IsAssignmentOperator(TokenKind kind) => kind switch
    {
        TokenKind.Equal or TokenKind.PlusEqual or TokenKind.MinusEqual
            or TokenKind.AmpersandEqual or TokenKind.PipeEqual or TokenKind.CaretEqual
            or TokenKind.LessLessEqual or TokenKind.GreaterGreaterEqual => true,
        _ => false,
    };
}
