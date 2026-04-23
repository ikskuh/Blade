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
    ZeroTerminatedStringLiteral,
    CharLiteral,

    // Storage class keywords
    CogKeyword,
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
    NoinlineKeyword,
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
    AssertKeyword,
    InKeyword,

    // Other keywords
    ImportKeyword,
    AsKeyword,
    AsmKeyword,
    VolatileKeyword,
    StructKeyword,
    UnionKeyword,
    EnumKeyword,
    BitfieldKeyword,
    TypeKeyword,
    LayoutKeyword,
    TaskKeyword,
    BitcastKeyword,
    UndefinedKeyword,
    AlignKeyword,
    VoidKeyword,
    TrueKeyword,
    FalseKeyword,
    AndKeyword,
    OrKeyword,
    SizeofKeyword,
    AlignofKeyword,
    MemoryofKeyword,

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
    U8x4Keyword,

    // Operators
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Ampersand,
    Pipe,
    Caret,
    Tilde,
    Bang,
    Dot,
    At,
    Hash,
    Dollar,

    // Multi-char operators
    Arrow,                  // ->
    DotDot,                 // ..
    DotDotDot,              // ...
    DotDotEqual,            // ..=
    DotDotLess,             // ..<
    LessLess,               // <<
    GreaterGreater,         // >>
    LessLessLess,           // <<<
    GreaterGreaterGreater,  // >>>
    RotateLeft,             // <%<
    RotateRight,            // >%>
    EqualEqual,             // ==
    BangEqual,              // !=
    LessEqual,              // <=
    GreaterEqual,           // >=

    // Comparison
    Less,
    Greater,

    // Assignment
    Equal,
    PlusEqual,
    MinusEqual,
    StarEqual,
    SlashEqual,
    PercentEqual,
    AmpersandEqual,
    PipeEqual,
    CaretEqual,
    LessLessLessEqual,    // <<<=
    GreaterGreaterGreaterEqual, // >>>=
    RotateLeftEqual,      // <%<=
    RotateRightEqual,     // >%>=
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
