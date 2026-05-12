---
# blade-w6my
title: 'Design Issue: `yieldto` from module constructor'
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

In the original design, `yieldto` was only allowed on the top-level code.

With the introduction of modules, `yieldto` could now be called in the magic top-level constructor code of a module.

This is an issue, as this implies calling `yieldto` with a potential non-zero stack level, and any call to `yieldto` cannot return.

Goal is to come up with a better first-class coroutine support that works well with nested function calls as `yieldto` is the wrong keyword for kicking off a "coroutine process".

Also consider if coroutines should be able to return back to the caller, which would technically be possible by any coroutine to execute a `RET`.

Maybe treating a "coroutine process" as a function which can return, but internally jump between routines makes sense?
