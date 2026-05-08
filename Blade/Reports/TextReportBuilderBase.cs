using System;
using System.Collections.Generic;
using System.Linq;

namespace Blade.Reports;

/// <summary>
/// Base class that wraps an ITextReportBuilder and provides common helper methods for emitting styled text spans and newlines.
/// </summary>
public abstract class TextReportBuilderBase(ITextReportBuilder builder)
{
    private readonly ITextReportBuilder _builder = Requires.NotNull(builder);

    protected abstract class Span(string text)
    {
        public string Text { get; } = text;

        public abstract void Emit(TextReportBuilderBase builder);

        public static implicit operator Span(char chr) => FromChar(chr);

        public static Span FromChar(char chr) => chr switch
        {
            _ when char.IsWhiteSpace(chr) => new BasicSpan(BasicTextSpanKind.Whitespace, chr.ToString()),
            _ when "()[]{},.:+-*/%&|^~!=<>?".Contains(chr, StringComparison.Ordinal) => new BasicSpan(BasicTextSpanKind.Punctuation, chr.ToString()),
            _ => Assert.UnreachableValue<Span>($"No implicit conversion defined for character '{chr}'."),
        };

        public static implicit operator Span((BasicTextSpanKind kind, string text) tuple) => FromValueTuple(tuple);
        public static implicit operator Span((SemanticTextSpanKind kind, object identity, string text) tuple) => FromValueTuple(tuple);

        public static Span FromValueTuple((BasicTextSpanKind kind, string text) tuple) => new BasicSpan(tuple.kind, tuple.text);

        public static Span FromValueTuple((SemanticTextSpanKind kind, object identity, string text) tuple) => new SemanticSpan(tuple.kind, tuple.identity, tuple.text);
    }

    protected static Span Basic(BasicTextSpanKind kind, string text)
    {
        return new BasicSpan(kind, text);
    }

    protected static Span Semantic(SemanticTextSpanKind kind, object identity, string text)
    {
        return new SemanticSpan(kind, identity, text);
    }

    private sealed class BasicSpan(BasicTextSpanKind kind, string text) : Span(text)
    {
        public BasicTextSpanKind Kind { get; } = kind;

        public override void Emit(TextReportBuilderBase builder) => builder.Append(Kind, Text);
    }

    private sealed class SemanticSpan(SemanticTextSpanKind kind, object identity, string text) : Span(text)
    {
        public SemanticTextSpanKind Kind { get; } = kind;
        public object Identity { get; } = identity;

        public override void Emit(TextReportBuilderBase builder) => builder.Append(Kind, Identity, Text);
    }

    protected Span Space(int count) => new BasicSpan(BasicTextSpanKind.Whitespace, new string(' ', count));

    protected void Append(params Span[] spans)
    {
        Requires.NotNull(spans);
        foreach (Span span in spans)
            span.Emit(this);
    }

    protected void AppendLine(params Span[] spans)
    {
        Requires.NotNull(spans);
        foreach (Span span in spans)
            span.Emit(this);
        this.NewLine();
    }

    /// <summary>
    /// Appends one styled text span.
    /// </summary>
    protected void Append(BasicTextSpanKind kind, string text)
    {
        Requires.NotNull(text);

        switch (kind)
        {
            case BasicTextSpanKind.Whitespace:
                // Whitespace spans must consist entirely of whitespace characters:
                Assert.Invariant(text.All(char.IsWhiteSpace), $"{kind} must only have whitespace, but found \"{text}\"");
                break;
            case BasicTextSpanKind.Literal:
            case BasicTextSpanKind.Comment:
                // Literals and comments may have interior whitespace:
                Assert.Invariant(text == text.Trim(), $"{kind} must only have interior whitespace, but found \"{text}\"");
                break;
            default:
                // Everything else must not contain any whitespace characters:
                Assert.Invariant(!text.Any(char.IsWhiteSpace), $"{kind} must not have  whitespace, but found \"{text}\"");
                break;
        }

        this._builder.Append(kind, text);
    }

    /// <summary>
    /// Appends one styled text span that refers to a specific semantic object.
    /// </summary>
    protected void Append(SemanticTextSpanKind kind, object identity, string text)
    {
        // semantic spans must not never contain any whitespace characters:
        Assert.Invariant(!text.Any(char.IsWhiteSpace), $"{kind} must not have  whitespace, but found \"{text}\"");
        this._builder.Append(kind, identity, text);
    }


    protected void AppendLine(BasicTextSpanKind kind, string text)
    {
        this.Append(kind, text);
        this.NewLine();
    }


    protected void AppendLine(SemanticTextSpanKind kind, object identity, string text)
    {
        this.Append(kind, identity, text);
        this.NewLine();
    }

    /// <summary>
    /// Ends the current line.
    /// </summary>
    protected void NewLine() => _builder.NewLine();
}
