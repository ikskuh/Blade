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
}
