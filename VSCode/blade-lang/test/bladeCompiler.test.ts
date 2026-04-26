import { PassThrough } from "node:stream";
import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import {
    interpretBladeCompilerOutput,
    resolveBladeExecutable,
    selectBladeWorkingDirectory,
    startBladeCompilation,
    type SpawnProcess,
    type SpawnedProcess,
} from "../src/bladeCompiler";

test("resolveBladeExecutable falls back to blade when setting is unset or blank", () => {
    assert.equal(resolveBladeExecutable(undefined), "blade");
    assert.equal(resolveBladeExecutable(null), "blade");
    assert.equal(resolveBladeExecutable(""), "blade");
    assert.equal(resolveBladeExecutable("   "), "blade");
});

test("resolveBladeExecutable preserves an explicit path", () => {
    assert.equal(resolveBladeExecutable("/opt/blade/bin/blade"), "/opt/blade/bin/blade");
});

test("resolveBladeExecutable expands workspace and file variables", () => {
    const resolvedPath = resolveBladeExecutable(
        "${workspaceFolder}/tools/${fileBasename}",
        {
            file: "/workspace/project/toolchains/blade",
            workspaceFolder: "/workspace/project",
        });

    assert.equal(resolvedPath, "/workspace/project/tools/blade");
});

test("resolveBladeExecutable expands environment variables and leaves unknown variables intact", () => {
    const resolvedPath = resolveBladeExecutable(
        "${env:BLADE_ROOT}/bin/blade-${unknown}",
        {
            env: {
                BLADE_ROOT: "/opt/blade",
            },
        });

    assert.equal(resolvedPath, "/opt/blade/bin/blade-${unknown}");
});

test("selectBladeWorkingDirectory prefers the containing workspace folder", () => {
    const cwd = selectBladeWorkingDirectory(
        "/workspace/project/src/main.blade",
        ["/workspace", "/workspace/project"]);

    assert.equal(cwd, "/workspace/project");
});

test("selectBladeWorkingDirectory falls back to the document directory when no workspace folder matches", () => {
    const cwd = selectBladeWorkingDirectory("/tmp/scratch/sample.blade", ["/workspace/project"]);
    assert.equal(cwd, "/tmp/scratch");
});

test("selectBladeWorkingDirectory uses the first workspace folder for untitled documents", () => {
    const cwd = selectBladeWorkingDirectory(undefined, ["/workspace/project", "/workspace/other"]);
    assert.equal(cwd, "/workspace/project");
});

test("interpretBladeCompilerOutput returns the full successful JSON report", () => {
    const outcome = interpretBladeCompilerOutput(
        JSON.stringify({
            success: true,
            diagnostics: [],
            dumps: [
                {
                    id: "bound",
                    title: "Bound",
                    fileName: "00_bound.ir",
                    content: "Program",
                },
            ],
            metrics: {
                token_count: 2,
            },
            result: "org 0\nmov r0, #1\n",
        }),
        "",
        0,
        null);

    assert.deepEqual(outcome, {
        kind: "report",
        report: {
            diagnostics: [],
            dumps: [
                {
                    id: "bound",
                    title: "Bound",
                    fileName: "00_bound.ir",
                    content: "Program",
                },
            ],
            metrics: {
                token_count: 2,
            },
            result: "org 0\nmov r0, #1\n",
            success: true,
        },
    });
});

test("interpretBladeCompilerOutput preserves diagnostics in a failed JSON report", () => {
    const outcome = interpretBladeCompilerOutput(
        JSON.stringify({
            success: false,
            diagnostics: [
                {
                    code: "E0202",
                    line: 4,
                    message: "Expected expression.",
                },
            ],
            dumps: [],
            metrics: {
                token_count: 4,
            },
            result: null,
        }),
        "ignored text diagnostics",
        1,
        null);

    assert.deepEqual(outcome, {
        kind: "report",
        report: {
            diagnostics: [
                {
                    code: "E0202",
                    file: undefined,
                    line: 4,
                    message: "Expected expression.",
                },
            ],
            dumps: [],
            metrics: {
                token_count: 4,
            },
            result: null,
            success: false,
        },
    });
});

test("interpretBladeCompilerOutput reports invalid JSON output as an execution error", () => {
    const outcome = interpretBladeCompilerOutput("not json", "spawn stderr", 1, null);

    assert.deepEqual(outcome, {
        kind: "execution-error",
        message: "Blade compiler returned invalid JSON output. spawn stderr",
    });
});

test("startBladeCompilation requests json and all dumps", async () => {
    let capturedArgs: readonly string[] | undefined;

    const fakeSpawn: SpawnProcess = (_command, args) => {
        capturedArgs = args;

        const child = new FakeChildProcess();
        queueMicrotask(() => {
            child.emit("error", new Error("spawn ENOENT"));
        });
        return child;
    };

    const activeCompilation = startBladeCompilation(
        {
            cwd: "/workspace/project",
            executablePath: "blade",
            sourceText: "cog var x: u32 = 1;",
        },
        fakeSpawn);

    const outcome = await activeCompilation.promise;
    assert.deepEqual(outcome, {
        kind: "execution-error",
        message: "Blade compiler invocation failed: spawn ENOENT",
    });
    assert.deepEqual(capturedArgs, ["--json", "--dump-all", "-"]);
});

class FakeChildProcess extends EventEmitter implements SpawnedProcess {
    public readonly stdin = new PassThrough();
    public readonly stdout = new PassThrough();
    public readonly stderr = new PassThrough();

    public kill(): boolean {
        this.emit("close", null, "SIGTERM");
        return true;
    }
}
