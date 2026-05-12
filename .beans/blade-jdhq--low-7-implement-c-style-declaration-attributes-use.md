---
# blade-jdhq
title: 'LOW-7: Implement C#-style declaration attributes ([Used], [LinkName])'
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:39:01Z
updated_at: 2026-05-12T21:39:01Z
parent: blade-jftu
---

Imported from TASKS.md.

Design note in `CallGraphAnalyzer.cs` (line 9). This is a planned language feature rather than a pure lowering gap.

## Todo
- [ ] Implement `[Used]` to mark a function or variable as reachable and prevent dead-code elimination without direct callers
- [ ] Implement `[LinkName("_start")]` to set the emitted assembly label name for linker or external interop
- [ ] Parse attributes in the syntax layer
- [ ] Store attributes in the bound tree
- [ ] Propagate attributes through MIR and LIR to call-graph analysis and codegen
