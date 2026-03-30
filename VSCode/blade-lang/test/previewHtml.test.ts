import test from "node:test";
import assert from "node:assert/strict";
import { renderCompilationReportHtml } from "../src/previewHtml";

test("renderCompilationReportHtml orders sections and includes clickable diagnostic links", () => {
    const html = renderCompilationReportHtml(
        {
            diagnostics: [
                {
                    code: "E0202",
                    file: "/workspace/main.blade",
                    line: 3,
                    message: "Expected expression.",
                },
            ],
            dumps: {
                "asmir": "asmir dump",
                "asmir-preopt": "asmir preopt dump",
                "bound": "bound dump",
                "lir": "lir dump",
                "lir-preopt": "lir preopt dump",
                "mir": "mir dump",
                "mir-preopt": "mir preopt dump",
            },
            metrics: {
                member_count: 2,
                time_ms: 1.25,
                token_count: 10,
            },
            result: "org 0",
            success: false,
        },
        () => ({
            href: "command:blade.openDiagnosticLocation?test",
            label: "/workspace/main.blade:3",
        }));

    const diagnosticsIndex = html.indexOf("<summary>Diagnostics</summary>");
    const finalAssemblyIndex = html.indexOf("<summary>Final Assembly</summary>");
    const boundIndex = html.indexOf("<summary>Bound</summary>");
    const mirPreoptIndex = html.indexOf("<summary>MIR (Preopt)</summary>");
    const mirIndex = html.indexOf("<summary>MIR</summary>");
    const lirPreoptIndex = html.indexOf("<summary>LIR (Preopt)</summary>");
    const lirIndex = html.indexOf("<summary>LIR</summary>");
    const asmirPreoptIndex = html.indexOf("<summary>ASMIR (Preopt)</summary>");
    const asmirIndex = html.indexOf("<summary>ASMIR</summary>");
    const metricsIndex = html.indexOf("<summary>Metrics</summary>");

    assert.ok(diagnosticsIndex < finalAssemblyIndex);
    assert.ok(finalAssemblyIndex < boundIndex);
    assert.ok(boundIndex < mirPreoptIndex);
    assert.ok(mirPreoptIndex < mirIndex);
    assert.ok(mirIndex < lirPreoptIndex);
    assert.ok(lirPreoptIndex < lirIndex);
    assert.ok(lirIndex < asmirPreoptIndex);
    assert.ok(asmirPreoptIndex < asmirIndex);
    assert.ok(asmirIndex < metricsIndex);
    assert.match(html, /<details open>\s*<summary>Final Assembly<\/summary>/);
    assert.match(html, /<a href="command:blade\.openDiagnosticLocation\?test">\/workspace\/main\.blade:3<\/a>/);
    assert.match(html, /<table>/);
    assert.match(html, /<pre><code>bound dump<\/code><\/pre>/);
});
