using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Blade.Reports;

/// <summary>
/// Renders compilation output as a self-contained HTML report.
/// </summary>
public sealed class HtmlReportWriter : IReportWriter
{
    private static readonly IReadOnlySet<string> SupportedPropertyKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        ThemePropertyKey,
    };
    private static readonly Lazy<string> VsCodeBodyStyle = new(LoadVsCodeBodyStyle);
    private const string ThemePropertyKey = "theme";
    private const string DefaultThemeName = "default";
    private const string VsCodeThemeName = "vscode";

    /// <summary>
    /// Gets the supported property keys for the HTML report writer.
    /// </summary>
    public IReadOnlySet<string> PropertyKeys => SupportedPropertyKeys;

    /// <summary>
    /// Writes the supplied compilation output as HTML.
    /// </summary>
    public void Write(TextWriter writer, CompilationOutput report, IReadOnlyDictionary<string, string> properties)
    {
        Requires.NotNull(writer);
        Requires.NotNull(report);
        Requires.NotNull(properties);

        IReadOnlyList<ReportSection> sections = ReportSectionCatalog.BuildSections(report);
        bool hasFinalAssemblySection = sections.Any(static section => section.Id == "final-asm");
        HtmlReportTheme theme = ResolveTheme(properties);
        HtmlSymbolRegistry symbolRegistry = new();
        StringBuilder sb = new();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <title>Blade Report</title>");
        sb.AppendLine("  <style>");
        AppendThemeCss(sb);
        sb.AppendLine("        body { background-color: var(--report-background); color: var(--report-foreground); }");
        sb.AppendLine("        h1 { border-bottom: 1px solid var(--report-border); }");
        sb.AppendLine("        h1:has(+h2.filename) { margin-bottom: 0; }");
        sb.AppendLine("        h2.filename { margin-top: 0; font-family: monospace; color: var(--report-foreground); }");
        sb.AppendLine("        .diagnostics { margin-bottom: 1rem; padding: 0.75rem; border: 1px solid var(--report-border); background-color: var(--report-panel-background); color: var(--report-foreground); }");
        sb.AppendLine("        .diagnostics h3 { margin-top: 0; }");
        sb.AppendLine("        .diagnostics ul { margin: 0; padding-left: 1.5rem; }");
        sb.AppendLine("        .diagnostics li + li { margin-top: 0.5rem; }");
        sb.AppendLine("        .diagnostics a { color: var(--report-keyword); text-decoration: none; }");
        sb.AppendLine("        .diagnostics a:hover { text-decoration: underline; }");
        sb.AppendLine("        .diagnostics .code { color: var(--report-literal); font-family: monospace; }");
        sb.AppendLine("        .diagnostics .message { color: var(--report-foreground); }");
        sb.AppendLine("        .metrics-table { width: 100%; border-collapse: collapse; }");
        sb.AppendLine("        .metrics-table th, .metrics-table td { padding: 0.35rem 0.5rem; border-bottom: 1px solid var(--report-border); text-align: left; }");
        sb.AppendLine("        .metrics-table th { color: var(--report-keyword); font-weight: 600; }");
        sb.AppendLine("        .metrics-table td { color: var(--report-literal); font-family: monospace; }");
        sb.AppendLine("        main { display: flex; gap: 0.25rem; flex-wrap: wrap; }");
        sb.AppendLine("        main::after { content: \"\"; order: 2; flex-basis: 100%; }");
        sb.AppendLine("        main section { display: contents; }");
        sb.AppendLine("        section label { order: 1; flex: 1; border: 2px solid var(--report-border); border-radius: 5px; background-color: var(--report-tab-background); color: var(--report-foreground); cursor: pointer; white-space: nowrap; line-height: 1.5rem; font-size: 90%; display: flex; flex-direction: row; align-items: center; }");
        sb.AppendLine("        section label:has(input:checked) { background-color: var(--report-tab-active-background); color: var(--report-tab-active-foreground); }");
        sb.AppendLine("        section .content { order: 3; flex: 1; display: none; overflow-x: auto; border: 1px solid var(--report-border); background-color: var(--report-panel-background); color: var(--report-foreground); }");
        sb.AppendLine("        section:has(input:checked) .content { display: block; }");
        sb.AppendLine("        section .content h3 { margin: 0; padding: 0.5rem; border-bottom: 1px solid var(--report-border); font-size: 100%; background: var(--report-panel-header-background); color: var(--report-panel-header-foreground); display: flex; }");
        sb.AppendLine("        section .content h3 :first-child { flex: 1; }");
        sb.AppendLine("        section .content pre { margin: 0.25rem; background-color: var(--report-panel-background); color: var(--report-foreground); }");
        sb.AppendLine("        pre.code .comment { color: var(--report-comment); }");
        sb.AppendLine("        pre.code .kw { color: var(--report-keyword); }");
        sb.AppendLine("        pre.code .type { color: var(--report-type); }");
        sb.AppendLine("        pre.code .func { color: var(--report-function); }");
        sb.AppendLine("        pre.code .var { color: var(--report-variable); }");
        sb.AppendLine("        pre.code .literal { color: var(--report-literal); }");
        sb.AppendLine("        pre.code .directive { color: var(--report-directive); }");
        sb.AppendLine("        pre.code .symbol { color: var(--report-symbol); }");
        sb.AppendLine("        pre.code .punct { color: var(--report-punctuation); }");
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
        sb.AppendLine("                style.textContent += `body:has([data-symbol=\"${attr}\"]:hover) [data-symbol=\"${attr}\"] { background: var(--report-hover-highlight); color: var(--report-hover-highlight-foreground); }\\n`;");
        sb.AppendLine("            }");
        sb.AppendLine("            document.head.appendChild(style);");
        sb.AppendLine("        });");
        sb.AppendLine("  </script>");
        sb.AppendLine("</head>");
        AppendBodyOpenTag(sb, theme);
        sb.AppendLine("    <h1>Build Report</h1>");
        sb.AppendLine("    <h2 class=\"filename\">" + WebUtility.HtmlEncode(report.Source.FilePath) + "</h2>");
        if (report.Diagnostics.Count > 0)
            AppendDiagnostics(sb, report);
        sb.AppendLine("    <main>");

        if (report.Crash is not null)
            WriteTextSection(sb, symbolRegistry, "crash", "Crash", writer => WriteCrash(writer, report.Crash), isChecked: !hasFinalAssemblySection);
        WriteMetricsSection(sb, "metrics", "Metrics", report.Metrics);

        foreach (ReportSection section in sections)
            WriteTextSection(sb, symbolRegistry, section.Id, section.Title, section.Emit, isChecked: section.Id == "final-asm", section.FileName);

        sb.AppendLine("    </main>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        writer.Write(sb.ToString());
    }

    private static HtmlReportTheme ResolveTheme(IReadOnlyDictionary<string, string> properties)
    {
        Requires.NotNull(properties);

        if (!properties.TryGetValue(ThemePropertyKey, out string? themeName))
            return HtmlReportTheme.Default;

        return themeName switch
        {
            DefaultThemeName => HtmlReportTheme.Default,
            VsCodeThemeName => HtmlReportTheme.VsCode,
            _ => throw new InvalidDataException($"unknown html report theme '{themeName}'; expected '{DefaultThemeName}' or '{VsCodeThemeName}'."),
        };
    }

    private static void AppendThemeCss(StringBuilder sb)
    {
        sb.AppendLine("        :root {");
        sb.AppendLine("            --report-background: white;");
        sb.AppendLine("            --report-foreground: black;");
        sb.AppendLine("            --report-border: silver;");
        sb.AppendLine("            --report-tab-background: #EEEEEE;");
        sb.AppendLine("            --report-tab-active-background: lightblue;");
        sb.AppendLine("            --report-tab-active-foreground: black;");
        sb.AppendLine("            --report-panel-background: white;");
        sb.AppendLine("            --report-panel-header-background: lightblue;");
        sb.AppendLine("            --report-panel-header-foreground: black;");
        sb.AppendLine("            --report-hover-highlight: yellow;");
        sb.AppendLine("            --report-hover-highlight-foreground: black;");
        sb.AppendLine("            --report-comment: green;");
        sb.AppendLine("            --report-keyword: blue;");
        sb.AppendLine("            --report-type: darkblue;");
        sb.AppendLine("            --report-function: darkred;");
        sb.AppendLine("            --report-variable: darkviolet;");
        sb.AppendLine("            --report-literal: #8b3a00;");
        sb.AppendLine("            --report-directive: #7c3aed;");
        sb.AppendLine("            --report-symbol: #0f766e;");
        sb.AppendLine("            --report-punctuation: #6b7280;");
        sb.AppendLine("        }");
        sb.AppendLine("        body.theme-vscode {");
        sb.AppendLine("            --report-background: var(--vscode-editor-background);");
        sb.AppendLine("            --report-foreground: var(--vscode-editor-foreground);");
        sb.AppendLine("            --report-border: var(--vscode-contrastBorder);");
        sb.AppendLine("            --report-tab-background: var(--vscode-button-secondaryBackground);");
        sb.AppendLine("            --report-tab-active-background: var(--vscode-list-activeSelectionBackground);");
        sb.AppendLine("            --report-tab-active-foreground: var(--vscode-list-activeSelectionForeground);");
        sb.AppendLine("            --report-panel-background: var(--vscode-editorWidget-background);");
        sb.AppendLine("            --report-panel-header-background: var(--vscode-quickInputTitle-background);");
        sb.AppendLine("            --report-panel-header-foreground: var(--vscode-editorWidget-foreground);");
        sb.AppendLine("            --report-hover-highlight: var(--vscode-editor-selectionBackground);");
        sb.AppendLine("            --report-hover-highlight-foreground: var(--vscode-editor-selectionForeground);");
        sb.AppendLine("            --report-comment: var(--vscode-descriptionForeground);");
        sb.AppendLine("            --report-keyword: var(--vscode-textLink-foreground);");
        sb.AppendLine("            --report-type: var(--vscode-symbolIcon-classForeground);");
        sb.AppendLine("            --report-function: var(--vscode-symbolIcon-functionForeground);");
        sb.AppendLine("            --report-variable: var(--vscode-symbolIcon-variableForeground);");
        sb.AppendLine("            --report-literal: var(--vscode-textPreformat-foreground);");
        sb.AppendLine("            --report-directive: var(--vscode-badge-foreground);");
        sb.AppendLine("            --report-symbol: var(--vscode-symbolIcon-fieldForeground);");
        sb.AppendLine("            --report-punctuation: var(--vscode-editorLineNumber-foreground);");
        sb.AppendLine("        }");
    }

    private static void AppendBodyOpenTag(StringBuilder sb, HtmlReportTheme theme)
    {
        switch (theme)
        {
            case HtmlReportTheme.Default:
                sb.AppendLine("<body class=\"theme-default\">");
                return;
            case HtmlReportTheme.VsCode:
                sb.AppendLine("<body class=\"theme-vscode\" style=\"" + WebUtility.HtmlEncode(VsCodeBodyStyle.Value) + "\">");
                return;
            default:
                Assert.Unreachable(); // pragma: force-coverage
                return;
        }
    }

    private static string LoadVsCodeBodyStyle()
    {
        string filePath = FindVsCodeCssPath();
        StringBuilder style = new();
        foreach (string line in File.ReadLines(filePath))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("--", StringComparison.Ordinal))
                continue;

            int separatorIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0 || !trimmed.EndsWith(';'))
                continue;

            style.Append(trimmed);
            style.Append(' ');
        }

        if (style.Length == 0)
            throw new InvalidDataException($"The VSCode theme file '{filePath}' did not contain any CSS variable declarations.");

        return style.ToString().TrimEnd();
    }

    private static string FindVsCodeCssPath()
    {
        string currentDirectory = Environment.CurrentDirectory;
        string currentDirectoryCandidate = Path.Combine(currentDirectory, "Work", "VSCode.css");
        if (File.Exists(currentDirectoryCandidate))
            return currentDirectoryCandidate;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "Work", "VSCode.css");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate Work/VSCode.css for the VSCode HTML report theme.", currentDirectoryCandidate);
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

    private static void AppendDiagnostics(StringBuilder sb, CompilationOutput report)
    {
        sb.AppendLine("    <section class=\"diagnostics\">");
        sb.AppendLine("        <h3>Diagnostics</h3>");

        if (report.Diagnostics.Count == 0)
        {
            sb.AppendLine("        <p>None.</p>");
            sb.AppendLine("    </section>");
            return;
        }

        sb.AppendLine("        <ul>");
        foreach (Blade.Diagnostics.Diagnostic diagnostic in report.Diagnostics)
        {
            sb.Append("            <li>");
            if (diagnostic.IsLocated)
            {
                Blade.Source.SourceLocation location = diagnostic.GetLocation();
                string locationText = WebUtility.HtmlEncode(location.ToString());
                string href = BuildFileHref(location);
                sb.Append("<a href=\"");
                sb.Append(WebUtility.HtmlEncode(href));
                sb.Append("\">");
                sb.Append(locationText);
                sb.Append("</a>: ");
            }

            sb.Append("<span class=\"code\">");
            sb.Append(WebUtility.HtmlEncode(diagnostic.FormatCode()));
            sb.Append("</span> ");
            sb.Append("<span class=\"message\">");
            sb.Append(WebUtility.HtmlEncode(diagnostic.Message));
            sb.Append("</span>");
            sb.AppendLine("</li>");
        }

        sb.AppendLine("        </ul>");
        sb.AppendLine("    </section>");
    }

    private static void WriteMetricsSection(StringBuilder sb, string id, string title, CompilationMetrics metrics)
    {
        sb.AppendLine("        <section>");
        sb.AppendLine("            <label><input type=\"checkbox\">" + WebUtility.HtmlEncode(title) + "</label>");
        sb.AppendLine("            <div class=\"content\">");
        sb.AppendLine("                <h3><span>" + WebUtility.HtmlEncode(title) + "</span><small>" + WebUtility.HtmlEncode(id) + "</small></h3>");
        sb.AppendLine("                <table class=\"metrics-table\">");
        sb.AppendLine("                    <tbody>");
        AppendMetricRow(sb, "tokens", metrics.TokenCount.ToString(CultureInfo.InvariantCulture));
        AppendMetricRow(sb, "members", metrics.MemberCount.ToString(CultureInfo.InvariantCulture));
        AppendMetricRow(sb, "bound-fns", metrics.BoundFunctionCount.ToString(CultureInfo.InvariantCulture));
        AppendMetricRow(sb, "mir-fns", metrics.MirFunctionCount.ToString(CultureInfo.InvariantCulture));
        AppendMetricRow(sb, "time-ms", metrics.TimeMs.ToString("F2", CultureInfo.InvariantCulture));
        sb.AppendLine("                    </tbody>");
        sb.AppendLine("                </table>");
        sb.AppendLine("            </div>");
        sb.AppendLine("        </section>");
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

    private static void AppendMetricRow(StringBuilder sb, string name, string value)
    {
        sb.AppendLine("                        <tr>");
        sb.AppendLine("                            <th scope=\"row\">" + WebUtility.HtmlEncode(name) + "</th>");
        sb.AppendLine("                            <td>" + WebUtility.HtmlEncode(value) + "</td>");
        sb.AppendLine("                        </tr>");
    }

    private static string BuildFileHref(Blade.Source.SourceLocation location)
    {
        string filePath = Path.IsPathRooted(location.FilePath)
            ? location.FilePath
            : Path.GetFullPath(location.FilePath);
        Uri fileUri = new(filePath, UriKind.Absolute);
        return fileUri.AbsoluteUri + "#L" + location.Line.ToString(CultureInfo.InvariantCulture);
    }

    private enum HtmlReportTheme
    {
        Default,
        VsCode,
    }
}
