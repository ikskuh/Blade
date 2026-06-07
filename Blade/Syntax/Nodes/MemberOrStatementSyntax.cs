using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.Source;

namespace Blade.Syntax.Nodes;


/// <summary>
/// Base class for top-level declarations.
/// </summary>
public abstract class MemberOrStatementSyntax(TextSpan span) : SyntaxNode(span)
{
}

/// <summary>
/// Marker interface for syntax items legal inside function bodies and embedded statement bodies.
/// </summary>
public interface ICodeBodyItemSyntax : ITaskBodyItemSyntax
{
}

/// <summary>
/// Marker interface for syntax items legal inside task bodies.
/// </summary>
public interface ITaskBodyItemSyntax
{
	TextSpan Span { get; }
}

/// <summary>
/// Recovery node for invalid non-top-level body items.
/// </summary>
public sealed class InvalidBodyItemSyntax(TextSpan span) : SyntaxNode(span), ICodeBodyItemSyntax, ITaskBodyItemSyntax
{
}