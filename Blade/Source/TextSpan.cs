namespace Blade.Source;

/// <summary>
/// Represents a span of text in source code by start position and length.
/// </summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);
}

/// <summary>
/// A human-readable source location (file, line, column) for diagnostic messages.
/// </summary>
public readonly record struct SourceLocation(string FilePath, int Line, int Column)
{
    public override string ToString() => $"{FilePath}:{Line}:{Column}";
}
