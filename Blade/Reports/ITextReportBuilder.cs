using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Blade.Reports;

public enum BasicTextSpanKind
{
    Whitespace,
    Comment,
    Keyword,
    Literal,
    Punctuation,
    Directive,
}

/// <summary>
/// Classifies semantic spans that carry object identity.
/// </summary>
public enum SemanticTextSpanKind
{
    TypeName,
    FunctionName,
    VariableName,
    LayoutName,
    TaskName,
}

/// <summary>
/// Receives text report output as a sequence of styled spans.
/// </summary>
public interface ITextReportBuilder
{
    /// <summary>
    /// Appends one styled text span.
    /// </summary>
    void Append(BasicTextSpanKind kind, string text);

    /// <summary>
    /// Appends one styled text span that refers to a specific semantic object.
    /// </summary>
    void Append(SemanticTextSpanKind kind, object identity, string text);

    /// <summary>
    /// Ends the current line.
    /// </summary>
    void NewLine();
}

public sealed class PlainTextReportBuilder(TextWriter writer) : ITextReportBuilder
{
    private readonly TextWriter _writer = writer;

    public void Append(BasicTextSpanKind kind, string text)
    {
        _writer.Write(text);
    }

    public void Append(SemanticTextSpanKind kind, object identity, string text)
    {
        Requires.NotNull(identity);
        _writer.Write(text);
    }

    public void NewLine()
    {
        _writer.WriteLine();
    }
}


internal sealed class HtmlTextReporter(StringBuilder builder, HtmlSymbolRegistry symbolRegistry) : ITextReportBuilder
{
    private readonly StringBuilder _builder = builder;
    private readonly HtmlSymbolRegistry _symbolRegistry = symbolRegistry;

    public void Append(BasicTextSpanKind kind, string text)
    {
        Requires.NotNull(text);
        if (text.Length == 0)
            return;

        string encoded = WebUtility.HtmlEncode(text);
        string cssClass = GetCssClass(kind);
        if (cssClass == "whitespace")
        {
            _builder.Append(encoded);
            return;
        }

        _builder.Append("<span class=\"");
        _builder.Append(cssClass);
        _builder.Append("\">");
        _builder.Append(encoded);
        _builder.Append("</span>");
    }

    public void Append(SemanticTextSpanKind kind, object identity, string text)
    {
        Requires.NotNull(identity);
        Requires.NotNull(text);
        if (text.Length == 0)
            return;

        string id = _symbolRegistry.GetId(identity);

        _builder.Append("<span class=\"");
        _builder.Append(GetCssClass(kind));
        _builder.Append("\" data-symbol=\"");
        _builder.Append(WebUtility.HtmlEncode(id));
        _builder.Append("\">");
        _builder.Append(WebUtility.HtmlEncode(text));
        _builder.Append("</span>");
    }

    public void NewLine()
    {
        _builder.AppendLine();
    }

    private static string GetCssClass(BasicTextSpanKind kind)
    {
        return kind switch
        {
            BasicTextSpanKind.Whitespace => "whitespace",
            BasicTextSpanKind.Comment => "comment",
            BasicTextSpanKind.Keyword => "kw",
            BasicTextSpanKind.Literal => "literal",
            BasicTextSpanKind.Punctuation => "punct",
            BasicTextSpanKind.Directive => "directive",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }

    private static string GetCssClass(SemanticTextSpanKind kind)
    {
        return kind switch
        {
            SemanticTextSpanKind.TypeName => "type",
            SemanticTextSpanKind.FunctionName => "func",
            SemanticTextSpanKind.VariableName => "var",
            SemanticTextSpanKind.LayoutName => "type",
            SemanticTextSpanKind.TaskName => "func",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }
}

internal sealed class HtmlSymbolRegistry
{
    private readonly Dictionary<object, string> _identityMap = new(ReferenceEqualityComparer.Instance);

    public string GetId(object identity)
    {
        Requires.NotNull(identity);

        if (!_identityMap.TryGetValue(identity, out string? id))
        {
            id = "sym-" + _identityMap.Count.ToString(CultureInfo.InvariantCulture);
            _identityMap.Add(identity, id);
        }

        return id;
    }
}
