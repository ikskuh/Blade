using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade.Source;

namespace Blade.Syntax.Nodes;

/// <summary>
/// Root AST node representing an entire source file.
/// </summary>
public sealed class CompilationUnitSyntax : SyntaxNode
{
    public IReadOnlyList<MemberSyntax> Members { get; }
    
    [ExcludeFromCodeCoverage]    
    public Token EndOfFileToken { get; }

    public CompilationUnitSyntax(IReadOnlyList<MemberSyntax> members, Token endOfFileToken)
        : base(TextSpan.FromBounds(0, endOfFileToken.Span.End))
    {
        Members = members;
        EndOfFileToken = endOfFileToken;
    }
}
