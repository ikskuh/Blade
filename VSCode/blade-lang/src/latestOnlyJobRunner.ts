export interface CancellableJob<T> {
    readonly promise: Promise<T>;
    cancel(): void;
}

export interface JobCompletion<T> {
    readonly accepted: boolean;
    readonly value: T;
}

export class LatestOnlyJobRunner<T> {
    private activeGeneration = 0;
    private activeJob: CancellableJob<T> | undefined;

    public run(factory: (generation: number) => CancellableJob<T>): Promise<JobCompletion<T>> {
        this.activeGeneration += 1;
        const generation = this.activeGeneration;

        this.activeJob?.cancel();

        const job = factory(generation);
        this.activeJob = job;

        return job.promise.then((value) => {
            const accepted = this.activeJob === job && this.activeGeneration === generation;
            if (accepted)
                this.activeJob = undefined;

            return { accepted, value };
        });
    }

    public cancel(): void {
        this.activeGeneration += 1;
        this.activeJob?.cancel();
        this.activeJob = undefined;
    }
}
