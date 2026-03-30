import * as path from "node:path";
import * as vscode from "vscode";
import {
    renderAssemblyHtml,
    resolveBladeExecutable,
    selectBladeWorkingDirectory,
    startBladeCompilation,
    type BladeCompilationOutcome,
} from "./bladeCompiler";
import { LatestOnlyJobRunner } from "./latestOnlyJobRunner";

const PreviewRefreshDelayMs = 250;

export class BladePreviewController implements vscode.Disposable {
    private readonly sessions = new Map<string, BladePreviewSession>();

    public openPreviewToSide(resource?: vscode.Uri): void {
        const editor = resolveTargetEditor(resource);
        if (editor === undefined || editor.document.languageId !== "blade") {
            void vscode.window.showErrorMessage("Open a Blade document before opening the preview.");
            return;
        }

        const key = editor.document.uri.toString();
        let session = this.sessions.get(key);
        if (session === undefined) {
            session = new BladePreviewSession(
                editor.document.uri,
                () => {
                    this.sessions.delete(key);
                });
            this.sessions.set(key, session);
        }

        session.reveal();
        session.refreshNow(editor.document);
    }

    public dispose(): void {
        for (const session of this.sessions.values())
            session.dispose();

        this.sessions.clear();
    }
}

class BladePreviewSession implements vscode.Disposable {
    private readonly refreshRunner = new LatestOnlyJobRunner<BladeCompilationOutcome>();
    private readonly panel: vscode.WebviewPanel;
    private readonly disposables: vscode.Disposable[] = [];
    private refreshTimer: NodeJS.Timeout | undefined;
    private disposed = false;

    public constructor(
        private readonly documentUri: vscode.Uri,
        private readonly onDisposed: () => void) {
        this.panel = vscode.window.createWebviewPanel(
            "blade.preview",
            buildPreviewTitle(documentUri),
            {
                viewColumn: vscode.ViewColumn.Beside,
                preserveFocus: true,
            },
            {
                enableFindWidget: true,
            });
        this.panel.webview.html = renderAssemblyHtml("");

        this.disposables.push(this.panel.onDidDispose(() => {
            this.disposeCore(false);
        }));
        this.disposables.push(vscode.workspace.onDidChangeTextDocument((event) => {
            if (!sameUri(event.document.uri, this.documentUri))
                return;

            this.scheduleRefresh(event.document);
        }));
    }

    public dispose(): void {
        this.disposeCore(true);
    }

    public reveal(): void {
        this.panel.reveal(vscode.ViewColumn.Beside, true);
    }

    public refreshNow(document?: vscode.TextDocument): void {
        const targetDocument = document ?? findOpenDocument(this.documentUri);
        if (targetDocument === undefined) {
            void vscode.window.showErrorMessage("The Blade preview could not find the source document.");
            return;
        }

        this.panel.title = buildPreviewTitle(targetDocument.uri);
        this.clearPendingRefresh();
        void this.runRefresh(targetDocument);
    }

    private scheduleRefresh(document: vscode.TextDocument): void {
        this.clearPendingRefresh();
        this.refreshTimer = setTimeout(() => {
            this.refreshTimer = undefined;
            void this.runRefresh(document);
        }, PreviewRefreshDelayMs);
    }

    private async runRefresh(document: vscode.TextDocument): Promise<void> {
        const workspaceFolderPaths = vscode.workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [];
        const documentPath = document.isUntitled ? undefined : document.uri.fsPath;
        const workingDirectory = selectBladeWorkingDirectory(documentPath, workspaceFolderPaths);
        const workspaceFolderPath = vscode.workspace.getWorkspaceFolder(document.uri)?.uri.fsPath
            ?? workspaceFolderPaths[0];
        const executablePath = resolveBladeExecutable(
            vscode.workspace.getConfiguration("blade", document.uri).get<string | null>("path"),
            {
                cwd: workingDirectory,
                env: process.env,
                file: documentPath,
                workspaceFolder: workspaceFolderPath,
            });

        const completion = await this.refreshRunner.run(() =>
            startBladeCompilation({
                cwd: workingDirectory,
                executablePath,
                sourceText: document.getText(),
            }));

        if (!completion.accepted || this.disposed)
            return;

        switch (completion.value.kind) {
            case "success":
                this.panel.webview.html = renderAssemblyHtml(completion.value.assembly);
                break;

            case "diagnostic-error":
            case "execution-error":
                void vscode.window.showErrorMessage(completion.value.message);
                break;

            case "cancelled":
                break;
        }
    }

    private clearPendingRefresh(): void {
        if (this.refreshTimer !== undefined) {
            clearTimeout(this.refreshTimer);
            this.refreshTimer = undefined;
        }
    }

    private disposeCore(shouldDisposePanel: boolean): void {
        if (this.disposed)
            return;

        this.disposed = true;
        this.clearPendingRefresh();
        this.refreshRunner.cancel();

        if (shouldDisposePanel)
            this.panel.dispose();

        for (const disposable of this.disposables)
            disposable.dispose();

        this.onDisposed();
    }
}

function resolveTargetEditor(resource?: vscode.Uri): vscode.TextEditor | undefined {
    if (resource !== undefined) {
        const matchingVisibleEditor = vscode.window.visibleTextEditors.find((editor) => sameUri(editor.document.uri, resource));
        if (matchingVisibleEditor !== undefined)
            return matchingVisibleEditor;
    }

    return vscode.window.activeTextEditor;
}

function findOpenDocument(targetUri: vscode.Uri): vscode.TextDocument | undefined {
    return vscode.workspace.textDocuments.find((document) => sameUri(document.uri, targetUri));
}

function sameUri(left: vscode.Uri, right: vscode.Uri): boolean {
    return left.toString() === right.toString();
}

function buildPreviewTitle(documentUri: vscode.Uri): string {
    const basename = path.posix.basename(documentUri.path);
    const name = basename.length > 0 ? basename : documentUri.toString();
    return `Blade Preview: ${name}`;
}
