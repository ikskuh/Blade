namespace Blade.Source;

public readonly record struct SourceSpan(SourceText Source, TextSpan Span)
{
    public SourceText Source { get; } = Requires.NotNull(Source);
    public TextSpan Span { get; } = Span;

    public string FilePath => Source.FilePath;
    public int Start => Span.Start;
    public int Length => Span.Length;
    public int End => Span.End;

    public SourceLocation StartLocation => Source.GetLocation(Span.Start);

    public static SourceSpan Synthetic(string filePath = "<synthetic>")
    {
        return new SourceSpan(new SourceText(string.Empty, filePath), new TextSpan(0, 0));
    }
}
