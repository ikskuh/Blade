using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Base class for all AST nodes.
/// </summary>
public abstract class SyntaxNode
{
    public TextSpan Span { get; }

    protected SyntaxNode(TextSpan span)
    {
        Span = span;
    }
}
