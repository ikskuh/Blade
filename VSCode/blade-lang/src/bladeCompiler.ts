import { spawn, type ChildProcessWithoutNullStreams, type SpawnOptionsWithoutStdio } from "node:child_process";
import * as path from "node:path";

export interface BladeDiagnostic {
    readonly code?: string;
    readonly file?: string;
    readonly line?: number;
    readonly message?: string;
}

export interface BladePathVariables {
    readonly cwd?: string;
    readonly env?: Readonly<Record<string, string | undefined>>;
    readonly file?: string;
    readonly workspaceFolder?: string;
}

export interface BladeCompilationRequest {
    readonly cwd: string;
    readonly executablePath: string;
    readonly sourceText: string;
}

export interface BladeCompilationSuccess {
    readonly kind: "success";
    readonly assembly: string;
}

export interface BladeCompilationDiagnosticFailure {
    readonly kind: "diagnostic-error";
    readonly message: string;
}

export interface BladeCompilationExecutionFailure {
    readonly kind: "execution-error";
    readonly message: string;
}

export interface BladeCompilationCancelled {
    readonly kind: "cancelled";
}

export type BladeCompilationOutcome =
    | BladeCompilationSuccess
    | BladeCompilationDiagnosticFailure
    | BladeCompilationExecutionFailure
    | BladeCompilationCancelled;

export interface ActiveCompilation {
    readonly promise: Promise<BladeCompilationOutcome>;
    cancel(): void;
}

export interface SpawnedProcess {
    readonly stdin: NodeJS.WritableStream;
    readonly stdout: NodeJS.ReadableStream;
    readonly stderr: NodeJS.ReadableStream;
    kill(signal?: NodeJS.Signals | number): boolean;
    on(event: "close", listener: (code: number | null, signal: NodeJS.Signals | null) => void): this;
    on(event: "error", listener: (error: Error) => void): this;
}

export type SpawnProcess = (
    command: string,
    args: readonly string[],
    options: SpawnOptionsWithoutStdio) => SpawnedProcess;

interface BladeJsonReport {
    readonly diagnostics?: unknown;
    readonly result?: unknown;
    readonly success?: unknown;
}

export function resolveBladeExecutable(
    configuredPath: string | null | undefined,
    variables: BladePathVariables = {}): string {
    if (typeof configuredPath !== "string")
        return "blade";

    const trimmedPath = configuredPath.trim();
    return trimmedPath.length > 0
        ? expandBladePathVariables(trimmedPath, variables)
        : "blade";
}

export function selectBladeWorkingDirectory(
    documentPath: string | undefined,
    workspaceFolderPaths: readonly string[]): string {
    if (documentPath !== undefined) {
        const matchingWorkspaceFolder = findContainingWorkspaceFolder(documentPath, workspaceFolderPaths);
        if (matchingWorkspaceFolder !== undefined)
            return matchingWorkspaceFolder;

        return path.dirname(documentPath);
    }

    return workspaceFolderPaths[0] ?? process.cwd();
}

