using System;
using System.Linq;
using System.Text.Json;
using Blade.Syntax;

namespace Blade.Source;

/// <summary>
/// Represents a span of text in source code by start position and length.
/// </summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    public static TextSpan FromBounds(params ITextSpannedElement?[] elements)
    {
        return elements
            .Where(t => t != null)
            .Aggregate(
                FromBounds(int.MaxValue, int.MinValue),
                (prev, elem) => FromBounds(
                    start: Math.Min(prev.Start, elem!.Span.Start),
                    end: Math.Max(prev.End, elem!.Span.End)
                ));
    }
}

/// <summary>
/// A human-readable source location (file, line, column) for diagnostic messages.
/// </summary>
public readonly record struct SourceLocation(string FilePath, int Line, int Column)
{
    public override string ToString() => $"{FilePath}:{Line}:{Column}";
}
