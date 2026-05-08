using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Blade.Reports;

/// <summary>
/// Renders compilation output as a self-contained HTML report.
/// </summary>
public sealed class HtmlReportWriter : IReportWriter
{
    /// <summary>
    /// Writes the supplied compilation output as HTML.
    /// </summary>
    public void Write(TextWriter writer, CompilationOutput report)
    {
        Requires.NotNull(writer);
        Requires.NotNull(report);

        IReadOnlyList<ReportSection> sections = ReportSectionCatalog.BuildSections(report);
        HtmlSymbolRegistry symbolRegistry = new();
        StringBuilder sb = new();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <title>Blade Report</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("        h1 { border-bottom: 1px solid silver; }");
        sb.AppendLine("        h1:has(+h2.filename) { margin-bottom: 0; }");
        sb.AppendLine("        h2.filename { margin-top: 0; font-family: monospace; }");
        sb.AppendLine("        main { display: flex; gap: 0.25rem; flex-wrap: wrap; }");
        sb.AppendLine("        main::after { content: \"\"; order: 2; flex-basis: 100%; }");
        sb.AppendLine("        main section { display: contents; }");
        sb.AppendLine("        section label { order: 1; flex: 1; border: 2px silver ridge; border-radius: 5px; background-color: #EEE; cursor: pointer; white-space: nowrap; line-height: 1.5rem; font-size: 90%; display: flex; flex-direction: row; align-items: center; }");
        sb.AppendLine("        section label:has(input:checked) { background-color: lightblue; }");
        sb.AppendLine("        section .content { order: 3; flex: 1; display: none; overflow-x: scroll; border: 1px solid silver; }");
        sb.AppendLine("        section:has(input:checked) .content { display: block; }");
        sb.AppendLine("        section .content h3 { margin: 0; padding: 0.5rem; border-bottom: 1px solid silver; font-size: 100%; background: lightblue; display: flex; }");
        sb.AppendLine("        section .content h3 :first-child { flex: 1; }");
        sb.AppendLine("        section .content pre { margin: 0.25rem; }");
        sb.AppendLine("        pre.code .comment { color: green; }");
        sb.AppendLine("        pre.code .kw { color: blue; }");
        sb.AppendLine("        pre.code .type { color: darkblue; }");
        sb.AppendLine("        pre.code .func { color: darkred; }");
        sb.AppendLine("        pre.code .var { color: darkviolet; }");
        sb.AppendLine("        pre.code .literal { color: #8b3a00; }");
        sb.AppendLine("        pre.code .directive { color: #7c3aed; }");
        sb.AppendLine("        pre.code .symbol { color: #0f766e; }");
        sb.AppendLine("        pre.code .punct { color: #6b7280; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("  <script defer>");
        sb.AppendLine("        window.addEventListener(\"DOMContentLoaded\", () => {");
        sb.AppendLine("            const symbols = new Set();");
        sb.AppendLine("            for (const el of document.querySelectorAll(\"[data-symbol]\")) {");
        sb.AppendLine("                symbols.add(el.dataset.symbol);");
        sb.AppendLine("            }");
        sb.AppendLine("            const style = document.createElement(\"style\");");
        sb.AppendLine("            for (const symbol of symbols) {");
        sb.AppendLine("                const attr = CSS.escape(symbol);");
        sb.AppendLine("                style.textContent += `body:has([data-symbol=\"${attr}\"]:hover) [data-symbol=\"${attr}\"] { background: yellow; }\\n`;");
        sb.AppendLine("            }");
        sb.AppendLine("            document.head.appendChild(style);");
        sb.AppendLine("        });");
        sb.AppendLine("  </script>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("    <h1>Build Report</h1>");
        sb.AppendLine("    <h2 class=\"filename\">" + WebUtility.HtmlEncode(report.Source.FilePath) + "</h2>");
        sb.AppendLine("    <main>");

        WriteTextSection(sb, symbolRegistry, "status", "Status", writer => WriteStatus(writer, report), isChecked: true);
        WriteTextSection(sb, symbolRegistry, "diagnostics", "Diagnostics", writer => WriteDiagnostics(writer, report), isChecked: report.Diagnostics.Count > 0);
        if (report.Crash is not null)
            WriteTextSection(sb, symbolRegistry, "crash", "Crash", writer => WriteCrash(writer, report.Crash), isChecked: true);
        WriteTextSection(sb, symbolRegistry, "metrics", "Metrics", writer => WriteMetrics(writer, report.Metrics), isChecked: false);

        foreach (ReportSection section in sections)
            WriteTextSection(sb, symbolRegistry, section.Id, section.Title, section.Emit, isChecked: false, section.FileName);

        sb.AppendLine("    </main>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        writer.Write(sb.ToString());
    }

    private static void WriteTextSection(
        StringBuilder sb,
        HtmlSymbolRegistry symbolRegistry,
        string id,
        string title,
        System.Action<ITextReportBuilder> emit,
        bool isChecked,
        string? fileName = null)
    {
        sb.AppendLine("        <section>");
        sb.AppendLine("            <label><input type=\"checkbox\"" + (isChecked ? " checked" : string.Empty) + ">" + WebUtility.HtmlEncode(title) + "</label>");
        sb.AppendLine("            <div class=\"content\">");
        sb.AppendLine("                <h3><span>" + WebUtility.HtmlEncode(title) + "</span><small>" + WebUtility.HtmlEncode(fileName ?? id) + "</small></h3>");
        sb.AppendLine("                <pre class=\"code\">");
        HtmlTextReporter htmlReporter = new(sb, symbolRegistry);
        emit(htmlReporter);
        sb.AppendLine("                </pre>");
        sb.AppendLine("            </div>");
        sb.AppendLine("        </section>");
    }

    private static void WriteStatus(ITextReportBuilder writer, CompilationOutput report)
    {
        writer.Append(BasicTextSpanKind.Keyword, "status");
        writer.Append(BasicTextSpanKind.Punctuation, ":");
        writer.Append(BasicTextSpanKind.Whitespace, " ");
        writer.Append(BasicTextSpanKind.Literal, FormatStatus(report.Status));
        writer.NewLine();
        writer.Append(BasicTextSpanKind.Keyword, "source");
        writer.Append(BasicTextSpanKind.Punctuation, ":");
        writer.Append(BasicTextSpanKind.Whitespace, " ");
        writer.Append(BasicTextSpanKind.Comment, report.Source.FilePath);
    }

    private static void WriteDiagnostics(ITextReportBuilder writer, CompilationOutput report)
    {
        if (report.Diagnostics.Count == 0)
        {
            writer.Append(BasicTextSpanKind.Keyword, "diagnostics");
            writer.Append(BasicTextSpanKind.Punctuation, ":");
            writer.Append(BasicTextSpanKind.Whitespace, " ");
            writer.Append(BasicTextSpanKind.Keyword, "none");
            return;
        }

        foreach (Blade.Diagnostics.Diagnostic diagnostic in report.Diagnostics)
        {
            if (diagnostic.IsLocated)
            {
                writer.Append(BasicTextSpanKind.Comment, diagnostic.GetLocation().ToString());
                writer.Append(BasicTextSpanKind.Punctuation, ":");
                writer.Append(BasicTextSpanKind.Whitespace, " ");
            }

            writer.Append(BasicTextSpanKind.Comment, diagnostic.ToString());
            writer.NewLine();
        }
    }

    private static void WriteCrash(ITextReportBuilder writer, CompilationCrashInfo crash)
    {
        writer.Append(BasicTextSpanKind.Keyword, "type");
        writer.Append(BasicTextSpanKind.Punctuation, ":");
        writer.Append(BasicTextSpanKind.Whitespace, " ");
        writer.Append(BasicTextSpanKind.Comment, crash.ExceptionType);
        writer.NewLine();
        writer.Append(BasicTextSpanKind.Keyword, "message");
        writer.Append(BasicTextSpanKind.Punctuation, ":");
        writer.Append(BasicTextSpanKind.Whitespace, " ");
        writer.Append(BasicTextSpanKind.Comment, crash.Message);
        if (!string.IsNullOrEmpty(crash.StackTrace))
        {
            writer.NewLine();
            writer.Append(BasicTextSpanKind.Keyword, "stack");
            writer.Append(BasicTextSpanKind.Punctuation, ":");
            foreach (string line in crash.StackTrace.Split('\n'))
            {
                writer.NewLine();
                writer.Append(BasicTextSpanKind.Comment, line.TrimEnd('\r'));
            }
        }
    }

    private static void WriteMetrics(ITextReportBuilder writer, CompilationMetrics metrics)
    {
        AppendMetric(writer, "tokens", metrics.TokenCount.ToString(CultureInfo.InvariantCulture));
        writer.NewLine();
        AppendMetric(writer, "members", metrics.MemberCount.ToString(CultureInfo.InvariantCulture));
        writer.NewLine();
        AppendMetric(writer, "bound-fns", metrics.BoundFunctionCount.ToString(CultureInfo.InvariantCulture));
        writer.NewLine();
        AppendMetric(writer, "mir-fns", metrics.MirFunctionCount.ToString(CultureInfo.InvariantCulture));
        writer.NewLine();
        AppendMetric(writer, "time-ms", metrics.TimeMs.ToString("F2", CultureInfo.InvariantCulture));
    }

    private static void AppendMetric(ITextReportBuilder writer, string name, string value)
    {
        writer.Append(BasicTextSpanKind.Keyword, name);
        writer.Append(BasicTextSpanKind.Punctuation, ":");
        writer.Append(BasicTextSpanKind.Whitespace, " ");
        writer.Append(BasicTextSpanKind.Literal, value);
    }

    private static string FormatStatus(CompilationStatus status)
    {
        return status switch
        {
            CompilationStatus.Succeeded => "succeeded",
            CompilationStatus.Failed => "failed",
            CompilationStatus.Crashed => "crashed",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }
}
