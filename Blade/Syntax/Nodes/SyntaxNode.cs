using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all AST nodes.
/// </summary>
public abstract class SyntaxNode(TextSpan span) : ITextSpannedElement
{
    public TextSpan Span { get; } = span;
}
