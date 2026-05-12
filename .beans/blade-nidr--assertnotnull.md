---
# blade-nidr
title: Assert.NotNull
status: todo
type: task
priority: low
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

AsmOperand yieldStateDestination = ctx.Tier == CallingConventionTier.Coroutine
                && ctx.CoroutineCallingConvention.TryGetValue(ctx.Function.Name, out CoroutineCallingConventionInfo? sourceInfo)
                && sourceInfo is not null
                ? new AsmPlaceOperand(sourceInfo.StatePlace)
                : ctx.TopLevelYieldStatePlace is not null
                    ? new AsmPlaceOperand(ctx.TopLevelYieldStatePlace)
                    : Assert.UnreachableValue<AsmOperand>();
