# Task layout visibility model
- Represent tasks as `TaskSymbol : LayoutSymbol` rather than as a parallel symbol kind.
- Declare `TaskSymbol` in global scope for duplicate checking and internal self-reference, but hide it from normal external name resolution unless the binder is currently inside that same task's implicit layout.
- Exclude non-exported task layouts from `CreateExportedSymbols()` so imported modules do not expose task-private layouts.
- Task helper functions bind with the task's implicit layout active, but their parent scope intentionally excludes the task startup parameter; they only see their own parameters plus the implicit layout members.