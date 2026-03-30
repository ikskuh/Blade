import test from "node:test";
import assert from "node:assert/strict";
import { LatestOnlyJobRunner, type CancellableJob } from "../src/latestOnlyJobRunner";

test("LatestOnlyJobRunner accepts only the newest completion and cancels the previous job", async () => {
    const runner = new LatestOnlyJobRunner<string>();

    let resolveFirst: ((value: string) => void) | undefined;
    let resolveSecond: ((value: string) => void) | undefined;
    let firstCancelled = false;

    const firstCompletionPromise = runner.run(() => createJob((resolve) => {
        resolveFirst = resolve;
    }, () => {
        firstCancelled = true;
    }));

    const secondCompletionPromise = runner.run(() => createJob((resolve) => {
        resolveSecond = resolve;
    }));

    assert.equal(firstCancelled, true);

    resolveFirst?.("old");
    resolveSecond?.("new");

    const firstCompletion = await firstCompletionPromise;
    const secondCompletion = await secondCompletionPromise;

    assert.deepEqual(firstCompletion, {
        accepted: false,
        value: "old",
    });
    assert.deepEqual(secondCompletion, {
        accepted: true,
        value: "new",
    });
});

function createJob<T>(
    start: (resolve: (value: T) => void) => void,
    onCancel?: () => void): CancellableJob<T> {
    return {
        promise: new Promise<T>((resolve) => {
            start(resolve);
        }),
        cancel: () => {
            onCancel?.();
        },
    };
}
