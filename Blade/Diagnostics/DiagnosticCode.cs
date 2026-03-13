namespace Blade.Diagnostics;

/// <summary>
/// Numeric diagnostic codes. E = error, W = warning, I = info.
/// </summary>
public enum DiagnosticCode
{
    // Lexer errors
    E0001_UnexpectedCharacter = 1,
    E0002_UnterminatedString = 2,
    E0003_InvalidNumberLiteral = 3,
    E0004_UnterminatedBlockComment = 4,

    // Parser errors
    E0101_UnexpectedToken = 101,
    E0102_ExpectedExpression = 102,
    E0103_ExpectedStatement = 103,
    E0104_ExpectedTypeName = 104,
    E0105_ExpectedIdentifier = 105,
    E0106_InvalidAssignmentTarget = 106,
    E0107_ExpectedSemicolon = 107,

    // Semantic errors
    E0201_SymbolAlreadyDeclared = 201,
    E0202_UndefinedName = 202,
    E0203_UndefinedType = 203,
    E0204_CannotAssignToConstant = 204,
    E0205_TypeMismatch = 205,
    E0206_NotCallable = 206,
    E0207_ArgumentCountMismatch = 207,
    E0208_InvalidLoopControl = 208,
    E0209_InvalidBreakInRepLoop = 209,
    E0210_InvalidYieldUsage = 210,
    E0211_InvalidYieldtoUsage = 211,
    E0212_ReturnValueCountMismatch = 212,
    E0213_ReturnOutsideFunction = 213,
    E0214_InvalidYieldtoTarget = 214,

    // Inline assembly errors
    E0301_InlineAsmUnknownInstruction = 301,
    E0302_InlineAsmUndefinedVariable = 302,
    E0303_InlineAsmEmptyInstruction = 303,
    E0304_InlineAsmInvalidFlagOutput = 304,
}
