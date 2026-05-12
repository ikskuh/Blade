---
# blade-n5vm
title: 'REVIEW-C5: Guard non-IO output path failures in compiler output writers'
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:39:06Z
updated_at: 2026-05-12T21:39:06Z
parent: blade-6ks1
---

Imported from TASKS.md.

`StdioOutputWriter` and `JsonOutputWriter` only normalize `IOException` and `UnauthorizedAccessException` when writing `--output` or `--dump-dir` targets.

## Todo
- [ ] Handle other expected path-format failures such as `ArgumentException` and `NotSupportedException`, or pre-validate the paths before opening them
- [ ] Keep output-path problems as regular user-facing CLI errors instead of process-terminating exceptions
- [ ] Cover both text and JSON output paths in tests
