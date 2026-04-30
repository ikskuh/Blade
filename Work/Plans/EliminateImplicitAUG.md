## Plan: Eliminate Implicit Long Immediates

The remaining implicit AUGS/AUGD behavior is concentrated in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/FinalAssemblyWriter.cs`: both `FormatSymbolOperand(...)` overloads still try to route image-start addresses, immediate `StoragePlace` symbols, LUT virtual-address aliases, and symbol+offset expressions through the deleted `useLongImmediate` path, which is what previously emitted `##...`. The correct fix is not to reintroduce a formatter flag, but to move every oversized symbolic immediate onto an implicit constant register before final text emission, then assert that final assembly text never contains `##`.

**Steps**
1. Repair the responsibility boundary in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/FinalAssemblyWriter.cs`. Remove the stale `useLongImmediate` callers and make the writer a pure formatter again: instruction operands should only ever print direct `#...` immediates or register operands, never decide whether a symbolic operand needs long-immediate treatment. This is the root seam and blocks the rest.
2. Extend the ASM model to represent shared constant-register contents for symbolic addresses, not only raw `uint` literals. The current `AsmSharedConstantSymbol(ImageDescriptor image, uint value)` in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmModel.cs` is sufficient for numeric immediates but cannot represent layout-dependent values such as `g_x`, `g_hub_value + 4`, `g_lut_value - $200 + 1`, or an `AsmImageStartSymbol` physical address. Introduce a typed constant payload/key for these address expressions so equality/deduplication stays structural and image-scoped rather than stringly typed.
3. Broaden `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmLegalizer.cs` to legalize oversized symbolic immediates into shared constant registers using that new payload type. Keep the existing numeric path for true literal immediates. Add a second legalization branch for immediate `AsmSymbolOperand` cases that currently relied on `##`, including:
   - immediate `StoragePlace` operands used as addresses
   - symbol+offset expressions
   - LUT virtual-address aliases outside the `RDLUT`/`WRLUT` short-immediate special case
   - `AsmImageStartSymbol` immediates
   Because legalization runs before COG resource planning in `/home/felix/projects/nerdgruppe/blade/Blade/IR/CodegenPipeline.cs`, the new shared constants must stay symbolic until final emission instead of requiring resolved numeric addresses at legalization time.
4. Teach final data emission in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/FinalAssemblyWriter.cs` how to serialize the new symbolic constant payloads inside constant-register definitions. Reuse typed operands/expressions rather than reintroducing text-only formatting state. `FormatDataOperand(...)` currently ignores symbol offsets, so either broaden it to honor the new symbolic constant form directly or route constant initializers through a dedicated expression formatter that can emit `label`, `(label + 4)`, LUT-address aliases, and image-start physical expressions for data definitions only.
5. Add a hard assertion in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/FinalAssemblyWriter.cs` immediately after `FinalAssemblyComposer.Compose(...)` that the produced file text does not contain `##` anywhere. This should be a compiler bug assertion, not a recoverable diagnostic, because after the refactor any surviving `##` means a missed legalization path.
6. Update tests to prove the new contract instead of the old syntax. `/home/felix/projects/nerdgruppe/blade/Blade.Tests/IrPipelineTests.cs` currently asserts raw `##g_x`, `##g_param`, `##g_base`, and `##(symbol +/- offset)` text; rewrite those expectations to assert the assembly uses constant-register materialization and globally `Does.Not.Contain("##")`. Add focused coverage in `/home/felix/projects/nerdgruppe/blade/Blade.Tests/AsmLegalizerTests.cs` for the new symbolic shared-constant legalization path, while keeping the existing explicit `AUGS`/`AUGD` tests because explicit prefixes are still a legal backend mechanism when an operand cannot be constantized.
7. Validate in two tiers: first with targeted unit coverage for the new symbolic constantization cases, then with `just regressions`. Do not run `just accept-changes` unless explicitly requested.

**Relevant files**
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/FinalAssemblyWriter.cs` — current compiler-side implicit `##` emission site; stale `useLongImmediate` callers, data/value formatting, and the final no-`##` assertion belong here.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmLegalizer.cs` — expand legalization from numeric-only large immediates to symbolic address/immediate constantization.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmModel.cs` — introduce a typed shared-constant payload/key that can represent symbolic address expressions, not just raw `uint` values.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/CodegenPipeline.cs` — reference for the ordering constraint: legalization happens before resource planning, so the new constant-register abstraction must survive unresolved until final emission.
- `/home/felix/projects/nerdgruppe/blade/Blade.Tests/IrPipelineTests.cs` — current end-to-end `##` assertions for address-of globals/parameters and immediate symbol offsets need to flip to the new contract.
- `/home/felix/projects/nerdgruppe/blade/Blade.Tests/AsmLegalizerTests.cs` — add direct tests for symbolic constant-register legalization without weakening explicit AUG coverage.

**Verification**
1. Run the focused test slice covering `/home/felix/projects/nerdgruppe/blade/Blade.Tests/AsmLegalizerTests.cs` and the affected `/home/felix/projects/nerdgruppe/blade/Blade.Tests/IrPipelineTests.cs` cases, confirming both the new symbolic constantization behavior and `Does.Not.Contain("##")`.
2. Run a targeted build if needed to catch the current stale-call-site compile errors in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/FinalAssemblyWriter.cs` before widening scope.
3. Run `just regressions` as the repo-level behavioral gate.

**Decisions**
- Included scope: compiler-side elimination of implicit long-immediate PASM syntax and replacement with implicit constant-register materialization.
- Excluded scope: removal of explicit `AUGS`/`AUGD` legalization support for true literals that still require prefix instructions.
- Verified inventory: the only remaining compiler source path that emits implicit long immediates is `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/FinalAssemblyWriter.cs`; the other `##` occurrences are test expectations in `/home/felix/projects/nerdgruppe/blade/Blade.Tests/IrPipelineTests.cs`.
- Recommended architecture: solve layout-dependent address constantization with typed symbolic shared constants emitted through the existing constant-register storage mechanism, not with a post-layout text rewrite and not by reintroducing a writer-level `useLongImmediate` switch.