export function renderAssemblyHtml(assembly: string): string {
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<style>
body {
    margin: 0;
    padding: 12px;
    background: var(--vscode-editor-background);
    color: var(--vscode-editor-foreground);
}

pre {
    margin: 0;
    white-space: pre-wrap;
    word-break: break-word;
    font-family: var(--vscode-editor-font-family);
    font-size: var(--vscode-editor-font-size);
    line-height: var(--vscode-editor-line-height);
}
</style>
</head>
<body><pre><code>${escapeHtml(assembly)}</code></pre></body>
</html>`;
}

export function interpretBladeCompilerOutput(
    stdout: string,
    stderr: string,
    exitCode: number | null,
    signal: NodeJS.Signals | null): BladeCompilationOutcome {
    let parsedReport: BladeJsonReport;

    try {
        parsedReport = parseJsonReport(stdout);
    }
    catch {
        return {
            kind: "execution-error",
            message: buildExecutionFailureMessage("Blade compiler returned invalid JSON output.", stderr, exitCode, signal),
        };
    }

    if (parsedReport.success === true) {
        if (typeof parsedReport.result !== "string") {
            return {
                kind: "execution-error",
                message: buildExecutionFailureMessage("Blade compiler JSON output did not include assembly text.", stderr, exitCode, signal),
            };
        }

        return {
            kind: "success",
            assembly: parsedReport.result,
        };
    }

    if (parsedReport.success === false) {
        const diagnostics = asDiagnostics(parsedReport.diagnostics);
        return {
            kind: "diagnostic-error",
            message: formatDiagnosticSummary(diagnostics[0]),
        };
    }

    return {
        kind: "execution-error",
        message: buildExecutionFailureMessage("Blade compiler JSON output was missing the success flag.", stderr, exitCode, signal),
    };
}

export function startBladeCompilation(
    request: BladeCompilationRequest,
    spawnProcess: SpawnProcess = spawnBladeProcess): ActiveCompilation {
    let cancelled = false;
    let childProcess: SpawnedProcess | undefined;

    const promise = new Promise<BladeCompilationOutcome>((resolve) => {
        let settled = false;
        let stdout = "";
        let stderr = "";

        const settle = (outcome: BladeCompilationOutcome): void => {
            if (settled)
                return;

            settled = true;
            resolve(outcome);
        };

        try {
            childProcess = spawnProcess(
                request.executablePath,
                ["--json", "-"],
                {
                    cwd: request.cwd,
                    windowsHide: true,
                });
        }
        catch (error) {
            const message = error instanceof Error
                ? error.message
                : "Failed to start the Blade compiler.";

            settle({
                kind: "execution-error",
                message: `Blade compiler invocation failed: ${message}`,
            });
            return;
        }

        childProcess.stdout.on("data", (chunk: Buffer | string) => {
            stdout += chunk.toString();
        });

        childProcess.stderr.on("data", (chunk: Buffer | string) => {
            stderr += chunk.toString();
        });

        childProcess.stdin.on("error", (error: Error) => {
            if (cancelled) {
                settle({ kind: "cancelled" });
                return;
            }

            settle({
                kind: "execution-error",
                message: `Blade compiler invocation failed: ${error.message}`,
            });
        });

        childProcess.on("error", (error: Error) => {
            if (cancelled) {
                settle({ kind: "cancelled" });
                return;
            }

            settle({
                kind: "execution-error",
                message: `Blade compiler invocation failed: ${error.message}`,
            });
        });

        childProcess.on("close", (exitCode, signal) => {
            if (cancelled) {
                settle({ kind: "cancelled" });
                return;
            }

            settle(interpretBladeCompilerOutput(stdout, stderr, exitCode, signal));
        });

        childProcess.stdin.end(request.sourceText);
    });

    return {
        promise,
        cancel: () => {
            cancelled = true;
            childProcess?.kill();
        },
    };
}

function spawnBladeProcess(
    command: string,
    args: readonly string[],
    options: SpawnOptionsWithoutStdio): SpawnedProcess {
    return spawn(command, args, {
        ...options,
        stdio: "pipe",
    }) as ChildProcessWithoutNullStreams;
}

function findContainingWorkspaceFolder(
    documentPath: string,
    workspaceFolderPaths: readonly string[]): string | undefined {
    const sortedFolders = [...workspaceFolderPaths].sort((left, right) => right.length - left.length);

    for (const workspaceFolderPath of sortedFolders) {
        if (isWithinFolder(documentPath, workspaceFolderPath))
            return workspaceFolderPath;
    }

    return undefined;
}

function isWithinFolder(filePath: string, folderPath: string): boolean {
    const relativePath = path.relative(folderPath, filePath);
    return relativePath === ""
        || (!relativePath.startsWith("..") && !path.isAbsolute(relativePath));
}

function escapeHtml(value: string): string {
    return value
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll("\"", "&quot;")
        .replaceAll("'", "&#39;");
}

function expandBladePathVariables(value: string, variables: BladePathVariables): string {
    const workspaceFolder = variables.workspaceFolder;
    const file = variables.file;
    const cwd = variables.cwd ?? process.cwd();

    return value.replaceAll(/\$\{([^}]+)\}/g, (match, name: string) => {
        switch (name) {
            case "workspaceFolder":
                return workspaceFolder ?? match;

            case "workspaceFolderBasename":
                return workspaceFolder !== undefined
                    ? path.basename(workspaceFolder)
                    : match;

            case "file":
                return file ?? match;

            case "fileBasename":
                return file !== undefined
                    ? path.basename(file)
                    : match;

            case "fileDirname":
                return file !== undefined
                    ? path.dirname(file)
                    : match;

            case "fileExtname":
                return file !== undefined
                    ? path.extname(file)
                    : match;

            case "cwd":
                return cwd;

            default:
                if (!name.startsWith("env:"))
                    return match;

                return variables.env?.[name.slice("env:".length)] ?? match;
        }
    });
}

function parseJsonReport(stdout: string): BladeJsonReport {
    const parsed = JSON.parse(stdout) as unknown;
    if (!isRecord(parsed))
        throw new Error("Blade compiler JSON output was not an object.");

    return parsed;
}

function asDiagnostics(value: unknown): readonly BladeDiagnostic[] {
    if (!Array.isArray(value))
        return [];

    const diagnostics: BladeDiagnostic[] = [];
    for (const item of value) {
        if (!isRecord(item))
            continue;

        diagnostics.push({
            code: typeof item.code === "string" ? item.code : undefined,
            file: typeof item.file === "string" ? item.file : undefined,
            line: typeof item.line === "number" ? item.line : undefined,
            message: typeof item.message === "string" ? item.message : undefined,
        });
    }

    return diagnostics;
}

function formatDiagnosticSummary(diagnostic: BladeDiagnostic | undefined): string {
    if (diagnostic === undefined)
        return "Blade compilation failed.";

    const code = diagnostic.code ?? "error";
    const lineSuffix = diagnostic.line !== undefined
        ? ` line ${diagnostic.line}`
        : "";
    const message = diagnostic.message ?? "Blade compilation failed.";

    return `${code}${lineSuffix}: ${message}`;
}

function buildExecutionFailureMessage(
    prefix: string,
    stderr: string,
    exitCode: number | null,
    signal: NodeJS.Signals | null): string {
    const detail = stderr.trim();
    if (detail.length > 0)
        return `${prefix} ${detail}`;

    if (signal !== null)
        return `${prefix} Process terminated by signal ${signal}.`;

    if (exitCode !== null && exitCode !== 0)
        return `${prefix} Process exited with code ${exitCode}.`;

    return prefix;
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null;
}
