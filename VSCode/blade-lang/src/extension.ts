import * as vscode from "vscode";
import { BladePreviewController } from "./previewController";

export function activate(context: vscode.ExtensionContext): void {
    const previewController = new BladePreviewController();
    context.subscriptions.push(
        previewController,
        vscode.commands.registerCommand("blade.openDiagnosticLocation", async (location?: { file?: string; line?: number }) => {
            if (location?.file === undefined)
                return;

            const targetUri = vscode.Uri.file(location.file);
            const document = await vscode.workspace.openTextDocument(targetUri);
            const editor = await vscode.window.showTextDocument(document, {
                preview: false,
            });
            const lineIndex = Math.max((location.line ?? 1) - 1, 0);
            const position = new vscode.Position(lineIndex, 0);
            editor.selection = new vscode.Selection(position, position);
            editor.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenter);
        }),
        vscode.commands.registerCommand("blade.openPreviewToSide", (resource?: vscode.Uri) => {
            previewController.openPreviewToSide(resource);
        }));
}

export function deactivate(): void {
}
