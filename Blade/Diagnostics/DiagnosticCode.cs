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
}
