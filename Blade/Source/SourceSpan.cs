using System.Diagnostics.CodeAnalysis;

namespace Blade.Source;

public readonly record struct SourceSpan(SourceText Source, TextSpan Span)
{
    public SourceText Source { get; } = Requires.NotNull(Source);
    public TextSpan Span { get; } = Span;

    [ExcludeFromCodeCoverage]
    public string FilePath => Source.FilePath;
    
    [ExcludeFromCodeCoverage]
    public int Start => Span.Start;
    
    [ExcludeFromCodeCoverage]
    public int Length => Span.Length;
    
    [ExcludeFromCodeCoverage]
    public int End => Span.End;

    public SourceLocation StartLocation => Source.GetLocation(Span.Start);

    public static SourceSpan Synthetic(string filePath = "<synthetic>")
    {
        return new SourceSpan(new SourceText(string.Empty, filePath), new TextSpan(0, 0));
    }
}
