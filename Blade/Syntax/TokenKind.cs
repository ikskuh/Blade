namespace Blade.Syntax;

/// <summary>
/// All token kinds in the Blade language.
/// </summary>
public enum TokenKind
{
    // Special
    Bad,
    EndOfFile,
    Identifier,

    // Literals
    IntegerLiteral,
    StringLiteral,

    // Storage class keywords
    RegKeyword,
    LutKeyword,
    HubKeyword,

    // Modifier keywords
    ExternKeyword,
    ConstKeyword,
    VarKeyword,

    // Function kind keywords
    FnKeyword,
    LeafKeyword,
    InlineKeyword,
    RecKeyword,
    CoroKeyword,
    ComptimeKeyword,
    Int1Keyword,
    Int2Keyword,
    Int3Keyword,

    // Control flow keywords
    IfKeyword,
    ElseKeyword,
    WhileKeyword,
    ForKeyword,
    LoopKeyword,
    BreakKeyword,
    ContinueKeyword,
    ReturnKeyword,
    YieldKeyword,
    YieldtoKeyword,
    RepKeyword,
    NoirqKeyword,
    InKeyword,

    // Other keywords
    ImportKeyword,
    AsKeyword,
    AsmKeyword,
    PackedKeyword,
    StructKeyword,
    UndefinedKeyword,
    AlignKeyword,
    VoidKeyword,
    TrueKeyword,
    FalseKeyword,

    // Type keywords
    BoolKeyword,
    BitKeyword,
    NitKeyword,
    NibKeyword,
    U8Keyword,
    I8Keyword,
    U16Keyword,
    I16Keyword,
    U32Keyword,
    I32Keyword,
    UintKeyword,
    IntKeyword,

    // Operators
    Plus,
    Minus,
    Star,
    Slash,
    Ampersand,
    Pipe,
    Caret,
    Bang,
    Dot,
    At,

    // Multi-char operators
    Arrow,          // ->
    DotDot,         // ..
    PlusPlus,       // ++
    MinusMinus,     // --
    LessLess,       // <<
    GreaterGreater, // >>
    EqualEqual,     // ==
    BangEqual,      // !=
    LessEqual,      // <=
    GreaterEqual,   // >=

    // Comparison
    Less,
    Greater,

    // Assignment
    Equal,
    PlusEqual,
    MinusEqual,
    AmpersandEqual,
    PipeEqual,
    CaretEqual,
    LessLessEqual,    // <<=
    GreaterGreaterEqual, // >>=

    // Delimiters
    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    Semicolon,
    Comma,
    Colon,
}
