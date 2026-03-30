import { type BladeCompilationReport, type BladeDiagnostic } from "./bladeCompiler";

export interface DiagnosticLink {
    readonly href: string;
    readonly label: string;
}

interface DumpSection {
    readonly content: string;
    readonly expanded: boolean;
    readonly title: string;
}

const OrderedDumpSections = [
    { key: "bound", title: "Bound" },
    { key: "mir-preopt", title: "MIR (Preopt)" },
    { key: "mir", title: "MIR" },
    { key: "lir-preopt", title: "LIR (Preopt)" },
    { key: "lir", title: "LIR" },
    { key: "asmir-preopt", title: "ASMIR (Preopt)" },
    { key: "asmir", title: "ASMIR" },
] as const;

const PreferredMetricOrder = [
    "token_count",
    "member_count",
    "bound_function_count",
    "mir_function_count",
    "time_ms",
] as const;

export function renderCompilationReportHtml(
    report: BladeCompilationReport,
    resolveDiagnosticLink: (diagnostic: BladeDiagnostic) => DiagnosticLink | undefined): string {
    const sections: string[] = [];

    if (report.diagnostics.length > 0)
        sections.push(renderDiagnosticsSection(report.diagnostics, resolveDiagnosticLink));

    if (report.result !== null)
        sections.push(renderCodeSection("Final Assembly", report.result, true));

    for (const section of buildDumpSections(report))
        sections.push(renderCodeSection(section.title, section.content, section.expanded));

    sections.push(renderMetricsSection(report.metrics));

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<style>
:root {
    color-scheme: light dark;
}

body {
    margin: 0;
    padding: 16px;
    background: var(--vscode-editor-background);
    color: var(--vscode-editor-foreground);
    font-family: var(--vscode-font-family);
}

.preview {
    display: flex;
    flex-direction: column;
    gap: 12px;
}

details {
    border: 1px solid var(--vscode-panel-border);
    border-radius: 8px;
    background: var(--vscode-sideBar-background);
    overflow: hidden;
}

summary {
    cursor: pointer;
    font-weight: 600;
    padding: 10px 12px;
    user-select: none;
}

.section-body {
    padding: 0 12px 12px 12px;
}

pre {
    margin: 0;
    padding: 12px;
    border-radius: 6px;
    overflow-x: auto;
    background: var(--vscode-textCodeBlock-background);
}

code {
    font-family: var(--vscode-editor-font-family);
    font-size: var(--vscode-editor-font-size);
    line-height: var(--vscode-editor-line-height);
}

table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.95em;
}

th,
td {
    padding: 8px 10px;
    border-top: 1px solid var(--vscode-panel-border);
    text-align: left;
    vertical-align: top;
}

th {
    font-weight: 600;
    color: var(--vscode-descriptionForeground);
}

tbody tr:hover {
    background: var(--vscode-list-hoverBackground);
}

a {
    color: var(--vscode-textLink-foreground);
    text-decoration: none;
}

a:hover {
    text-decoration: underline;
}
</style>
</head>
<body>
<div class="preview">
${sections.join("\n")}
</div>
</body>
</html>`;
}

function buildDumpSections(report: BladeCompilationReport): readonly DumpSection[] {
    const sections: DumpSection[] = [];
    for (const { key, title } of OrderedDumpSections) {
        const dump = report.dumps[key];
        if (typeof dump === "string")
            sections.push({ content: dump, expanded: false, title });
    }

    return sections;
}

function renderDiagnosticsSection(
    diagnostics: readonly BladeDiagnostic[],
    resolveDiagnosticLink: (diagnostic: BladeDiagnostic) => DiagnosticLink | undefined): string {
    const rows = diagnostics.map((diagnostic) => {
        const locationLabel = formatLocationLabel(diagnostic);
        const link = resolveDiagnosticLink(diagnostic);
        const locationCell = link !== undefined
            ? `<a href="${escapeHtmlAttribute(link.href)}">${escapeHtml(link.label)}</a>`
            : escapeHtml(locationLabel);

        return `<tr>
<td>${escapeHtml(diagnostic.code ?? "")}</td>
<td>${escapeHtml(diagnostic.message ?? "")}</td>
<td>${locationCell}</td>
</tr>`;
    }).join("\n");

    return `<details>
<summary>Diagnostics</summary>
<div class="section-body">
<table>
<thead>
<tr><th>Code</th><th>Message</th><th>Location</th></tr>
</thead>
<tbody>
${rows}
</tbody>
</table>
</div>
</details>`;
}

function renderCodeSection(title: string, content: string, expanded: boolean): string {
    const openAttribute = expanded ? " open" : "";
    return `<details${openAttribute}>
<summary>${escapeHtml(title)}</summary>
<div class="section-body">
<pre><code>${escapeHtml(content)}</code></pre>
</div>
</details>`;
}

function renderMetricsSection(metrics: Readonly<Record<string, unknown>>): string {
    const orderedEntries = orderMetrics(metrics);
    const rows = orderedEntries.map(([name, value]) => `<tr>
<td>${escapeHtml(name)}</td>
<td>${escapeHtml(formatMetricValue(value))}</td>
</tr>`).join("\n");

    return `<details>
<summary>Metrics</summary>
<div class="section-body">
<table>
<thead>
<tr><th>Metric</th><th>Value</th></tr>
</thead>
<tbody>
${rows}
</tbody>
</table>
</div>
</details>`;
}

function orderMetrics(metrics: Readonly<Record<string, unknown>>): readonly [string, unknown][] {
    const preferredEntries: [string, unknown][] = [];
    const seen = new Set<string>();

    for (const key of PreferredMetricOrder) {
        if (!(key in metrics))
            continue;

        preferredEntries.push([key, metrics[key]]);
        seen.add(key);
    }

    const remainingEntries = Object.entries(metrics)
        .filter(([key]) => !seen.has(key))
        .sort(([left], [right]) => left.localeCompare(right));

    return [...preferredEntries, ...remainingEntries];
}

function formatLocationLabel(diagnostic: BladeDiagnostic): string {
    if (diagnostic.file === undefined)
        return "";

    if (diagnostic.line === undefined)
        return diagnostic.file;

    return `${diagnostic.file}:${diagnostic.line}`;
}

function formatMetricValue(value: unknown): string {
    if (typeof value === "number")
        return Number.isInteger(value)
            ? value.toString()
            : value.toFixed(2);

    if (typeof value === "boolean")
        return value ? "true" : "false";

    if (value === null || value === undefined)
        return "";

    return String(value);
}

function escapeHtml(value: string): string {
    return value
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll("\"", "&quot;")
        .replaceAll("'", "&#39;");
}

function escapeHtmlAttribute(value: string): string {
    return escapeHtml(value);
}
