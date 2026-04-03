using Blade.Semantics;
using Blade.Source;

namespace Blade.Syntax;

/// <summary>
/// A single token produced by the lexer.
/// </summary>
public readonly record struct Token(TokenKind Kind, TextSpan Span, string Text, BladeValue? Value = null);
