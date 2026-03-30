import * as vscode from "vscode";
import { BladePreviewController } from "./previewController";

export function activate(context: vscode.ExtensionContext): void {
    const previewController = new BladePreviewController();
    context.subscriptions.push(
        previewController,
        vscode.commands.registerCommand("blade.openPreviewToSide", (resource?: vscode.Uri) => {
            previewController.openPreviewToSide(resource);
        }));
}

export function deactivate(): void {
}
