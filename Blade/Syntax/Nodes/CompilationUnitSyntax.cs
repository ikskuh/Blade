using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Root AST node representing an entire source file.
/// </summary>
public sealed class CompilationUnitSyntax(IReadOnlyList<MemberSyntax> members, Token endOfFileToken) : SyntaxNode(TextSpan.FromBounds(0, endOfFileToken.Span.End))
{
    public IReadOnlyList<MemberSyntax> Members { get; } = members;

    [ExcludeFromCodeCoverage]
    public Token EndOfFileToken { get; } = endOfFileToken;
}
